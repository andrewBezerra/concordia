namespace Concordia.Hook.Config;

/// <summary>
/// Shape of <c>concordia.json</c> and of the built-in presets. Every field is optional:
/// values in the repository file override the preset for the detected stack.
/// An empty string for a command disables that step.
/// </summary>
internal sealed class ConcordiaConfig
{
    public string? Stack { get; set; }
    public CommandSet? Commands { get; set; }
    public List<string>? Watch { get; set; }
    public List<string>? Protect { get; set; }
    public Dictionary<string, LensRule>? Lenses { get; set; }
    public EscalationRules? Escalation { get; set; }
    public GateOptions? Gate { get; set; }
    public TestGuardOptions? TestGuard { get; set; }
}

internal sealed class CommandSet
{
    /// <summary>Formatter command. <c>{files}</c> is replaced by the quoted list of edited files.</summary>
    public string? Format { get; set; }
    public string? Build { get; set; }
    public string? Test { get; set; }
}

internal sealed class LensRule
{
    /// <summary>Globs: a changed file matching one of them activates the lens.</summary>
    public List<string>? Paths { get; set; }

    /// <summary>Plain substrings: an added line containing one of them activates the lens.</summary>
    public List<string>? Signals { get; set; }
}

internal sealed class EscalationRules
{
    public int? MaxDiffLines { get; set; }
    public int? MaxProjects { get; set; }
}

internal sealed class TestGuardOptions
{
    /// <summary>Globs: the files that are scanned for the markers below.</summary>
    public List<string>? Paths { get; set; }

    /// <summary>Plain substrings: each occurrence counts as one test. Fewer of them than at session start blocks.</summary>
    public List<string>? Markers { get; set; }

    /// <summary>Plain substrings: each occurrence counts as one skip. More of them than at session start blocks.</summary>
    public List<string>? Skips { get; set; }
}

internal sealed class GateOptions
{
    public bool? Enabled { get; set; }
    public int? MaxAttempts { get; set; }
    public int? MaxOutputLines { get; set; }
    public int? TimeoutSeconds { get; set; }
}
