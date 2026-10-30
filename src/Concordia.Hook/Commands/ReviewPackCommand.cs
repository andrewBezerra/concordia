using System.Text;
using System.Text.Json;
using Concordia.Hook.Config;
using Concordia.Hook.Infra;
using Concordia.Hook.Review;

namespace Concordia.Hook.Commands;

/// <summary>
/// Builds the review package (stats, chosen lenses, diff) as a file, so reviewer agents read one
/// compact artifact instead of re-discovering the change with many tool calls.
/// Prints a JSON summary to stdout.
/// </summary>
internal static class ReviewPackCommand
{
    private const int MaxUntrackedLines = 400;

    public static int Run(string[] args, string cwd, TextWriter stdout, TextWriter stderr)
    {
        var baseRef = Option(args, "--base") ?? "HEAD";
        var maxLines = int.TryParse(Option(args, "--max-lines"), out var parsed) ? parsed : 3000;

        var repo = Repo.Find(cwd);
        if (repo is null)
        {
            stderr.WriteLine("concordia-hook review-pack: not inside a git repository.");
            return 1;
        }

        var settings = ConfigLoader.Load(repo.Root) ?? Settings.From("custom", new ConcordiaConfig());

        var diff = repo.Git("diff", "--no-color", "--unified=3", baseRef);
        if (!diff.Succeeded)
        {
            stderr.WriteLine($"concordia-hook review-pack: git diff {baseRef} failed: {diff.StdErr.Trim()}");
            return 1;
        }

        var (files, changedLines) = DiffParser.NumStat(repo.Git("diff", "--numstat", baseRef).StdOut);
        var added = DiffParser.AddedLines(diff.StdOut);

        // Untracked files are invisible to git diff; append them as new-file diffs.
        var untrackedDiff = new StringBuilder();
        var untracked = repo.Git("ls-files", "--others", "--exclude-standard", "-z").StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var file in untracked)
        {
            var full = Path.Combine(repo.Root, file);
            if (!File.Exists(full) || IsBinary(full)) continue;

            var lines = File.ReadAllLines(full);
            files.Add(file);
            changedLines += lines.Length;
            added[file] = lines.ToList();

            untrackedDiff.AppendLine($"diff --git a/{file} b/{file}").AppendLine("new file (untracked)").AppendLine("--- /dev/null").AppendLine($"+++ b/{file}");
            foreach (var line in lines.Take(MaxUntrackedLines)) untrackedDiff.Append('+').AppendLine(line);
            if (lines.Length > MaxUntrackedLines) untrackedDiff.AppendLine($"+... {lines.Length - MaxUntrackedLines} more line(s)");
        }

        files = files.Select(GlobSet.Normalize).Distinct(StringComparer.Ordinal).ToList();
        var projects = files.Select(f => ProjectOf(repo.Root, f)).Distinct(StringComparer.Ordinal).Count();
        var triage = LensTriage.Evaluate(files, added, changedLines, projects, settings);

        Directory.CreateDirectory(repo.StateDir);
        var packPath = Path.Combine(repo.StateDir, "review-pack.md");
        File.WriteAllText(packPath, Render(baseRef, files, changedLines, projects, triage, diff.StdOut + untrackedDiff, maxLines));

        var summary = new ReviewPackSummary
        {
            Pack = GlobSet.Normalize(packPath),
            Base = baseRef,
            Files = files.Count,
            ChangedLines = changedLines,
            Projects = projects,
            EscalateAll = triage.EscalateAll,
            EscalationReasons = triage.EscalationReasons.ToList(),
            Lenses = triage.Lenses.Select(l => new LensHit { Lens = l.Lens, Reasons = l.Reasons.ToList() }).ToList(),
        };
        stdout.WriteLine(JsonSerializer.Serialize(summary, ConcordiaJson.Default.ReviewPackSummary));
        return 0;
    }

    private static string Render(string baseRef, List<string> files, int changedLines, int projects, TriageResult triage, string diff, int maxLines)
    {
        var text = new StringBuilder();
        text.AppendLine("# Concordia review pack").AppendLine();
        text.AppendLine($"- Base: `{baseRef}`");
        text.AppendLine($"- Files: {files.Count} · Changed lines: {changedLines} · Projects: {projects}");
        text.AppendLine(triage.EscalateAll
            ? $"- Escalation: all lenses ({string.Join("; ", triage.EscalationReasons)})"
            : "- Escalation: none");
        text.AppendLine().AppendLine("## Lenses selected by triage").AppendLine();
        if (triage.Lenses.Count == 0) text.AppendLine("- none (general review only)");
        foreach (var lens in triage.Lenses) text.AppendLine($"- **{lens.Lens}**: {string.Join("; ", lens.Reasons)}");

        text.AppendLine().AppendLine("## Files").AppendLine();
        foreach (var file in files) text.AppendLine($"- {file}");

        var diffLines = diff.Replace("\r\n", "\n").Split('\n');
        text.AppendLine().AppendLine("## Diff").AppendLine().AppendLine("```diff");
        foreach (var line in diffLines.Take(maxLines)) text.AppendLine(line);
        text.AppendLine("```");
        if (diffLines.Length > maxLines)
        {
            text.AppendLine().AppendLine($"Diff truncated: {diffLines.Length - maxLines} more line(s). Read the files directly for the rest.");
        }

        return text.ToString();
    }

    private static string ProjectOf(string root, string relativeFile)
    {
        string[] markers = ["*.csproj", "*.fsproj", "*.vbproj", "package.json", "pyproject.toml", "go.mod", "Cargo.toml"];
        var dir = Path.GetDirectoryName(Path.Combine(root, relativeFile));
        var fullRoot = Path.GetFullPath(root);
        while (!string.IsNullOrEmpty(dir) && Path.GetFullPath(dir).Length >= fullRoot.Length)
        {
            if (Directory.Exists(dir) && markers.Any(m => Directory.EnumerateFiles(dir, m).Any())) return GlobSet.Normalize(Path.GetRelativePath(root, dir));
            dir = Path.GetDirectoryName(dir);
        }

        return ".";
    }

    private static bool IsBinary(string path)
    {
        Span<byte> buffer = stackalloc byte[4096];
        using var stream = File.OpenRead(path);
        var read = stream.Read(buffer);
        return buffer[..read].Contains((byte)0);
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
