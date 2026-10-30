using Concordia.Hook.Config;
using Concordia.Hook.Gate;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary>
/// PostToolUse on Edit/Write: only records the edited file. Formatting happens once, in the
/// stop gate, instead of after every edit, which keeps this hook in the millisecond range.
/// </summary>
internal static class PostToolCommand
{
    public static int Run(HookInput input)
    {
        var path = input.ToolInput?.TargetPath;
        if (string.IsNullOrEmpty(path)) return 0;

        var repo = Repo.Find(path) ?? Repo.Find(input.WorkingDirectory);
        if (repo is null) return 0;

        var settings = ConfigLoader.Load(repo.Root);
        if (settings is null || settings.FormatCommand is null) return 0;

        var relative = repo.Relative(path);
        if (!settings.Watch.IsEmpty && !settings.Watch.Matches(relative)) return 0;

        new StateStore(repo).Update(state =>
        {
            if (!state.Touched.Contains(relative, StringComparer.Ordinal)) state.Touched.Add(relative);
        });
        return 0;
    }
}
