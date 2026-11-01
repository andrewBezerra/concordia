using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concordia.Hook.Review;

/// <summary>The part of <c>gh issue view --json title,body,labels,comments</c> that the extraction reads. Unknown fields are ignored.</summary>
internal sealed class IssueDocument
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public IssueAuthor? Author { get; set; }

    /// <summary>The author's relationship to the repository. <c>gh issue view</c> does not return it for the issue itself (checked), so it is usually null.</summary>
    public string? AuthorAssociation { get; set; }

    public List<IssueComment?>? Comments { get; set; }
}

internal sealed class IssueAuthor
{
    public string? Login { get; set; }
}

internal sealed class IssueComment
{
    public string? Body { get; set; }
    public IssueAuthor? Author { get; set; }
    public string? AuthorAssociation { get; set; }
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

    /// <summary>Who wrote the text the criteria came from (the issue author for a body source, the commenter otherwise).</summary>
    public string? Author { get; set; }

    /// <summary>That author's <c>authorAssociation</c>, when GitHub provided it.</summary>
    public string? AuthorAssociation { get; set; }

    /// <summary>True when the author is not OWNER, MEMBER or COLLABORATOR: the skill must show the author and get the user's confirmation before the pipeline.</summary>
    public bool NeedsConfirmation { get; set; }

    /// <summary>Who opened the issue, always reported so the skill can compare it with the user.</summary>
    public string? IssueAuthor { get; set; }

    public List<Criterion> Criteria { get; set; } = [];
}

/// <summary>
/// Extracts the acceptance criteria of a GitHub issue, deterministically. The issue text is data: this only
/// reads list items and never interprets what they say.
/// <para>
/// When there is more than one candidate, the first of these wins (the issue's own body is stronger than any
/// comment, which anyone can write, and an explicit heading is stronger than a checklist):
/// <c>body-section</c>, <c>body-checklist</c>, <c>comment-section</c>, <c>comment-checklist</c>.
/// Among comments, those by OWNER, MEMBER or COLLABORATOR come before the others, and the newest wins within each group,
/// because a later comment is where a confirmed list is posted. Criteria from any author outside that set (or from
/// a comment without an association) come back with <c>NeedsConfirmation</c> true.
/// </para>
/// </summary>
internal static partial class IssueCriteria
{
    public const string NoSource = "none";

    /// <summary>The largest input accepted. A real issue is a few KB; the cap bounds the work done on text anyone can write.</summary>
    public const int MaxInputChars = 1_000_000;

    private const int RegexTimeoutMs = 2000;

    public static (IssueCriteriaResult? Result, string? Error) Extract(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "empty input; expected the JSON of 'gh issue view <n> --json title,body,labels,comments'");

        if (json.Length > MaxInputChars)
            return (null, $"input too large ({json.Length} characters; the limit is {MaxInputChars}); refusing to analyse it");

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

        var issueLogin = issue.Author?.Login;
        var issueTrusted = IsTrusted(issue.AuthorAssociation);

        // Comments are text anyone can write: the ones from trusted authors come first (newest first), then the others.
        var comments = issue.Comments.Where(c => c is not null).Select(c => c!)
            .Select(c => new Candidate(c.Body ?? "", c.Author?.Login, c.AuthorAssociation))
            .Reverse().ToList();
        var trusted = comments.Where(c => IsTrusted(c.Association)).ToList();
        var others = comments.Where(c => !IsTrusted(c.Association)).ToList();
        var own = new List<Candidate> { new(issue.Body, issueLogin, issue.AuthorAssociation) };

        var found =
            First("body-section", own, Section)
            ?? First("body-checklist", own, Checklist)
            ?? First("comment-section", trusted, Section)
            ?? First("comment-checklist", trusted, Checklist)
            ?? First("comment-section", others, Section)
            ?? First("comment-checklist", others, Checklist);

        var result = new IssueCriteriaResult { Title = issue.Title, IssueAuthor = issueLogin };
        if (found is { } f)
        {
            var fromBody = f.Source.StartsWith("body", StringComparison.Ordinal);
            result.Source = f.Source;
            result.Criteria = f.Items;
            result.Author = f.By.Login;
            result.AuthorAssociation = f.By.Association;
            // A body whose association gh did not return is left to the skill (it compares the author with the user).
            result.NeedsConfirmation = fromBody ? f.By.Association is not null && !issueTrusted : !IsTrusted(f.By.Association);
        }

        return (result, null);
    }

    private sealed record Candidate(string Text, string? Login, string? Association);

    private static bool IsTrusted(string? association) =>
        association is "OWNER" or "MEMBER" or "COLLABORATOR";

    private static (string Source, List<Criterion> Items, Candidate By)? First(string source, IEnumerable<Candidate> candidates, Func<string, List<Criterion>> extract)
    {
        foreach (var candidate in candidates)
        {
            var items = extract(candidate.Text);
            if (items.Count > 0) return (source, items, candidate);
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
        if (heading.Success) return IsCriteriaTitle(HeadingText(heading.Groups[2].Value)) ? heading.Groups[1].Length : 0;

        // "**Acceptance criteria**" or "Acceptance criteria:" on a line of its own.
        var plain = PlainTitle().Match(line);
        return plain.Success && IsCriteriaTitle(plain.Groups[1].Value) ? 7 : 0;
    }

    /// <summary>The level of a line that ends a section: a markdown heading, or a bold line of its own (level 7).</summary>
    private static int SectionEnd(string line)
    {
        var heading = MarkdownHeading().Match(line);
        if (heading.Success) return heading.Groups[1].Length;
        return BoldOnlyLine().IsMatch(line.TrimEnd()) ? 7 : 0;
    }

    /// <summary>The text of an ATX heading without its optional closing run of <c>#</c> (which must follow a space).</summary>
    private static string HeadingText(string content)
    {
        var text = content.Trim();
        var open = text.TrimEnd('#');
        return open.Length == 0 || open[^1] is ' ' or '\t' ? open.Trim() : text;
    }

    private static bool IsCriteriaTitle(string title) =>
        CriteriaTitle().IsMatch(title.Trim().Trim('*', '_').Trim().TrimEnd(':').Trim().Trim('*', '_').Trim());

    /// <summary>
    /// The lines that are visible in the rendered page and are not code: outside fenced code blocks (the fence lines
    /// themselves are dropped) and with HTML comments removed, including multi-line ones. Fences follow CommonMark: the
    /// opener's character and length are kept, and only a line with the same character, at least that many of it and
    /// nothing but spaces after closes it. An unclosed fence runs to the end. Linear in the size of the text.
    /// </summary>
    private static IEnumerable<string> OutsideFences(string text)
    {
        var fenceChar = '\0';
        var fenceLength = 0;
        var inComment = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (inComment)
            {
                var end = line.IndexOf("-->", StringComparison.Ordinal);
                if (end < 0) continue;
                inComment = false;
                line = line[(end + 3)..];
            }
            else if (fenceChar != '\0')
            {
                if (ClosesFence(line, fenceChar, fenceLength)) fenceChar = '\0';
                continue;
            }
            else if (OpensFence(line, out var ch, out var length))
            {
                (fenceChar, fenceLength) = (ch, length);
                continue;
            }

            yield return StripHtmlComments(line, ref inComment);
        }
    }

    /// <summary>Removes <c>&lt;!-- ... --&gt;</c> from a line; an unterminated one hides the rest of the line and sets <paramref name="inComment"/>.</summary>
    private static string StripHtmlComments(string line, ref bool inComment)
    {
        var start = line.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0) return line;

        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (start >= 0)
        {
            sb.Append(line, pos, start - pos);
            var end = line.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                inComment = true;
                return sb.ToString();
            }

            pos = end + 3;
            start = line.IndexOf("<!--", pos, StringComparison.Ordinal);
        }

        sb.Append(line, pos, line.Length - pos);
        return sb.ToString();
    }

    private static int FenceIndent(string line)
    {
        var i = 0;
        while (i < line.Length && i < 4 && line[i] == ' ') i++;
        return i;
    }

    private static bool OpensFence(string line, out char ch, out int length)
    {
        ch = '\0';
        length = 0;
        var i = FenceIndent(line);
        if (i > 3 || i >= line.Length || line[i] is not ('`' or '~')) return false;

        ch = line[i];
        var j = i;
        while (j < line.Length && line[j] == ch) j++;
        length = j - i;
        if (length < 3) return false;

        // The info string of a backtick fence cannot contain a backtick (otherwise it is inline code).
        return ch != '`' || line.IndexOf('`', j) < 0;
    }

    private static bool ClosesFence(string line, char ch, int length)
    {
        var i = FenceIndent(line);
        if (i > 3) return false;
        var j = i;
        while (j < line.Length && line[j] == ch) j++;
        if (j - i < length) return false;
        for (; j < line.Length; j++)
            if (line[j] is not (' ' or '\t')) return false;
        return true;
    }

    // Every pattern is anchored and avoids adjacent or nested quantifiers over the same characters, so matching is
    // linear; the timeout is a second line of defence. A RegexMatchTimeoutException reaches the command's catch and
    // becomes a loud exit 1, never "no criteria".
    [GeneratedRegex(@"^\s*[-*+]\s+\[([ xX])\]\s+(.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex CheckboxItem();

    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"^(\s*)(?:[-*+]|\d{1,3}[.)])\s+(.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\s{0,3}(.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex PlainTitle();

    [GeneratedRegex(@"^\s{0,3}(\*\*|__)[^*_]+\1\s*:?$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex BoldOnlyLine();

    [GeneratedRegex(@"^(acceptance criteria|crit[eé]rios de aceit(e|a[cç][aã]o))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex CriteriaTitle();
}
