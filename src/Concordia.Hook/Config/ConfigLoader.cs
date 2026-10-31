using System.Text.Json;

namespace Concordia.Hook.Config;

internal static class ConfigLoader
{
    public const string FileName = "concordia.json";

    /// <summary>The config a session pins when the repository has no file: the preset alone.</summary>
    public const string NoConfig = "{}";

    /// <summary>Where the file may live, relative to the repository root. The first one found wins.</summary>
    public static readonly string[] Candidates = [FileName, $".claude/{FileName}"];

    /// <summary>
    /// Loads the effective settings for a repository. Returns null when the repository has
    /// no concordia.json and no recognizable stack: Concordia then stays out of the way.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="pinnedConfig">Config text to use instead of the file, when the session pinned one at its start.</param>
    public static Settings? Load(string repoRoot, string? pinnedConfig = null)
    {
        var user = pinnedConfig is null ? ReadUserConfig(repoRoot) : Parse(pinnedConfig);
        var stack = user?.Stack ?? DetectStack(repoRoot);
        if (user is null && stack is null) return null;

        var preset = stack is null ? null : Presets.Load(stack);
        var merged = Merge(preset ?? new ConcordiaConfig(), user ?? new ConcordiaConfig());
        if (stack is not null && preset is not null) StackTools.Adapt(stack, repoRoot, preset, merged);
        return Settings.From(stack ?? "custom", merged);
    }

    public static string? FindConfigPath(string repoRoot)
    {
        return Candidates.Select(c => Path.Combine(repoRoot, c)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Files at the repository root that identify a stack, in priority order: when a root has several
    /// (a .NET solution with a package.json for tooling, say), the first stack listed here wins.
    /// </summary>
    internal static readonly (string Stack, string[] Markers)[] StackMarkers =
    [
        ("dotnet", ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "global.json", "Directory.Build.props"]),
        ("java-maven", ["pom.xml"]),
        ("java-gradle", ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"]),
        ("python", ["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile"]),
        ("node", ["package.json"]),
    ];

    /// <summary>The stack Concordia uses for this repository, or null when none is recognized.</summary>
    public static string? DetectStack(string repoRoot) => DetectStacks(repoRoot).FirstOrDefault();

    /// <summary>Every stack recognized at the root, in priority order; the first one is the one in use.</summary>
    public static IReadOnlyList<string> DetectStacks(string repoRoot)
    {
        var found = StackMarkers
            .Where(s => s.Markers.Any(pattern => Directory.EnumerateFiles(repoRoot, pattern).Any()))
            .Select(s => s.Stack)
            .ToList();
        if (found.Count > 0) return found;

        // A .NET repository often keeps its projects one or two folders down, without a root solution.
        var nested = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(repoRoot, "*.csproj", nested).Any() ? ["dotnet"] : [];
    }

    private static ConcordiaConfig? ReadUserConfig(string repoRoot)
    {
        var text = ReadConfigText(repoRoot);
        return text is null ? null : Parse(text);
    }

    /// <summary>The raw text of the config file that wins, or null when there is none.</summary>
    public static string? ReadConfigText(string repoRoot)
    {
        var path = FindConfigPath(repoRoot);
        return path is null ? null : File.ReadAllText(path);
    }

    private static ConcordiaConfig? Parse(string text) => JsonSerializer.Deserialize(text, ConcordiaJson.Default.ConcordiaConfig);

    internal static ConcordiaConfig Merge(ConcordiaConfig preset, ConcordiaConfig user) => new()
    {
        Stack = user.Stack ?? preset.Stack,
        Commands = new CommandSet
        {
            Format = user.Commands?.Format ?? preset.Commands?.Format,
            Build = user.Commands?.Build ?? preset.Commands?.Build,
            Test = user.Commands?.Test ?? preset.Commands?.Test,
        },
        Watch = user.Watch ?? preset.Watch,
        Protect = user.Protect ?? preset.Protect,
        Lenses = MergeLenses(preset.Lenses, user.Lenses),
        Escalation = new EscalationRules
        {
            MaxDiffLines = user.Escalation?.MaxDiffLines ?? preset.Escalation?.MaxDiffLines,
            MaxProjects = user.Escalation?.MaxProjects ?? preset.Escalation?.MaxProjects,
        },
        Gate = new GateOptions
        {
            Enabled = user.Gate?.Enabled ?? preset.Gate?.Enabled,
            MaxAttempts = user.Gate?.MaxAttempts ?? preset.Gate?.MaxAttempts,
            MaxOutputLines = user.Gate?.MaxOutputLines ?? preset.Gate?.MaxOutputLines,
            TimeoutSeconds = user.Gate?.TimeoutSeconds ?? preset.Gate?.TimeoutSeconds,
        },
        TestGuard = new TestGuardOptions
        {
            Paths = user.TestGuard?.Paths ?? preset.TestGuard?.Paths,
            Markers = user.TestGuard?.Markers ?? preset.TestGuard?.Markers,
            Skips = user.TestGuard?.Skips ?? preset.TestGuard?.Skips,
        },
    };

    private static Dictionary<string, LensRule> MergeLenses(Dictionary<string, LensRule>? preset, Dictionary<string, LensRule>? user)
    {
        var result = new Dictionary<string, LensRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rule) in preset ?? new Dictionary<string, LensRule>()) result[name] = rule;

        // A lens defined in the repository file replaces the preset lens of the same name.
        foreach (var (name, rule) in user ?? new Dictionary<string, LensRule>()) result[name] = rule;
        return result;
    }
}
