using System.Globalization;
using System.Text;
using System.Text.Json;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Review;

/// <summary>One reviewer answer as read from disk. <see cref="Text"/> is null when the file could not be read.</summary>
internal sealed record ReviewInput(string Source, string? Text);

internal sealed class ReviewerAnswer
{
    public string? Lens { get; set; }
    public string? Verdict { get; set; }
    public List<ReviewerFinding>? Findings { get; set; }
    public List<ReviewerEscalation>? Escalate { get; set; }
}

internal sealed class ReviewerFinding
{
    public string? File { get; set; }
    public int? Line { get; set; }
    public string? Severity { get; set; }
    public double? Confidence { get; set; }
    public string? Title { get; set; }
    public string? Scenario { get; set; }
    public string? Fix { get; set; }
}

internal sealed class ReviewerEscalation
{
    public string? Lens { get; set; }
    public string? Reason { get; set; }
}

internal sealed record MergedFinding(
    string File, int? Line, string Title, string Severity, double Confidence,
    List<string> Lenses, string? Scenario, string? Fix);

internal sealed record InvalidAnswer(string Source, string Reason);

internal sealed record MergeResult(
    List<MergedFinding> Findings, List<InvalidAnswer> Invalid, List<ReviewerEscalation> Escalations, int Reviewers)
{
    /// <summary>
    /// <c>approve</c> only when every answer was readable and nothing blocking was found.
    /// An unreadable answer yields <c>incomplete</c>: a reviewer that did not speak cannot count as a yes.
    /// </summary>
    public string Verdict =>
        Findings.Any(ReviewMerger.IsBlocking) ? "changes_requested"
        : Invalid.Count > 0 ? "incomplete"
        : "approve";

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"Review merge: {Reviewers} answer(s), {Findings.Count} finding(s), {Invalid.Count} invalid");

        foreach (var f in Findings)
        {
            var location = f.Line is { } line ? $"{f.File}:{line}" : f.File;
            var mark = ReviewMerger.IsBlocking(f) ? "*" : " ";
            text.AppendLine($"{mark} [{f.Severity} {f.Confidence.ToString("0.##", CultureInfo.InvariantCulture)}] {location} {f.Title} ({string.Join(", ", f.Lenses)})");
            if (!string.IsNullOrWhiteSpace(f.Scenario)) text.AppendLine($"    scenario: {f.Scenario}");
            if (!string.IsNullOrWhiteSpace(f.Fix)) text.AppendLine($"    fix: {f.Fix}");
        }

        foreach (var bad in Invalid) text.AppendLine($"INVALID {bad.Source}: {bad.Reason}");
        foreach (var e in Escalations) text.AppendLine($"escalate: {e.Lens} ({e.Reason})");

        var blocking = Findings.Count(ReviewMerger.IsBlocking);
        text.Append($"Verdict: {Verdict}");
        if (blocking > 0) text.Append($" ({blocking} blocking, marked *)");
        if (Invalid.Count > 0) text.Append($" ({Invalid.Count} invalid answer(s) not reviewed; rerun them)");
        return text.ToString();
    }
}

/// <summary>Merges the JSON answers of the reviewer agents into one deduplicated, sorted list and a verdict.</summary>
internal static class ReviewMerger
{
    /// <summary>Two findings in the same file with the same title are duplicates when their lines differ by at most this much.</summary>
    public const int NearbyLines = 3;

    public const double BlockingConfidence = 0.7;

    private static readonly string[] Severities = ["low", "medium", "high"];

    public static bool IsBlocking(MergedFinding f) =>
        f.Confidence >= BlockingConfidence && SeverityRank(f.Severity) >= SeverityRank("medium");

    public static MergeResult Merge(IReadOnlyList<ReviewInput> inputs)
    {
        var raw = new List<(MergedFinding Finding, string Title)>();
        var invalid = new List<InvalidAnswer>();
        var escalations = new List<ReviewerEscalation>();

        foreach (var input in inputs)
        {
            var (answer, error) = Parse(input.Text);
            if (answer is null)
            {
                invalid.Add(new InvalidAnswer(input.Source, error!));
                continue;
            }

            var lens = string.IsNullOrWhiteSpace(answer.Lens) ? "unknown" : answer.Lens.Trim();
            foreach (var f in answer.Findings ?? [])
            {
                var file = GlobSet.Normalize(f.File!.Trim());
                raw.Add((new MergedFinding(file, f.Line, f.Title!.Trim(), f.Severity!.Trim().ToLowerInvariant(), f.Confidence!.Value, [lens], f.Scenario, f.Fix), TitleKey(f.Title!)));
            }

            foreach (var e in answer.Escalate ?? [])
            {
                if (!string.IsNullOrWhiteSpace(e.Lens) && !escalations.Any(x => string.Equals(x.Lens, e.Lens, StringComparison.OrdinalIgnoreCase)))
                    escalations.Add(e);
            }
        }

        // Ordering by file, title and line first makes the clustering independent of the order of the inputs.
        var ordered = raw
            .OrderBy(r => r.Finding.File, StringComparer.Ordinal)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ThenBy(r => r.Finding.Line ?? int.MaxValue);

        var merged = new List<(MergedFinding Finding, string Title)>();
        foreach (var item in ordered)
        {
            var index = merged.FindIndex(m => IsDuplicate(m, item));
            if (index >= 0) merged[index] = Combine(merged[index], item);
            else merged.Add(item);
        }

        var findings = merged.Select(m => m.Finding)
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? int.MaxValue)
            .ToList();

        return new MergeResult(findings, invalid, escalations, inputs.Count);
    }

    private static bool IsDuplicate((MergedFinding Finding, string Title) kept, (MergedFinding Finding, string Title) candidate) =>
        kept.Finding.File == candidate.Finding.File
        && kept.Title == candidate.Title
        && (kept.Finding.Line, candidate.Finding.Line) switch
        {
            (int a, int b) => Math.Abs(a - b) <= NearbyLines,
            (null, null) => true,
            _ => false,
        };

    private static (MergedFinding Finding, string Title) Combine((MergedFinding Finding, string Title) kept, (MergedFinding Finding, string Title) other)
    {
        var a = kept.Finding;
        var b = other.Finding;
        var stronger = SeverityRank(b.Severity) > SeverityRank(a.Severity) ? b : a;
        return (a with
        {
            Severity = stronger.Severity,
            Confidence = Math.Max(a.Confidence, b.Confidence),
            Lenses = a.Lenses.Union(b.Lenses).ToList(),
            Scenario = stronger.Scenario ?? a.Scenario ?? b.Scenario,
            Fix = stronger.Fix ?? a.Fix ?? b.Fix,
        }, kept.Title);
    }

    private static int SeverityRank(string severity) => Array.IndexOf(Severities, severity);

    private static string TitleKey(string title) =>
        string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static (ReviewerAnswer? Answer, string? Error) Parse(string? text)
    {
        if (text is null) return (null, "could not be read");
        text = StripFence(text.Trim());
        if (text.Length == 0) return (null, "empty answer");

        ReviewerAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize(text, ConcordiaJson.Default.ReviewerAnswer);
        }
        catch (JsonException ex)
        {
            return (null, $"not valid reviewer JSON ({ex.Message})");
        }

        if (answer is null) return (null, "answer is null");

        // A reviewer that did not state its result did not review: {} must not count as a yes.
        if (string.IsNullOrWhiteSpace(answer.Lens)) return (null, "lens is missing");
        if (answer.Verdict is not ("approve" or "changes_requested")) return (null, "verdict must be approve or changes_requested");
        if (answer.Findings is null) return (null, "findings is missing");
        if (answer.Verdict == "changes_requested" && answer.Findings.Count == 0) return (null, "changes_requested with no findings");

        for (var i = 0; i < (answer.Escalate?.Count ?? 0); i++)
        {
            if (answer.Escalate![i] is null) return (null, $"escalate {i + 1} is null");
        }

        for (var i = 0; i < answer.Findings.Count; i++)
        {
            var f = answer.Findings[i];
            if (f is null) return (null, $"finding {i + 1} is null");
            var problem =
                string.IsNullOrWhiteSpace(f.File) ? "file is missing"
                : string.IsNullOrWhiteSpace(f.Title) ? "title is missing"
                : f.Severity is null || SeverityRank(f.Severity.Trim().ToLowerInvariant()) < 0 ? "severity must be low, medium or high"
                : f.Confidence is not { } c || c < 0 || c > 1 ? "confidence must be a number from 0 to 1"
                : null;
            if (problem is not null) return (null, $"finding {i + 1}: {problem}");
        }

        return (answer, null);
    }

    /// <summary>Reviewers are told to answer with JSON only, but a model often wraps it in a markdown fence.</summary>
    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstLineEnd = text.IndexOf('\n');
        var last = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd < 0 || last <= firstLineEnd ? text : text[(firstLineEnd + 1)..last].Trim();
    }
}
