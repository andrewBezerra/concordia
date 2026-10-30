using Concordia.Hook.Config;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Commands;

/// <summary><c>init [--force]</c>: writes concordia.json for the detected stack at the repository root.</summary>
internal static class InitCommand
{
    private const string CustomTemplate = """
        {
          "stack": "custom",
          "commands": {
            "format": "",
            "build": "",
            "test": ""
          },
          "watch": [],
          "protect": [],
          "lenses": {}
        }
        """;

    public static int Run(string[] args, string cwd, TextWriter stdout, TextWriter stderr)
    {
        var repo = Repo.Find(cwd);
        if (repo is null)
        {
            stderr.WriteLine("concordia-hook init: not inside a git repository.");
            return 1;
        }

        var target = Path.Combine(repo.Root, ConfigLoader.FileName);
        var existing = ConfigLoader.FindConfigPath(repo.Root);
        if (existing is not null && !args.Contains("--force"))
        {
            stdout.WriteLine($"{repo.Relative(existing)} already exists. Use --force to overwrite it with the preset.");
            return 0;
        }

        var stack = ConfigLoader.DetectStack(repo.Root);
        var content = stack is null ? null : Presets.ReadText(stack);
        File.WriteAllText(target, (content ?? CustomTemplate).ReplaceLineEndings("\n"));

        stdout.WriteLine(stack is null
            ? $"Wrote {ConfigLoader.FileName} (no known stack detected: fill in the build and test commands)."
            : $"Wrote {ConfigLoader.FileName} with the '{stack}' preset. Review the commands, protected paths and lenses.");
        return 0;
    }
}
