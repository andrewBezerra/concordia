using System.Diagnostics;
using System.Text;

namespace Concordia.Hook.Infra;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, TimeSpan Elapsed)
{
    public string Combined => StdErr.Length == 0 ? StdOut : StdOut + Environment.NewLine + StdErr;

    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

internal static class ProcessRunner
{
    /// <summary>Runs an executable with an exact argument list (no shell).</summary>
    public static ProcessResult Run(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        // Git writes UTF-8 whatever the console code page is; decoding it otherwise breaks non-ASCII paths.
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Execute(info, timeout);
    }

    /// <summary>Runs a user-configured command line through the platform shell.</summary>
    public static ProcessResult Shell(string commandLine, string workingDirectory, TimeSpan timeout)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // /s strips the outer quotes and runs the rest verbatim, so user quoting survives.
            info = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{commandLine}\"" };
        }
        else
        {
            info = new ProcessStartInfo("/bin/sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(commandLine);
        }

        info.WorkingDirectory = workingDirectory;
        return Execute(info, timeout);
    }

    private static ProcessResult Execute(ProcessStartInfo info, TimeSpan timeout)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["MSBUILDTERMINALLOGGER"] = "off";

        // OutputSummarizer parses English output; a localized SDK would hide every test failure from it.
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{info.FileName}'.");

        // Children must never wait on the hook's stdin.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var timedOut = !process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            process.WaitForExit();
        }

        Task.WaitAll(stdout, stderr);
        stopwatch.Stop();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout.Result, stderr.Result, timedOut, stopwatch.Elapsed);
    }
}
