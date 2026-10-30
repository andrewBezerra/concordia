using Concordia.Hook.Config;

namespace Concordia.Hook.Review;

internal sealed record LensDecision(string Lens, IReadOnlyList<string> Reasons);

internal sealed record TriageResult(IReadOnlyList<LensDecision> Lenses, bool EscalateAll, IReadOnlyList<string> EscalationReasons);

/// <summary>
/// Deterministic, zero-token choice of review lenses. The reviewer agent may still escalate
/// to another lens on its own; this only sets the starting point.
/// </summary>
internal static class LensTriage
{
    private const int MaxReasonsPerLens = 5;

    public static TriageResult Evaluate(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, List<string>> addedLinesByFile,
        int changedLines,
        int projects,
        Settings settings)
    {
        var escalation = new List<string>();
        if (changedLines > settings.MaxDiffLines) escalation.Add($"{changedLines} changed lines (limit {settings.MaxDiffLines})");
        if (projects > settings.MaxProjects) escalation.Add($"{projects} projects touched (limit {settings.MaxProjects})");
        var escalateAll = escalation.Count > 0;

        var decisions = new List<LensDecision>();
        foreach (var lens in settings.Lenses)
        {
            var reasons = new List<string>();
            if (escalateAll) reasons.Add("large change: every lens runs");

            foreach (var file in files)
            {
                var rule = lens.Paths.Match(file);
                if (rule is not null) reasons.Add($"{file} matches {rule}");
            }

            foreach (var (file, added) in addedLinesByFile)
            {
                foreach (var signal in lens.Signals)
                {
                    if (added.Any(line => line.Contains(signal, StringComparison.Ordinal))) reasons.Add($"{file} adds '{signal}'");
                }
            }

            if (reasons.Count > 0) decisions.Add(new LensDecision(lens.Name, reasons.Distinct().Take(MaxReasonsPerLens).ToList()));
        }

        return new TriageResult(decisions, escalateAll, escalation);
    }
}

internal sealed class ReviewPackSummary
{
    public string Pack { get; set; } = "";
    public string Base { get; set; } = "";
    public int Files { get; set; }
    public int ChangedLines { get; set; }
    public int Projects { get; set; }
    public bool EscalateAll { get; set; }
    public List<string> EscalationReasons { get; set; } = [];
    public List<LensHit> Lenses { get; set; } = [];
}

internal sealed class LensHit
{
    public string Lens { get; set; } = "";
    public List<string> Reasons { get; set; } = [];
}
