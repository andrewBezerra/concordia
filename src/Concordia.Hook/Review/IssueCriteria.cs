using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concordia.Hook.Review;

/// <summary>The part of <c>gh issue view --json title,body,labels,comments</c> that the extraction reads. Unknown fields are ignored.</summary>
internal sealed class IssueDocument
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public List<IssueComment?>? Comments { get; set; }
}

internal sealed class IssueComment
{
    public string? Body { get; set; }
}

/// <summary>One acceptance criterion. <see cref="Checked"/> is null for a plain bullet, which has no checkbox.</summary>
internal sealed class Criterion
{
    public string Text { get; set; } = "";
    public bool? Checked { get; set; }
}

/// <summary>
/// The result of the extraction. <see cref="Source"/> says where the criteria came from, or <c>none</c> when the
/// issue has none; the caller treats that as "ask the user", never as a failure.
/// </summary>
internal sealed class IssueCriteriaResult
{
    public string? Title { get; set; }
    public string Source { get; set; } = IssueCriteria.NoSource;
    public List<Criterion> Criteria { get; set; } = [];
}

/// <summary>
/// Extracts the acceptance criteria of a GitHub issue, deterministically. The issue text is data: this only
/// reads list items and never interprets what they say.
/// <para>
/// When there is more than one candidate, the first of these wins (an explicit heading is a stronger signal
/// than a checklist, and the issue's own body is stronger than a comment, which anyone can write):
/// <c>body-section</c>, <c>comment-section</c>, <c>body-checklist</c>, <c>comment-checklist</c>.
/// Among comments the newest wins, because a later comment is where a confirmed list is posted.
/// </para>
/// </summary>
internal static partial class IssueCriteria
{
    public const string NoSource = "none";

    public static (IssueCriteriaResult? Result, string? Error) Extract(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "empty input; expected the JSON of 'gh issue view <n> --json title,body,labels,comments'");

        IssueDocument? issue;
        try
        {
            issue = JsonSerializer.Deserialize(json, ConcordiaJson.Default.IssueDocument);
        }
        catch (JsonException ex)
        {
            return (null, $"not valid issue JSON ({ex.Message})");
        }

        if (issue is null) return (null, "the JSON is null, not an issue object");
        if (issue.Body is null) return (null, "'body' is missing or null; ask gh for --json title,body,labels,comments");
        if (issue.Comments is null) return (null, "'comments' is missing or null; ask gh for --json title,body,labels,comments");

        var comments = issue.Comments.Select(c => c?.Body ?? "").Reverse().ToList();

        var found =
            First("body-section", [issue.Body], Section)
            ?? First("comment-section", comments, Section)
            ?? First("body-checklist", [issue.Body], Checklist)
            ?? First("comment-checklist", comments, Checklist);

        return (found is { } f
            ? new IssueCriteriaResult { Title = issue.Title, Source = f.Source, Criteria = f.Items }
            : new IssueCriteriaResult { Title = issue.Title }, null);
    }

    private static (string Source, List<Criterion> Items)? First(string source, IEnumerable<string> texts, Func<string, List<Criterion>> extract)
    {
        foreach (var text in texts)
        {
            var items = extract(text);
            if (items.Count > 0) return (source, items);
        }

        return null;
    }

    /// <summary>Every checkbox item (<c>- [ ] x</c> or <c>- [x] x</c>) outside a code fence.</summary>
    public static List<Criterion> Checklist(string text)
    {
        var items = new List<Criterion>();
        foreach (var line in OutsideFences(text))
        {
            var m = CheckboxItem().Match(line);
            if (m.Success && m.Groups[2].Value.Trim().Length > 0)
                items.Add(new Criterion { Text = m.Groups[2].Value.Trim(), Checked = m.Groups[1].Value != " " });
        }

        return items;
    }

    /// <summary>
    /// The list items under an "Acceptance criteria" (or "Critérios de aceite") heading, up to the next heading.
    /// Only the outermost items count; nested lines and continuation lines are joined to their parent.
    /// </summary>
    public static List<Criterion> Section(string text)
    {
        var items = new List<Criterion>();
        var inSection = false;
        var level = 0;
        var baseIndent = -1;

        foreach (var line in OutsideFences(text))
        {
            if (!inSection)
            {
                level = SectionStart(line);
                inSection = level > 0;
                baseIndent = -1;
                continue;
            }

            var ends = SectionEnd(line);
            if (ends > 0)
            {
                // A heading at the same or a higher level closes the section; a deeper one is part of it.
                if (ends <= level)
                {
                    if (items.Count > 0) break;
                    level = SectionStart(line);
                    inSection = level > 0;
                    baseIndent = -1;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            var item = ListItem().Match(line);
            if (item.Success)
            {
                var indent = IndentOf(item.Groups[1].Value);
                if (baseIndent < 0 || indent <= baseIndent)
                {
                    baseIndent = indent;
                    var body = item.Groups[2].Value.Trim();
                    var box = Checkbox().Match(body);
                    items.Add(box.Success
                        ? new Criterion { Text = box.Groups[2].Value.Trim(), Checked = box.Groups[1].Value != " " }
                        : new Criterion { Text = body });
                    continue;
                }
            }

            // A nested item or a wrapped line: it belongs to the criterion above.
            if (items.Count > 0)
            {
                var more = item.Success ? item.Groups[2].Value.Trim() : line.Trim();
                items[^1].Text = $"{items[^1].Text} {more}";
            }
        }

        return items.Where(i => i.Text.Length > 0).ToList();
    }

    private static int IndentOf(string whitespace) => whitespace.Replace("\t", "    ").Length;

    /// <summary>The level of a heading line that opens an acceptance-criteria section, or 0.</summary>
    private static int SectionStart(string line)
    {
        var heading = MarkdownHeading().Match(line);
        if (heading.Success) return IsCriteriaTitle(heading.Groups[2].Value) ? heading.Groups[1].Length : 0;

        // "**Acceptance criteria**" or "Acceptance criteria:" on a line of its own.
        var plain = PlainTitle().Match(line);
        return plain.Success && IsCriteriaTitle(plain.Groups[1].Value) ? 7 : 0;
    }

    /// <summary>The level of a line that ends a section: a markdown heading, or a bold line of its own (level 7).</summary>
    private static int SectionEnd(string line)
    {
        var heading = MarkdownHeading().Match(line);
        if (heading.Success) return heading.Groups[1].Length;
        return BoldOnlyLine().IsMatch(line) ? 7 : 0;
    }

    private static bool IsCriteriaTitle(string title) =>
        CriteriaTitle().IsMatch(title.Trim().Trim('*', '_').Trim().TrimEnd(':').Trim().Trim('*', '_').Trim());

    /// <summary>The lines that are not inside a fenced code block; the fence lines themselves are dropped.</summary>
    private static IEnumerable<string> OutsideFences(string text)
    {
        string? fence = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Fence().Match(line);
            if (m.Success)
            {
                var marker = m.Groups[1].Value[..3];
                if (fence is null) fence = marker;
                else if (marker == fence) fence = null;
                continue;
            }

            if (fence is null) yield return line;
        }
    }

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex CheckboxItem();

    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$")]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"^(\s*)(?:[-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\s{0,3}(.+?)\s*$")]
    private static partial Regex PlainTitle();

    [GeneratedRegex(@"^\s{0,3}(\*\*|__)[^*_]+\1\s*:?\s*$")]
    private static partial Regex BoldOnlyLine();

    [GeneratedRegex(@"^(acceptance criteria|crit[eé]rios de aceit(e|a[cç][aã]o))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CriteriaTitle();
}
