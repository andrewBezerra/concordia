using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary>
/// PreToolUse on Edit/Write: blocks edits to protected files that already exist in HEAD, and to what the
/// gate itself reads. On a shell tool, blocks commands that visibly cite what the gate reads.
/// </summary>
internal static class PreToolCommand
{
    public const int Block = 2;

    public static int Run(HookInput input, TextWriter stderr)
    {
        if (input.ToolInput?.Command is { } command) return Refuse(GateInputGuard.CheckCommand(command, input.WorkingDirectory), stderr);

        var target = input.ToolInput?.TargetPath;
        if (string.IsNullOrEmpty(target)) return 0;

        // Before the config is loaded: a broken concordia.json must not open the way to editing it.
        var path = Path.GetFullPath(target, input.WorkingDirectory);
        if (Refuse(GateInputGuard.CheckFile(path, input.WorkingDirectory), stderr) == Block) return Block;

        var repo = Repo.Find(path) ?? Repo.Find(input.WorkingDirectory);
        if (repo is null) return 0;

        var settings = ConfigLoader.Load(repo.Root);
        if (settings is null || settings.Protect.IsEmpty) return 0;

        var relative = repo.Relative(path);
        var rule = settings.Protect.Match(relative);
        if (rule is null) return 0;

        // New files under a protected path are fine (e.g. a new migration). Committed ones are not.
        if (!repo.ExistsInHead(relative)) return 0;

        stderr.WriteLine(
            $"Concordia: '{relative}' is protected by the rule '{rule}' in concordia.json, and it is already committed. " +
            "Do not edit it (for example, an applied EF Core migration). Create a new file instead, " +
            "or ask the user to change the rule.");
        return Block;
    }

    private static int Refuse(string? reason, TextWriter stderr)
    {
        if (reason is null) return 0;
        stderr.WriteLine(reason);
        return Block;
    }
}
