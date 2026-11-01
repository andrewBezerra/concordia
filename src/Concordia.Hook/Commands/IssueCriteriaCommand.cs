using System.Text.Json;
using Concordia.Hook.Review;

namespace Concordia.Hook.Commands;

/// <summary>
/// Extracts the acceptance criteria from the JSON of <c>gh issue view</c>, so the model does not have to
/// find them (or vary between runs). Exit code 0 means the input was understood, whether or not the issue
/// has criteria (<c>source</c> is <c>none</c> then); exit code 1 means the input was not usable. A bad input is
/// never reported as "no criteria": that would make the agent invent criteria for an issue that has them.
/// </summary>
internal static class IssueCriteriaCommand
{
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length > 1)
        {
            stderr.WriteLine("concordia-hook issue-criteria: usage: issue-criteria [<file>|-]  (reads stdin without an argument or with '-')");
            return 1;
        }

        string text;
        try
        {
            text = args.Length == 1 && args[0] != "-" ? File.ReadAllText(args[0]) : stdin.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stderr.WriteLine($"concordia-hook issue-criteria: could not read the input ({ex.Message})");
            return 1;
        }

        // Never let an unexpected failure reach the fail-open handler in Cli.Run: it would exit 0 with no output,
        // which reads as "no criteria". This command must fail loudly.
        IssueCriteriaResult? result;
        string? error;
        try
        {
            (result, error) = IssueCriteria.Extract(text);
        }
        catch (Exception ex)
        {
            (result, error) = (null, $"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        if (result is null)
        {
            stderr.WriteLine($"concordia-hook issue-criteria: {error}");
            return 1;
        }

        stdout.WriteLine(JsonSerializer.Serialize(result, ConcordiaJson.Default.IssueCriteriaResult));
        return 0;
    }
}
