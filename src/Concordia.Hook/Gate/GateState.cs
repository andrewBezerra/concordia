using System.Text.Json;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Gate;

internal sealed class GateState
{
    /// <summary>Fingerprint of the last working tree that passed build and tests.</summary>
    public string? LastGreenFingerprint { get; set; }

    /// <summary>Fingerprint of the tree the gate gave up on. It stays released until the tree changes.</summary>
    public string? ReleasedFingerprint { get; set; }

    /// <summary>Consecutive failed verifications. Reset on success or when the gate releases.</summary>
    public int Attempts { get; set; }

    public bool Disabled { get; set; }

    public string? DisabledReason { get; set; }

    /// <summary>Last concordia.json parse error sent to the agent, so the same error blocks only once.</summary>
    public string? ReportedConfigError { get; set; }

    /// <summary>Watched files edited since the last stop; formatted before verification.</summary>
    public List<string> Touched { get; set; } = [];

    /// <summary>HEAD at session start, per session id. Lets the gate verify commits made during a session.</summary>
    public Dictionary<string, string> SessionHeads { get; set; } = new();

    /// <summary>Watched files already dirty at session start, with their hashes, per session id. They are not the session's work.</summary>
    public Dictionary<string, Dictionary<string, string>> SessionBaselines { get; set; } = new();

    /// <summary>Test and skip counts of the test files already dirty at session start, per session id.</summary>
    public Dictionary<string, Dictionary<string, TestCounts>> SessionTestCounts { get; set; } = new();

    /// <summary>Session whose test removal is waiting for the user ("" when the hook got no session id); null when none is.</summary>
    public string? TestRemovalSession { get; set; }

    /// <summary>Fingerprint of the tree whose test removal the user accepted from a terminal.</summary>
    public string? AcceptedTestRemoval { get; set; }

    /// <summary>Fingerprint of the tree the gate stopped blocking on after maxAttempts. This is not an acceptance.</summary>
    public string? ReleasedTestRemoval { get; set; }

    /// <summary>Consecutive stops blocked for test removal. Separate from <see cref="Attempts"/>, which counts red builds.</summary>
    public int TestRemovalAttempts { get; set; }
}

internal sealed class StateStore(Repo repo)
{
    private const int MaxSessions = 20;

    private string FilePath => Path.Combine(repo.StateDir, "state.json");

    public GateState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new GateState();
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConcordiaJson.Default.GateState) ?? new GateState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new GateState();
        }
    }

    public void Save(GateState state)
    {
        while (state.SessionHeads.Count > MaxSessions)
        {
            var oldest = state.SessionHeads.Keys.First();
            state.SessionHeads.Remove(oldest);
            state.SessionBaselines.Remove(oldest);
            state.SessionTestCounts.Remove(oldest);
        }

        Directory.CreateDirectory(repo.StateDir);
        var temp = $"{FilePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, ConcordiaJson.Default.GateState));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Update(Action<GateState> change)
    {
        var state = Load();
        change(state);
        Save(state);
    }
}
