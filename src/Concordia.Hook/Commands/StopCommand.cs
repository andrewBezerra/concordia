using System.Globalization;
using System.Text.Json;
using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary>
/// Stop hook: the verification gate. The agent may only finish when build and tests pass for
/// the changes it made. Exit code 2 sends the summarized errors back to the agent.
/// </summary>
internal static class StopCommand
{
    public const int Block = 2;

    public static int Run(HookInput input, TextWriter stdout, TextWriter stderr)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CONCORDIA_GATE"), "off", StringComparison.OrdinalIgnoreCase)) return 0;

        var repo = Repo.Find(input.WorkingDirectory);
        if (repo is null) return 0;

        var settings = ConfigLoader.Load(repo.Root);
        if (settings is null || !settings.GateEnabled) return 0;
        if (settings.BuildCommand is null && settings.TestCommand is null) return 0;

        var store = new StateStore(repo);
        var state = store.Load();
        if (state.Disabled) return 0;

        // 1. Format the files edited since the last stop (once, not per edit).
        var touched = state.Touched.Where(f => File.Exists(Path.Combine(repo.Root, f))).ToList();
        if (touched.Count > 0 && settings.FormatCommand is not null)
        {
            var files = string.Join(' ', touched.Select(f => $"\"{f}\""));
            ProcessRunner.Shell(settings.FormatCommand.Replace("{files}", files, StringComparison.Ordinal), repo.Root, settings.CommandTimeout);
        }

        state.Touched.Clear();

        // 2. Decide whether there is anything to verify.
        var head = repo.Head();
        var changes = ChangeSet.WatchedChanges(repo, settings.Watch);
        var sessionHead = input.SessionId is not null && state.SessionHeads.TryGetValue(input.SessionId, out var h) ? h : null;
        var committedThisSession = sessionHead is not null && sessionHead != head;

        if (changes.Count == 0 && !committedThisSession)
        {
            store.Save(state);
            return 0;
        }

        var fingerprint = ChangeSet.Fingerprint(repo, head, changes);
        if (fingerprint == state.LastGreenFingerprint)
        {
            store.Save(state);
            return 0;
        }

        // 3. Build, then test. Stop at the first failing step.
        var elapsed = TimeSpan.Zero;
        foreach (var (step, command) in Steps(settings))
        {
            var result = ProcessRunner.Shell(command, repo.Root, settings.CommandTimeout);
            elapsed += result.Elapsed;

            if (result.TimedOut)
            {
                state.Attempts = 0;
                store.Save(state);
                WriteMessage(stdout, $"Concordia: {step} timed out after {settings.CommandTimeout.TotalSeconds:0}s. Gate skipped; verify manually.");
                return 0;
            }

            if (result.Succeeded) continue;

            state.Attempts++;
            if (state.Attempts > settings.MaxAttempts)
            {
                state.Attempts = 0;
                store.Save(state);
                WriteMessage(stdout,
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
        store.Save(state);
        WriteMessage(stdout, $"Concordia: build and tests green ({elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s).");
        return 0;
    }

    private static IEnumerable<(string Step, string Command)> Steps(Settings settings)
    {
        if (settings.BuildCommand is not null) yield return ("build", settings.BuildCommand);
        if (settings.TestCommand is not null) yield return ("tests", settings.TestCommand);
    }

    private static void WriteMessage(TextWriter stdout, string message) =>
        stdout.WriteLine(JsonSerializer.Serialize(new HookOutput { SystemMessage = message }, ConcordiaJson.Default.HookOutput));
}
