using Concordia.Hook.Commands;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

/// <summary>End-to-end tests against a real temporary git repository. Requires git on PATH.</summary>
public class GateIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("concordia-gate-").FullName;

    public GateIntegrationTests()
    {
        Git("init", "-q");
        Git("config", "user.email", "test@concordia.local");
        Git("config", "user.name", "Concordia Tests");
        Write("src/App.cs", "class App {}");
        Write("src/Data/Migrations/0001_Init.cs", "// applied migration");
        UseCommands(build: "echo build ok", test: "echo tests ok");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Stop_without_changes_does_nothing()
    {
        var (code, _, _) = Stop();
        Assert.Equal(0, code);
    }

    [Fact]
    public void Stop_with_green_changes_passes_and_reports()
    {
        Write("src/App.cs", "class App { int X; }");

        var (code, stdout, _) = Stop();

        Assert.Equal(0, code);
        Assert.Contains("green", stdout);
    }

    [Fact]
    public void Stop_with_failing_build_blocks_with_summarized_errors()
    {
        UseCommands(build: "echo 'src/App.cs(1,1): error CS1002: ; expected' && exit 1", test: "echo tests ok");
        Write("src/App.cs", "class App {");

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("build failed (attempt 1 of 3)", stderr);
        Assert.Contains("CS1002", stderr);
    }

    [Fact]
    public void Stop_releases_the_gate_after_max_attempts()
    {
        UseCommands(build: "exit 1", test: "echo tests ok");
        Write("src/App.cs", "class App {");

        for (var attempt = 1; attempt <= 3; attempt++) Assert.Equal(StopCommand.Block, Stop().Code);
        var (code, stdout, _) = Stop();

        Assert.Equal(0, code);
        Assert.Contains("Gate released", stdout);
    }

    [Fact]
    public void Stop_skips_verification_when_tree_is_unchanged_since_last_green()
    {
        Write("src/App.cs", "class App { int X; }");
        Assert.Equal(0, Stop().Code);

        // Same watched content as the last green run: the (now failing) commands must not even run.
        UseCommands(build: "exit 1", test: "exit 1");

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void PreTool_blocks_committed_protected_file_but_allows_new_one()
    {
        var committed = Path.Combine(_root, "src/Data/Migrations/0001_Init.cs");
        var fresh = Path.Combine(_root, "src/Data/Migrations/0002_AddPrice.cs");

        Assert.Equal(PreToolCommand.Block, PreTool(committed).Code);
        Assert.Equal(0, PreTool(fresh).Code);
        Assert.Equal(0, PreTool(Path.Combine(_root, "src/App.cs")).Code);
    }

    private (int Code, string StdOut, string StdErr) Stop()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = StopCommand.Run(new HookInput { Cwd = _root, SessionId = "test-session" }, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string StdErr) PreTool(string path)
    {
        var stderr = new StringWriter();
        var code = PreToolCommand.Run(new HookInput { Cwd = _root, ToolInput = new ToolInput { FilePath = path } }, stderr);
        return (code, stderr.ToString());
    }

    private void UseCommands(string build, string test) =>
        Write("concordia.json", $$"""
            {
              "commands": { "format": "", "build": "{{Escape(build)}}", "test": "{{Escape(test)}}" },
              "watch": ["**/*.cs"],
              "protect": ["**/Migrations/**"]
            }
            """);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, _root, TimeSpan.FromSeconds(30));
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }
}
