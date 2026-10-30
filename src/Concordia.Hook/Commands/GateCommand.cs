using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary><c>gate status|on|off [reason]</c>: inspect or toggle the verification gate for this repository.</summary>
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
                return 0;

            default:
                stderr.WriteLine("Usage: concordia-hook gate status|on|off [reason]");
                return 1;
        }
    }
}
