namespace Concordia.Hook.Config;

/// <summary>
/// Fits a preset's commands to the tools a repository actually uses: its package manager, its
/// Python runner, its Maven or Gradle wrapper. Only commands still equal to the preset's are
/// changed, so anything written in concordia.json is used exactly as written.
/// </summary>
internal static class StackTools
{
    public static void Adapt(string stack, string repoRoot, ConcordiaConfig preset, ConcordiaConfig merged)
    {
        var replacements = Replacements(stack, repoRoot, OperatingSystem.IsWindows());
        if (replacements.Count == 0 || merged.Commands is null) return;

        string? Fit(string? current, string? original) =>
            current is not null && current == original ? Apply(current, replacements) : current;

        merged.Commands.Format = Fit(merged.Commands.Format, preset.Commands?.Format);
        merged.Commands.Build = Fit(merged.Commands.Build, preset.Commands?.Build);
        merged.Commands.Test = Fit(merged.Commands.Test, preset.Commands?.Test);
    }

    /// <summary>Token replacements for the preset's commands. Each token is a command prefix, so it only ever matches at the start.</summary>
    internal static IReadOnlyList<(string From, string To)> Replacements(string stack, string repoRoot, bool windows)
    {
        bool Has(string file) => File.Exists(Path.Combine(repoRoot, file));

        switch (stack)
        {
            case "node":
                var manager = Has("pnpm-lock.yaml") ? "pnpm"
                    : Has("yarn.lock") ? "yarn"
                    : Has("bun.lockb") || Has("bun.lock") ? "bun"
                    : null;
                return manager is null
                    ? []
                    : [("npm run ", $"{manager} run "), ("npx --no-install ", manager == "pnpm" ? "pnpm exec " : manager == "yarn" ? "yarn " : "bunx ")];

            case "python":
                var runner = Has("uv.lock") ? "uv run " : Has("poetry.lock") ? "poetry run " : Has("Pipfile.lock") ? "pipenv run " : null;
                return runner is null ? [] : [("python -m pytest", $"{runner}pytest"), ("ruff format", $"{runner}ruff format")];

            case "java-maven":
                if (windows && Has("mvnw.cmd")) return [("mvn ", "mvnw.cmd ")];
                return !windows && Has("mvnw") ? [("mvn ", "./mvnw ")] : [];

            case "java-gradle":
                if (windows && Has("gradlew.bat")) return [("gradle ", "gradlew.bat ")];
                return !windows && Has("gradlew") ? [("gradle ", "./gradlew ")] : [];

            default:
                return [];
        }
    }

    private static string Apply(string command, IReadOnlyList<(string From, string To)> replacements)
    {
        foreach (var (from, to) in replacements)
        {
            if (command.StartsWith(from, StringComparison.Ordinal)) return to + command[from.Length..];
        }

        return command;
    }
}
