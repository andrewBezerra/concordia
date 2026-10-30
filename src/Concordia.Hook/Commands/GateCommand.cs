using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary><c>gate status|on|off [reason]|accept-test-removal</c>: inspect or toggle the verification gate for this repository, or accept a test removal.</summary>
internal static class GateCommand
{
    public static int Run(string[] args, string cwd, bool interactive, TextWriter stdout, TextWriter stderr)
    {
        var repo = Repo.Find(cwd);
        if (repo is null)
        {
            stderr.WriteLine("concordia-hook gate: not inside a git repository.");
            return 1;
        }

        var store = new StateStore(repo);
        var action = args.Length > 0 ? args[0] : "status";
        switch (action)
        {
            case "off":
                // The agent's Bash tool has no terminal, so this keeps the gate out of the agent's reach.
                if (!interactive)
                {
                    stderr.WriteLine(
                        "concordia-hook gate off: refused, it needs an interactive terminal. " +
                        "Only the user can turn the gate off: ask them, and tell them why.");
                    return 1;
                }

                var reason = args.Length > 1 ? string.Join(' ', args[1..]) : "turned off manually";
                store.Update(s =>
                {
                    s.Disabled = true;
                    s.DisabledReason = reason;
                });
                stdout.WriteLine($"Concordia gate: off ({reason}).");
                return 0;

            case "on":
                store.Update(s =>
                {
                    s.Disabled = false;
                    s.DisabledReason = null;
                    s.Attempts = 0;
                });
                stdout.WriteLine("Concordia gate: on.");
                return 0;

            case "accept-test-removal":
                return AcceptTestRemoval(repo, store, interactive, stdout, stderr);

            case "status":
                var state = store.Load();
                var settings = ConfigLoader.Load(repo.Root);
                var config = ConfigLoader.FindConfigPath(repo.Root) is { } path ? repo.Relative(path) : "none (preset defaults)";
                stdout.WriteLine($"Stack: {settings?.Stack ?? "not detected"}");
                stdout.WriteLine($"Config: {config}");
                stdout.WriteLine($"Gate: {(settings is null || !settings.GateEnabled || state.Disabled ? "off" : "on")}{(state.Disabled ? $" ({state.DisabledReason})" : "")}");
                stdout.WriteLine($"Build: {settings?.BuildCommand ?? "-"}");
                stdout.WriteLine($"Test: {settings?.TestCommand ?? "-"}");
                stdout.WriteLine($"Failed attempts in a row: {state.Attempts}");
                if (state.TestRemovalSession is not null) stdout.WriteLine("Test removal: unaccepted");
                return 0;

            default:
                stderr.WriteLine("Usage: concordia-hook gate status|on|off [reason]|accept-test-removal");
                return 1;
        }
    }

    /// <summary>Accepts the removal in the tree as it is now. Any later change to the tree needs a new acceptance.</summary>
    private static int AcceptTestRemoval(Repo repo, StateStore store, bool interactive, TextWriter stdout, TextWriter stderr)
    {
        // The agent's Bash tool has no terminal, so this keeps the acceptance out of the agent's reach.
        if (!interactive)
        {
            stderr.WriteLine(
                "concordia-hook gate accept-test-removal: refused, it needs an interactive terminal. " +
                "Only the user can accept a test removal: ask them, and tell them why.");
            return 1;
        }

        var state = store.Load();
        var settings = ConfigLoader.Load(repo.Root);
        if (state.TestRemovalSession is null || settings is null)
        {
            stdout.WriteLine("Concordia gate: no test removal is waiting for acceptance.");
            return 0;
        }

        var session = state.TestRemovalSession.Length == 0 ? null : state.TestRemovalSession;
        var report = TestGuard.Check(repo, settings, state, session);
        if (report.Losses.Count == 0)
        {
            state.TestRemovalSession = null;
            store.Save(state);
            stdout.WriteLine("Concordia gate: the tests are back, so there is nothing to accept.");
            return 0;
        }

        state.AcceptedTestRemoval = report.Fingerprint;
        state.ReleasedTestRemoval = null;
        state.TestRemovalSession = null;
        state.TestRemovalAttempts = 0;
        store.Save(state);
        stdout.WriteLine($"Concordia gate: test removal accepted for the tree as it is now. Changing the tree needs a new acceptance.\n{TestGuard.Describe(report, settings.MaxOutputLines)}");
        return 0;
    }
}
