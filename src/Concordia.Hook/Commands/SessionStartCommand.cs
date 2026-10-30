using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary>
/// SessionStart: records HEAD for this session and prints one short line of context.
/// Deliberately tiny: Concordia never preloads skills into the session.
/// </summary>
internal static class SessionStartCommand
{
    public static int Run(HookInput input, TextWriter stdout)
    {
        var repo = Repo.Find(input.WorkingDirectory);
        if (repo is null) return 0;

        var settings = ConfigLoader.Load(repo.Root);
        if (settings is null)
        {
            stdout.WriteLine($"Concordia is installed but not configured for this repository (run /concordia:init). Concordia helper: \"{HelperPath()}\".");
            return 0;
        }

        var store = new StateStore(repo);
        var state = store.Load();
        if (input.SessionId is not null)
        {
            state.SessionHeads.Remove(input.SessionId);
            state.SessionHeads[input.SessionId] = repo.Head();
        }

        store.Save(state);

        var gate = !settings.GateEnabled || state.Disabled ? "off" : "on";
        stdout.WriteLine(
            $"Concordia is active (stack: {settings.Stack}, verification gate: {gate}). " +
            $"Concordia helper: \"{HelperPath()}\". Use /concordia:feature or /concordia:fix for code changes.");
        return 0;
    }

    /// <summary>Path the agent should call: the launcher script when running as a plugin, else this binary.</summary>
    internal static string HelperPath()
    {
        var pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT");
        if (!string.IsNullOrEmpty(pluginRoot)) return GlobSet.Normalize(Path.Combine(pluginRoot, "bin", "concordia-hook"));
        return Environment.ProcessPath ?? "concordia-hook";
    }
}
