using System.Globalization;
using System.Text.Json;
using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary>
/// Stop hook: the verification gate. The agent may only finish when build and tests pass for
/// the changes it made in this session. Exit code 2 sends the summarized errors back to the agent.
/// </summary>
internal static class StopCommand
{
    public const int Block = 2;

    public static int Run(HookInput input, TextWriter stdout, TextWriter stderr)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CONCORDIA_GATE"), "off", StringComparison.OrdinalIgnoreCase)) return 0;

        var repo = Repo.Find(input.WorkingDirectory);
        if (repo is null) return 0;

        var store = new StateStore(repo);
        var state = store.Load();

        Settings? settings;
        try
        {
            settings = ConfigLoader.Load(repo.Root);
        }
        catch (JsonException ex)
        {
            return InvalidConfig(repo, store, state, ex.Message, stdout, stderr);
        }

        if (settings is null || !settings.GateEnabled) return 0;
        if (settings.BuildCommand is null && settings.TestCommand is null) return 0;
        if (state.Disabled) return 0;

        state.ReportedConfigError = null;

        // 1. Format the files edited since the last stop (once, not per edit).
        var touched = state.Touched.Where(f => File.Exists(Path.Combine(repo.Root, f))).ToList();
        if (touched.Count > 0 && settings.FormatCommand is not null)
        {
            var files = string.Join(' ', touched.Select(f => $"\"{f}\""));
            ProcessRunner.Shell(settings.FormatCommand.Replace("{files}", files, StringComparison.Ordinal), repo.Root, settings.CommandTimeout);
        }

        state.Touched.Clear();

        // 2. Tests removed or skipped: only the user can accept that, so this comes before everything else.
        var removal = TestGuard.Check(repo, settings, state, input.SessionId);
        var removalKey = input.SessionId ?? "";
        string? unaccepted = null;
        if (removal.Losses.Count == 0)
        {
            // Only this session's own record: another session's removal is not this session's to clear.
            state.TestRemovals.Remove(removalKey);
        }
        else if (!state.TestRemovals.TryGetValue(removalKey, out var pending) || removal.Fingerprint != pending.Accepted)
        {
            pending ??= state.TestRemovals[removalKey] = new TestRemovalState();
            var list = TestGuard.Describe(removal, settings.MaxOutputLines);
            if (removal.Fingerprint != pending.Released)
            {
                pending.Attempts++;
                if (pending.Attempts <= settings.MaxAttempts)
                {
                    store.Save(state);
                    stderr.WriteLine(
                        $"Concordia gate: tests were removed or skipped in this session (attempt {pending.Attempts} of {settings.MaxAttempts}). " +
                        "Restore them. If removing them is right, only the user can accept it: stop and ask them to run " +
                        $"\"{SessionStartCommand.HelperPath()}\" gate accept-test-removal in a terminal, and tell them why.\n{list}");
                    return Block;
                }

                pending.Attempts = 0;
                pending.Released = removal.Fingerprint;
            }

            // The session may stop so that it cannot loop forever, but nobody has accepted anything.
            unaccepted =
                "Concordia: test removal is UNACCEPTED. The gate stopped blocking so the session could end, which is not an acceptance. " +
                $"Review it, then restore the tests or run \"{SessionStartCommand.HelperPath()}\" gate accept-test-removal in a terminal.\n{list}";
        }

        // 3. Decide whether there is anything to verify.
        var head = repo.Head();
        var changes = ChangeSet.WatchedChanges(repo, settings.Watch);
        var sessionHead = input.SessionId is not null && state.SessionHeads.TryGetValue(input.SessionId, out var h) ? h : null;
        var committedThisSession = sessionHead is not null && sessionHead != head;

        var baseline = input.SessionId is not null && state.SessionBaselines.TryGetValue(input.SessionId, out var b) ? b : null;

        // Dirty files the session found and left alone are not its work, so they trigger nothing.
        // Once something does trigger the gate, the whole tree is verified: that is what must build.
        if (!committedThisSession && !ChangeSet.DiffersFrom(repo, changes, baseline))
        {
            store.Save(state);
            WriteMessage(stdout, unaccepted, null);
            return 0;
        }

        var fingerprint = ChangeSet.Fingerprint(repo, head, changes);
        if (fingerprint == state.LastGreenFingerprint || fingerprint == state.ReleasedFingerprint)
        {
            store.Save(state);
            WriteMessage(stdout, unaccepted, null);
            return 0;
        }

        if (fingerprint == state.TimedOutFingerprint)
        {
            state.Attempts = 0;
            store.Save(state);
            WriteMessage(stdout, unaccepted, TimedOutWarning(state.TimedOutStep));
            return 0;
        }

        // 4. Build, then test. Stop at the first failing step.
        var elapsed = TimeSpan.Zero;
        foreach (var (step, command) in Steps(settings))
        {
            var result = ProcessRunner.Shell(command, repo.Root, settings.CommandTimeout);
            elapsed += result.Elapsed;

            if (result.TimedOut)
            {
                // The step may have written files, so record the tree it left: that is what the next stop will see.
                state.TimedOutFingerprint = ChangeSet.Fingerprint(repo, repo.Head(), ChangeSet.WatchedChanges(repo, settings.Watch));
                state.TimedOutStep = step;

                // Files that keep changing between stops must not block forever, so timeouts share the failure cap.
                state.Attempts++;
                if (state.Attempts > settings.MaxAttempts)
                {
                    state.Attempts = 0;
                    store.Save(state);
                    WriteMessage(stdout, unaccepted, TimedOutWarning(step));
                    return 0;
                }

                store.Save(state);
                stderr.WriteLine(
                    $"Concordia gate: {step} timed out after {settings.CommandTimeout.TotalSeconds:0}s, so nothing was verified. " +
                    "Do not say the build or the tests passed: tell the user they were not verified. " +
                    "The gate will not run them again on this same tree.");
                return Block;
            }

            if (result.Succeeded) continue;

            state.Attempts++;
            if (state.Attempts > settings.MaxAttempts)
            {
                state.Attempts = 0;
                state.ReleasedFingerprint = fingerprint;
                store.Save(state);
                WriteMessage(stdout, unaccepted,
                    $"Concordia: {step} still failing after {settings.MaxAttempts} fix attempts. " +
                    "Gate released so you can take over. Do not merge until it is green.");
                return 0;
            }

            store.Save(state);
            var summary = OutputSummarizer.Summarize(result.Combined, repo.Root, settings.MaxOutputLines);
            stderr.WriteLine(
                $"Concordia gate: {step} failed (attempt {state.Attempts} of {settings.MaxAttempts}). " +
                $"Fix the cause, do not weaken or delete tests, then finish again.\n{summary}");
            return Block;
        }

        state.Attempts = 0;
        state.LastGreenFingerprint = fingerprint;
        state.ReleasedFingerprint = null;
        state.TimedOutFingerprint = null;
        state.TimedOutStep = null;
        store.Save(state);
        WriteMessage(stdout, unaccepted, $"Concordia: build and tests green ({elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s).");
        return 0;
    }

    private static string TimedOutWarning(string? step) =>
        $"Concordia: {step} timed out on this tree, so NOTHING was verified. The gate let the session end; verify it yourself.";

    /// <summary>An unreadable config must not look like a green gate: tell the agent once, then get out of the way.</summary>
    private static int InvalidConfig(Repo repo, StateStore store, GateState state, string error, TextWriter stdout, TextWriter stderr)
    {
        if (state.Disabled) return 0;

        var path = ConfigLoader.FindConfigPath(repo.Root) is { } found ? repo.Relative(found) : ConfigLoader.FileName;
        if (state.ReportedConfigError == error)
        {
            WriteMessage(stdout, $"Concordia: {path} is still invalid. Nothing was verified.");
            return 0;
        }

        state.ReportedConfigError = error;
        store.Save(state);
        var fix = repo.ExistsInHead(path) ? "It is committed, so you may not edit it: tell the user, and ask them to fix it." : "Fix the file, then finish again.";
        stderr.WriteLine($"Concordia gate: {path} is invalid, so build and tests did not run. {fix}\n{error}");
        return Block;
    }

    private static IEnumerable<(string Step, string Command)> Steps(Settings settings)
    {
        if (settings.BuildCommand is not null) yield return ("build", settings.BuildCommand);
        if (settings.TestCommand is not null) yield return ("tests", settings.TestCommand);
    }

    /// <summary>Claude Code reads one JSON object from stdout, so the unaccepted-removal warning shares it with the gate's message.</summary>
    private static void WriteMessage(TextWriter stdout, string? unaccepted, string? message)
    {
        if (unaccepted is null && message is null) return;
        WriteMessage(stdout, unaccepted is null ? message! : message is null ? unaccepted : $"{unaccepted}\n{message}");
    }

    private static void WriteMessage(TextWriter stdout, string message) =>
        stdout.WriteLine(JsonSerializer.Serialize(new HookOutput { SystemMessage = message }, ConcordiaJson.Default.HookOutput));
}
