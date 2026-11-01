using System.Reflection;
using Concordia.Hook.Commands;

namespace Concordia.Hook;

internal static class Cli
{
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var command = args.Length > 0 ? args[0] : "help";
        string[] rest = args.Length > 1 ? args[1..] : [];

        try
        {
            return command switch
            {
                "pre-tool" => PreToolCommand.Run(HookInput.Read(stdin), stderr),
                "post-tool" => PostToolCommand.Run(HookInput.Read(stdin)),
                "stop" => StopCommand.Run(HookInput.Read(stdin), stdout, stderr),
                "session-start" => SessionStartCommand.Run(HookInput.Read(stdin), stdout),
                "review-pack" => ReviewPackCommand.Run(rest, Directory.GetCurrentDirectory(), stdout, stderr),
                "review-merge" => ReviewMergeCommand.Run(rest, stdout, stderr),
                "gate" => GateCommand.Run(rest, Directory.GetCurrentDirectory(), !Console.IsInputRedirected, stdout, stderr),
                "init" => InitCommand.Run(rest, Directory.GetCurrentDirectory(), stdout, stderr),
                "version" or "--version" => PrintVersion(stdout),
                _ => PrintHelp(stdout),
            };
        }
        catch (Exception ex)
        {
            // Fail open: a bug in Concordia must never block the user's session.
            stderr.WriteLine($"concordia-hook: internal error in '{command}' ({ex.GetType().Name}: {ex.Message}). Skipped.");
            return 0;
        }
    }

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static int PrintVersion(TextWriter stdout)
    {
        stdout.WriteLine($"concordia-hook {Version}");
        return 0;
    }

    private static int PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine($"""
            concordia-hook {Version}

            Hook commands (read the Claude Code hook JSON from stdin):
              pre-tool        Block edits to protected, already-committed files and to what the gate reads
              post-tool       Record edited files for formatting at the end
              stop            Verification gate: format, build and test before the agent may finish
              session-start   Print a one-line context note for the agent

            Helper commands:
              review-pack [--base <ref>] [--max-lines <n>]
                              Write a compact review package and pick review lenses
              review-merge <file>...
                              Merge the reviewers' JSON answers: dedupe, sort, verdict
              gate status|on|off [reason]
                              Inspect or toggle the verification gate for this repository
              gate accept-test-removal
                              Accept the tests removed or skipped in the current tree (terminal only)
              init [--force]  Write concordia.json for the detected stack
              version         Print the version
            """);
        return 0;
    }
}
