namespace Concordia.Hook.Infra;

/// <summary>A git working tree. Supports linked worktrees, where <c>.git</c> is a file.</summary>
internal sealed record Repo(string Root, string GitDir)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Concordia state lives inside the git directory: never committed, separate per worktree.</summary>
    public string StateDir => Path.Combine(GitDir, "concordia");

    /// <summary>Walks up from a file or directory until it finds a <c>.git</c> entry. No process is started.</summary>
    public static Repo? Find(string startPath)
    {
        var full = Path.GetFullPath(startPath);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);

        while (!string.IsNullOrEmpty(dir))
        {
            var dotGit = Path.Combine(dir, ".git");
            if (Directory.Exists(dotGit)) return new Repo(dir, dotGit);

            if (File.Exists(dotGit))
            {
                const string prefix = "gitdir:";
                var line = File.ReadAllText(dotGit).Trim();
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var gitDir = line[prefix.Length..].Trim();
                    if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(dir, gitDir));
                    return new Repo(dir, gitDir);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Repository-relative path with forward slashes.</summary>
    public string Relative(string path) =>
        GlobSet.Normalize(Path.GetRelativePath(Root, Path.GetFullPath(path, Root)));

    /// <summary>Runs git with non-ASCII paths left as they are, so its output can be matched against real file names.</summary>
    public ProcessResult Git(params string[] arguments) =>
        ProcessRunner.Run("git", ["-c", "core.quotePath=false", .. arguments], Root, GitTimeout);

    public string Head()
    {
        var result = Git("rev-parse", "HEAD");
        return result.Succeeded ? result.StdOut.Trim() : string.Empty;
    }

    public bool ExistsInHead(string relativePath) => Git("cat-file", "-e", $"HEAD:{relativePath}").Succeeded;
}
