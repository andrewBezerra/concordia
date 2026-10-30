using Concordia.Hook.Config;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Gate;

/// <summary>How many test markers and skip markers a file has.</summary>
internal sealed record TestCounts(int Tests, int Skips);

/// <summary>One file that lost test markers or gained skip markers since the session started.</summary>
internal sealed record TestLoss(string File, int TestsRemoved, int SkipsAdded, bool Deleted, bool Unreadable = false)
{
    public string Describe()
    {
        if (Unreadable) return $"{File}: could not be read, so its tests were not counted";

        var parts = new List<string>();
        if (Deleted) parts.Add("deleted");
        if (TestsRemoved > 0) parts.Add($"{TestsRemoved} test marker(s) removed");
        if (SkipsAdded > 0) parts.Add($"{SkipsAdded} skip marker(s) added");
        return $"{File}: {string.Join(", ", parts)}";
    }
}

/// <summary>The net counts matter when a test moved: a renamed file that lost one test lists all of its markers as removed.</summary>
internal sealed record TestRemovalReport(IReadOnlyList<TestLoss> Losses, int NetTestsRemoved, int NetSkipsAdded, string Fingerprint);

/// <summary>
/// Text check for tests removed or skipped since the session started. It counts configured
/// substrings and does not understand test code, so it can be evaded (see the README).
/// The totals are compared across all changed test files, which is what lets a moved or renamed
/// test pass: what left one file arrived in another.
/// </summary>
internal static class TestGuard
{
    /// <summary>Counts for the test files already dirty at session start: git cannot give their content later.</summary>
    public static Dictionary<string, TestCounts> Snapshot(Repo repo, TestGuardSettings guard)
    {
        var counts = new Dictionary<string, TestCounts>(StringComparer.Ordinal);
        if (!guard.Enabled) return counts;

        // Not git status: it hides the old path of a staged rename, and that path must count as already gone.
        // An unreadable file counts as committed: a zero here would hide any test removed from it later.
        var head = repo.Head();
        foreach (var file in ChangedSince(repo, head).Where(guard.Paths.Matches)) counts[file] = Count(ReadWorkingTree(repo, file) ?? ReadCommit(repo, head, file), guard);
        return counts;
    }

    public static TestRemovalReport Check(Repo repo, Settings settings, GateState state, string? sessionId)
    {
        var guard = settings.TestGuard;
        var start = sessionId is not null && state.SessionHeads.TryGetValue(sessionId, out var head) ? head : repo.Head();
        var snapshot = sessionId is not null && state.SessionTestCounts.TryGetValue(sessionId, out var counts) ? counts : null;
        var changed = ChangedSince(repo, start);
        if (snapshot is not null) changed.UnionWith(snapshot.Keys);

        var testFiles = guard.Enabled ? changed.Where(guard.Paths.Matches).ToList() : [];
        var texts = testFiles.Select(file => (File: file, Text: ReadWorkingTree(repo, file))).ToList();
        var deltas = texts.Where(t => t.Text is not null).Select(t =>
        {
            var before = snapshot is not null && snapshot.TryGetValue(t.File, out var known) ? known : Count(ReadCommit(repo, start, t.File), guard);
            var now = Count(t.Text!, guard);
            return (t.File, Tests: now.Tests - before.Tests, Skips: now.Skips - before.Skips);
        }).ToList();

        var netTests = -deltas.Sum(d => d.Tests);
        var netSkips = deltas.Sum(d => d.Skips);
        var testsLost = netTests > 0;
        var skipsGained = netSkips > 0;
        var losses = deltas
            .Where(d => (testsLost && d.Tests < 0) || (skipsGained && d.Skips > 0))
            .Select(d => new TestLoss(
                d.File,
                testsLost ? Math.Max(-d.Tests, 0) : 0,
                skipsGained ? Math.Max(d.Skips, 0) : 0,
                !File.Exists(Path.Combine(repo.Root, d.File))))
            .ToList();

        // A test file nobody can read is a test file nobody counted: that must not look like no loss.
        losses.AddRange(texts.Where(t => t.Text is null).Select(t => new TestLoss(t.File, 0, 0, false, Unreadable: true)));

        // Measured from the session's start and not from HEAD, so committing an accepted tree keeps the acceptance.
        var fingerprintFiles = changed.Where(f => settings.Watch.IsEmpty || settings.Watch.Matches(f) || guard.Paths.Matches(f)).ToList();
        return new TestRemovalReport(losses, Math.Max(netTests, 0), Math.Max(netSkips, 0), ChangeSet.Fingerprint(repo, start, fingerprintFiles));
    }

    /// <summary>The removals the gate blocked on that still stand and that nobody has accepted, as the tree is now.</summary>
    public static List<(TestRemovalState State, TestRemovalReport Report)> Unaccepted(Repo repo, Settings settings, GateState state)
    {
        var waiting = new List<(TestRemovalState, TestRemovalReport)>();
        foreach (var (session, removal) in state.TestRemovals)
        {
            var report = Check(repo, settings, state, session.Length == 0 ? null : session);
            if (report.Losses.Count > 0 && report.Fingerprint != removal.Accepted) waiting.Add((removal, report));
        }

        return waiting;
    }

    /// <summary>The report's lines, cut to the configured limit because they go into the model context.</summary>
    public static string Describe(TestRemovalReport report, int maxLines)
    {
        var lines = new List<string> { $"Net since the session started: {report.NetTestsRemoved} test marker(s) removed, {report.NetSkipsAdded} skip marker(s) added." };
        lines.AddRange(report.Losses.Take(maxLines).Select(l => $"- {l.Describe()}"));
        if (report.Losses.Count > maxLines) lines.Add($"- and {report.Losses.Count - maxLines} more file(s)");
        return string.Join('\n', lines);
    }

    internal static TestCounts Count(string text, TestGuardSettings guard)
    {
        var tests = 0;
        var skips = 0;
        foreach (var line in text.Split('\n'))
        {
            // A commented-out test is a removed test. Block comments and #if are not recognized.
            if (line.AsSpan().TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

            tests += guard.Markers.Sum(m => Occurrences(line, m));
            skips += guard.Skips.Sum(m => Occurrences(line, m));
        }

        return new TestCounts(tests, skips);
    }

    private static int Occurrences(string line, string marker)
    {
        var found = 0;
        for (var at = line.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = line.IndexOf(marker, at + marker.Length, StringComparison.Ordinal)) found++;
        return found;
    }

    /// <summary>Everything that differs from the start: tracked changes, committed or not, plus untracked files.</summary>
    private static SortedSet<string> ChangedSince(Repo repo, string start)
    {
        var files = new SortedSet<string>(ChangeSet.WatchedChanges(repo, new GlobSet([])), StringComparer.Ordinal);
        if (start.Length == 0) return files;

        var diff = repo.Git("diff", "--name-only", "--no-renames", "-z", start);
        if (diff.Succeeded) files.UnionWith(diff.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(GlobSet.Normalize));
        return files;
    }

    private static string ReadCommit(Repo repo, string commit, string file)
    {
        var result = commit.Length == 0 ? null : repo.Git("show", $"{commit}:{file}");
        return result is { Succeeded: true } ? result.StdOut : string.Empty;
    }

    /// <summary>The file's text, empty when it is gone, or null when it exists and cannot be read.</summary>
    private static string? ReadWorkingTree(Repo repo, string file)
    {
        var full = Path.Combine(repo.Root, file);
        try
        {
            return File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
