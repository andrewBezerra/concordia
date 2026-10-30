using Concordia.Hook.Commands;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

/// <summary>End-to-end tests against a real temporary git repository. Requires git on PATH.</summary>
public class GateIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("concordia-gate-").FullName;

    private const string LockedBuild = "echo 'Common.targets(5096,5): error MSB3027: Could not copy App.dll. The file is locked by: testhost' && exit 1";

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
    public void A_locked_output_file_blocks_once_without_blaming_the_code_or_spending_an_attempt()
    {
        UseCommands(build: LockedBuild, test: "echo tests ok");
        Write("src/App.cs", "class App { int X; }");

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("nothing was verified", stderr);
        Assert.Contains("not a failure in the code", stderr);
        Assert.DoesNotContain("attempt", stderr);

        // A real failure afterwards still gets every attempt.
        UseCommands(build: "echo 'src/App.cs(1,1): error CS1002: ; expected' && exit 1", test: "echo tests ok");
        Assert.Contains("attempt 1 of 3", Stop().StdErr);
    }

    [Fact]
    public void A_file_still_locked_on_the_next_stop_lets_the_session_end_saying_nothing_was_verified()
    {
        UseCommands(build: LockedBuild, test: "echo tests ok");
        Write("src/App.cs", "class App { int X; }");
        Assert.Equal(StopCommand.Block, Stop().Code);

        var (code, stdout, _) = Stop();

        Assert.Equal(0, code);
        Assert.Contains("NOTHING was verified", stdout);
        Assert.DoesNotContain("green", stdout);
    }

    [Fact]
    public void A_stop_that_passed_a_locked_file_does_not_count_as_green_once_the_file_is_free()
    {
        UseCommands(build: LockedBuild, test: "echo tests ok");
        Write("src/App.cs", "class App { int X; }");
        Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Equal(0, Stop().Code);

        UseCommands(build: "echo 'src/App.cs(1,1): error CS1002: ; expected' && exit 1", test: "echo tests ok");

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void A_locked_file_next_to_a_compile_error_is_an_ordinary_failure()
    {
        UseCommands(build: "echo 'src/App.cs(1,1): error CS1002: ; expected' && " + LockedBuild, test: "echo tests ok");
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

    [Fact]
    public void Stop_blocks_once_on_invalid_config_then_warns()
    {
        Write("concordia.json", "{ invalid");
        Write("src/App.cs", "class App { int X; }");

        var (firstCode, _, firstError) = Stop();
        var (secondCode, secondOut, _) = Stop();

        Assert.Equal(StopCommand.Block, firstCode);
        Assert.Contains("concordia.json is invalid", firstError);
        Assert.Equal(0, secondCode);
        Assert.Contains("Nothing was verified", secondOut);
    }

    [Fact]
    public void Stop_blocks_again_when_the_config_breaks_in_a_new_way()
    {
        Write("concordia.json", "{ invalid");
        Assert.Equal(StopCommand.Block, Stop().Code);

        Write("concordia.json", """{ "commands": 1 }""");

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Gate_off_is_refused_without_a_terminal()
    {
        var stderr = new StringWriter();

        var code = GateCommand.Run(["off", "agent reason"], _root, interactive: false, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("interactive terminal", stderr.ToString());
        Write("src/App.cs", "class App { int X; }");
        UseCommands(build: "exit 1", test: "exit 0");
        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Gate_off_works_from_a_terminal()
    {
        Assert.Equal(0, GateCommand.Run(["off", "user reason"], _root, interactive: true, new StringWriter(), new StringWriter()));

        Write("src/App.cs", "class App { int X; }");
        UseCommands(build: "exit 1", test: "exit 0");
        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void ReviewPack_includes_untracked_file_with_non_ascii_name()
    {
        Write("src/Validação.cs", "class V {}");
        var stdout = new StringWriter();

        var code = ReviewPackCommand.Run([], _root, stdout, new StringWriter());

        Assert.Equal(0, code);
        Assert.Contains("\"files\": 1", stdout.ToString());
        Assert.Contains("src/Validação.cs", File.ReadAllText(Path.Combine(_root, ".git", "concordia", "review-pack.md")));
    }

    [Fact]
    public void Stop_ignores_files_already_dirty_at_session_start()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        Write("src/App.cs", "class App { int UserWork; }");
        Write("src/UserDraft.cs", "class Draft {}");
        SessionStart();

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Stop_verifies_when_the_session_edits_a_file_that_was_already_dirty()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        Write("src/App.cs", "class App { int UserWork; }");
        SessionStart();

        Write("src/App.cs", "class App { int UserWork; int AgentWork; }");

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Stop_verifies_when_the_session_edits_another_file()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        Write("src/App.cs", "class App { int UserWork; }");
        SessionStart();

        Write("src/New.cs", "class New {}");

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Stop_verifies_when_the_session_reverts_a_dirty_file()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        Git("add", "-A");
        Git("commit", "-q", "-m", "red commands");
        Write("src/App.cs", "class App { int UserWork; }");
        SessionStart();

        Git("checkout", "--", "src/App.cs");

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Starting_point_survives_a_second_session_start()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        SessionStart();
        Write("src/App.cs", "class App { int AgentWork; }");

        // Claude Code fires SessionStart again on resume and after compaction.
        SessionStart();

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Released_gate_stays_released_until_the_tree_changes()
    {
        UseCommands(build: "exit 1", test: "exit 0");
        Write("src/App.cs", "class App {");
        for (var attempt = 1; attempt <= 3; attempt++) Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Contains("Gate released", Stop().StdOut);

        var (code, stdout, _) = Stop();
        Assert.Equal(0, code);
        Assert.DoesNotContain("Gate released", stdout);

        Write("src/App.cs", "class App { {");
        var (changedCode, _, changedError) = Stop();
        Assert.Equal(StopCommand.Block, changedCode);
        Assert.Contains("attempt 1 of 3", changedError);
    }

    private void SessionStart() =>
        SessionStartCommand.Run(new HookInput { Cwd = _root, SessionId = "test-session" }, new StringWriter());

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
