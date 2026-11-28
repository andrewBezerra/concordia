using System.Text.RegularExpressions;
using Concordia.Hook.Config;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Gate;

/// <summary>
/// Keeps the agent's own tools away from what the gate reads: a committed concordia.json and the
/// state directory. This is a brake, not access control: a script can always get past a text check.
/// </summary>
internal static partial class GateInputGuard
{
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    [GeneratedRegex(@"(?<![\w.-])\.git/(?:(?:worktrees|modules)/[^/]+/)*concordia(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateDirPath();

    [GeneratedRegex(@"[^\s""'`;|&<>()=]*concordia\.json(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigToken();

    [GeneratedRegex(
        @"(?<![\w./-])(?:rm|mv|cp|ln|touch|mkdir|rmdir|chmod|chown|truncate|dd|tee|rsync|xcopy|robocopy|del|sed\s+(?:\S+\s+)*?-\w*i\w*" +
        @"|Set-Content|Add-Content|Out-File|Clear-Content|Remove-Item|Move-Item|Copy-Item|New-Item|Rename-Item|Set-Item" +
        @"|python3?|node|perl|ruby|pwsh|powershell|bash|sh|zsh|eval|git\s+(?:checkout|restore|reset|clean|update-ref))(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteVerb();

    /// <summary>A &gt; or &gt;&gt; into a file; not 2&gt;&amp;1, not /dev/null, $null or nul, not an arrow (-&gt;, =&gt;).</summary>
    [GeneratedRegex(@"(?<![-=>])>>?\|?\s*(?![&\s])(?!(?:/dev/null|\$null|nul)(?![\w./-]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileRedirect();

    [GeneratedRegex(@"[\\/]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"(?<![\w/.:~-])/([a-zA-Z])/")]
    private static partial Regex GitBashDrive();

    /// <summary>The reason an edit of this file is refused, or null when it is allowed.</summary>
    public static string? CheckFile(string fullPath, string cwd)
    {
        var normalized = Normalize(fullPath);
        var cwdRepo = Repo.Find(cwd);
        var fileRepo = Repo.Find(fullPath);
        if (IsStatePath(normalized, cwdRepo) || IsStatePath(normalized, fileRepo)) return StateMessage();

        var repo = fileRepo ?? cwdRepo;
        if (repo is null) return null;

        var relative = repo.Relative(fullPath);
        return CommittedConfig(repo, relative) is { } committed ? ConfigMessage(relative, committed) : null;
    }

    /// <summary>The reason a shell command is refused, or null when it cites nothing the gate reads.</summary>
    public static string? CheckCommand(string command, string cwd)
    {
        if (!command.Contains("concordia", PathComparison)) return null;

        var normalized = Normalize(command);
        if (OperatingSystem.IsWindows()) normalized = GitBashDrive().Replace(normalized, "$1:/");

        var cwdRepo = Repo.Find(cwd);
        if (WritesToStateDir(normalized, cwdRepo)) return StateMessage();

        foreach (Match token in ConfigToken().Matches(normalized))
        {
            var cited = token.Value.StartsWith("./", StringComparison.Ordinal) ? token.Value[2..] : token.Value;

            // A bare name counts wherever the session is: "cd .. && rm concordia.json" cites it too.
            if (cwdRepo is not null && CommittedConfig(cwdRepo, cited) is { } committed) return ConfigMessage(cited, committed);

            var full = Path.GetFullPath(cited, cwd);
            var repo = Repo.Find(full);
            if (repo is not null && CommittedConfig(repo, repo.Relative(full)) is { } other) return ConfigMessage(repo.Relative(full), other);
        }

        // A root with a space in it splits the tokens above, so an absolute citation is also looked for as text.
        if (cwdRepo is null) return null;
        foreach (var candidate in ConfigLoader.Candidates)
        {
            if (normalized.Contains($"{Normalize(cwdRepo.Root)}/{candidate}", PathComparison)
                && CommittedConfig(cwdRepo, candidate) is { } committed)
            {
                return ConfigMessage(candidate, committed);
            }
        }

        return null;
    }

    /// <summary>
    /// The committed config this path would change or override, if any. The root file wins over the
    /// one in .claude, so creating it would silently replace a committed .claude/concordia.json.
    /// </summary>
    private static string? CommittedConfig(Repo repo, string relative)
    {
        var index = Array.FindIndex(ConfigLoader.Candidates, c => c.Equals(relative, PathComparison));
        return index < 0 ? null : ConfigLoader.Candidates.Skip(index).FirstOrDefault(repo.ExistsInHead);
    }

    private static bool IsStatePath(string normalizedPath, Repo? repo)
    {
        if (StateDirPath().IsMatch(normalizedPath)) return true;
        if (repo is null) return false;

        var stateDir = Normalize(Path.GetFullPath(repo.StateDir));
        return normalizedPath.Equals(stateDir, PathComparison) || normalizedPath.StartsWith(stateDir + "/", PathComparison);
    }

    /// <summary>
    /// A command that cites the state directory and also writes: it redirects output to a file (anywhere, since
    /// a variable such as <c>d=.git/concordia; echo {} &gt; $d/state.json</c> hides the target), or it uses a verb
    /// that changes files (or runs an interpreter, which could). A command that only reads or quotes the
    /// path, such as <c>cat</c> or a bug report's text, passes.
    /// </summary>
    private static bool WritesToStateDir(string normalizedCommand, Repo? repo)
    {
        var cites = new List<int>();
        foreach (Match match in StateDirPath().Matches(normalizedCommand)) cites.Add(match.Index);

        if (repo is not null)
        {
            var stateDir = Normalize(Path.GetFullPath(repo.StateDir));
            for (var at = normalizedCommand.IndexOf(stateDir, PathComparison); at >= 0; at = normalizedCommand.IndexOf(stateDir, at + stateDir.Length, PathComparison))
            {
                cites.Add(at);
            }
        }

        if (cites.Count == 0) return false;
        return FileRedirect().IsMatch(normalizedCommand) || WriteVerb().IsMatch(normalizedCommand);
    }

    private static string Normalize(string value) => Separators().Replace(value, "/");

    private static string ConfigMessage(string cited, string committed) =>
        $"Concordia: '{cited}' would change the verification gate's configuration, and '{committed}' is already committed. " +
        "The agent may not change it. To read it, use the Read tool. If it has to change, tell the user what and why, and ask them to do it.";

    private static string StateMessage() =>
        "Concordia: the verification gate's state directory is off limits to edits and to shell commands that write there. " +
        "Reading is fine (the Read tool, cat). Save anything of yours, such as a reviewer's answer, in a scratch folder outside it. If the gate is in the way, ask the user.";
}
