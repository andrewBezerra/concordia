using Concordia.Hook.Infra;

namespace Concordia.Hook.Config;

internal sealed record LensSettings(string Name, GlobSet Paths, IReadOnlyList<string> Signals);

internal sealed record TestGuardSettings(GlobSet Paths, IReadOnlyList<string> Markers, IReadOnlyList<string> Skips)
{
    public bool Enabled => !Paths.IsEmpty && (Markers.Count > 0 || Skips.Count > 0);
}

/// <summary>Effective configuration after merging the preset with the repository file.</summary>
internal sealed record Settings(
    string Stack,
    string? FormatCommand,
    string? BuildCommand,
    string? TestCommand,
    GlobSet Watch,
    GlobSet Protect,
    IReadOnlyList<LensSettings> Lenses,
    int MaxDiffLines,
    int MaxProjects,
    bool GateEnabled,
    int MaxAttempts,
    int MaxOutputLines,
    TimeSpan CommandTimeout,
    TestGuardSettings TestGuard)
{
    public static Settings From(string stack, ConcordiaConfig config)
    {
        var lenses = new List<LensSettings>();
        foreach (var (name, rule) in config.Lenses ?? new Dictionary<string, LensRule>())
        {
            lenses.Add(new LensSettings(name, new GlobSet(rule.Paths ?? []), rule.Signals ?? []));
        }

        return new Settings(
            stack,
            NullIfEmpty(config.Commands?.Format),
            NullIfEmpty(config.Commands?.Build),
            NullIfEmpty(config.Commands?.Test),
            new GlobSet(config.Watch ?? []),
            new GlobSet(config.Protect ?? []),
            lenses,
            config.Escalation?.MaxDiffLines ?? 400,
            config.Escalation?.MaxProjects ?? 3,
            config.Gate?.Enabled ?? true,
            config.Gate?.MaxAttempts ?? 3,
            config.Gate?.MaxOutputLines ?? 30,
            TimeSpan.FromSeconds(config.Gate?.TimeoutSeconds ?? 600),
            new TestGuardSettings(
                new GlobSet(config.TestGuard?.Paths ?? []),
                NonEmpty(config.TestGuard?.Markers),
                NonEmpty(config.TestGuard?.Skips)));
    }

    private static List<string> NonEmpty(List<string>? values) => (values ?? []).Where(v => !string.IsNullOrEmpty(v)).ToList();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
