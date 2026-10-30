using System.Text.Json;
using Concordia.Hook.Commands;
using Concordia.Hook.Config;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

/// <summary>The stop gate against a real temporary git repository. Requires git on PATH.</summary>
public class TestRemovalGateTests : IDisposable
{
    private const string TwoTests = """
        public class AppTests
        {
            [Fact]
            public void One() { }

            [Theory]
            [InlineData(1)]
            public void Two(int x) { }
        }
        """;

    private const string OneTestLess = """
        public class AppTests
        {
            [Fact]
            public void One() { }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("concordia-tests-").FullName;

    public TestRemovalGateTests()
    {
        Git("init", "-q");
        Git("config", "user.email", "test@concordia.local");
        Git("config", "user.name", "Concordia Tests");
        Write("src/App.cs", "class App {}");
        Write("tests/AppTests.cs", TwoTests);
        Write("tests/Support/TestApp.cs", "class TestApp {}");
        UseConfig("echo build ok");
        Commit();
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
    public void Deleting_a_test_file_blocks_and_lists_it()
    {
        SessionStart();
        File.Delete(Path("tests/AppTests.cs"));

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("tests/AppTests.cs: deleted, 3 test marker(s) removed", stderr);
        Assert.Contains("attempt 1 of 3", stderr);
    }

    [Fact]
    public void Removing_a_test_attribute_blocks()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("tests/AppTests.cs: 2 test marker(s) removed", stderr);
    }

    [Fact]
    public void Adding_a_skip_blocks()
    {
        SessionStart();
        Write("tests/AppTests.cs", TwoTests.Replace("[Fact]", """[Fact(Skip = "later")]"""));

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("tests/AppTests.cs: 1 skip marker(s) added", stderr);
    }

    [Fact]
    public void Commenting_out_a_test_blocks()
    {
        SessionStart();
        Write("tests/AppTests.cs", TwoTests.Replace("[Fact]", "// [Fact]"));

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void A_removal_the_session_committed_still_blocks()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Commit();

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Renaming_a_test_file_without_staging_does_not_block()
    {
        SessionStart();
        File.Move(Path("tests/AppTests.cs"), Path("tests/RenamedTests.cs"));

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Renaming_a_test_file_with_git_does_not_block()
    {
        SessionStart();
        Git("mv", "tests/AppTests.cs", "tests/RenamedTests.cs");

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Renaming_a_test_file_and_losing_a_test_blocks_and_reports_the_net_loss()
    {
        SessionStart();
        File.Delete(Path("tests/AppTests.cs"));
        Write("tests/RenamedTests.cs", OneTestLess);

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("Net since the session started: 2 test marker(s) removed", stderr);
    }

    [Fact]
    public void Rename_staged_before_the_session_started_does_not_block()
    {
        Git("mv", "tests/AppTests.cs", "tests/RenamedTests.cs");
        SessionStart();

        Write("src/App.cs", "class App { int AgentWork; }");

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Moving_a_test_to_another_file_does_not_block()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Write("tests/OtherTests.cs", "class OtherTests { [Theory] [InlineData(1)] public void Two(int x) { } }");

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Deleting_a_helper_without_tests_does_not_block()
    {
        SessionStart();
        File.Delete(Path("tests/Support/TestApp.cs"));

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Removal_already_dirty_at_session_start_does_not_block()
    {
        Write("tests/AppTests.cs", OneTestLess);
        SessionStart();

        Assert.Equal(0, Stop().Code);

        Write("src/App.cs", "class App { int AgentWork; }");
        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Removing_a_test_the_user_had_not_committed_blocks()
    {
        Write("tests/DraftTests.cs", "class DraftTests { [Fact] public void Draft() { } }");
        SessionStart();

        File.Delete(Path("tests/DraftTests.cs"));

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Starting_counts_survive_a_second_session_start()
    {
        Write("tests/DraftTests.cs", "class DraftTests { [Fact] public void Draft() { } }");
        SessionStart();
        File.Delete(Path("tests/DraftTests.cs"));

        SessionStart();

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Acceptance_is_refused_without_a_terminal()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Assert.Equal(StopCommand.Block, Stop().Code);
        var stderr = new StringWriter();

        var code = GateCommand.Run(["accept-test-removal"], _root, interactive: false, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("interactive terminal", stderr.ToString());
        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Acceptance_covers_that_tree_and_no_other()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Assert.Equal(StopCommand.Block, Stop().Code);

        var stdout = new StringWriter();
        Assert.Equal(0, GateCommand.Run(["accept-test-removal"], _root, interactive: true, stdout, new StringWriter()));
        Assert.Contains("tests/AppTests.cs: 2 test marker(s) removed", stdout.ToString());
        Assert.Equal(0, Stop().Code);

        Write("src/App.cs", "class App { int Later; }");
        var (code, _, stderr) = Stop();
        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("attempt 1 of 3", stderr);
    }

    [Fact]
    public void Committing_an_accepted_tree_keeps_the_acceptance()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Equal(0, GateCommand.Run(["accept-test-removal"], _root, interactive: true, new StringWriter(), new StringWriter()));

        Commit();

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Acceptance_with_nothing_waiting_accepts_nothing()
    {
        SessionStart();
        var stdout = new StringWriter();
        Assert.Equal(0, GateCommand.Run(["accept-test-removal"], _root, interactive: true, stdout, new StringWriter()));
        Assert.Contains("no test removal", stdout.ToString());

        Write("tests/AppTests.cs", OneTestLess);

        Assert.Equal(StopCommand.Block, Stop().Code);
    }

    [Fact]
    public void Max_attempts_lets_the_session_stop_without_accepting()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);

        for (var attempt = 1; attempt <= 3; attempt++) Assert.Equal(StopCommand.Block, Stop().Code);

        // Stopping is allowed from here on, and the warning stays for as long as nobody has accepted.
        for (var stop = 1; stop <= 2; stop++)
        {
            var (code, stdout, _) = Stop();
            Assert.Equal(0, code);
            var message = SystemMessage(stdout);
            Assert.Contains("test removal is UNACCEPTED", message);
            Assert.Contains("not an acceptance", message);
            Assert.Contains("tests/AppTests.cs", message);
        }

        var status = new StringWriter();
        GateCommand.Run(["status"], _root, interactive: false, status, new StringWriter());
        Assert.Contains("Test removal: unaccepted", status.ToString());

        Write("src/App.cs", "class App { int Later; }");
        var (changedCode, _, changedError) = Stop();
        Assert.Equal(StopCommand.Block, changedCode);
        Assert.Contains("attempt 1 of 3", changedError);
    }

    [Fact]
    public void Released_removal_still_runs_the_build_and_shares_one_json_message()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        for (var attempt = 1; attempt <= 3; attempt++) Assert.Equal(StopCommand.Block, Stop().Code);

        var (code, stdout, _) = Stop();

        Assert.Equal(0, code);
        var message = SystemMessage(stdout);
        Assert.Contains("UNACCEPTED", message);
        Assert.Contains("build and tests green", message);
    }

    [Fact]
    public void Restoring_the_tests_clears_the_block_and_the_count()
    {
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);
        Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Equal(StopCommand.Block, Stop().Code);

        Write("tests/AppTests.cs", TwoTests);
        Assert.Equal(0, Stop().Code);

        Write("tests/AppTests.cs", OneTestLess);
        Assert.Contains("attempt 1 of 3", Stop().StdErr);
    }

    [Fact]
    public void A_red_build_keeps_its_own_attempts_after_a_restored_test()
    {
        UseConfig("exit 1");
        Commit();
        SessionStart();
        Write("tests/AppTests.cs", OneTestLess);

        Assert.Contains("tests were removed", Stop().StdErr);

        Write("tests/AppTests.cs", TwoTests);
        Write("src/App.cs", "class App {");
        Assert.Contains("build failed (attempt 1 of 3)", Stop().StdErr);
    }

    [Fact]
    public void Empty_lists_turn_off_the_preset_check()
    {
        Write("concordia.json", """
            { "stack": "dotnet", "commands": { "format": "", "build": "echo ok", "test": "" }, "watch": ["**/*.cs"], "testGuard": { "markers": [], "skips": [] } }
            """);
        Commit();
        SessionStart();
        File.Delete(Path("tests/AppTests.cs"));

        Assert.Equal(0, Stop().Code);
    }

    [Fact]
    public void Dotnet_preset_covers_test_paths_markers_and_skips()
    {
        var dir = Directory.CreateTempSubdirectory("concordia-preset-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "App.slnx"), "<Solution />");

            var guard = ConfigLoader.Load(dir)!.TestGuard;

            Assert.True(guard.Enabled);
            Assert.True(guard.Paths.Matches("tests/App.Tests/Support/Helper.cs"));
            Assert.True(guard.Paths.Matches("src/App/OrderTests.cs"));
            Assert.False(guard.Paths.Matches("src/App/Order.cs"));
            Assert.Contains("[Fact]", guard.Markers);
            Assert.Contains("[Theory]", guard.Markers);
            Assert.Contains("Skip =", guard.Skips);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Repository_file_overrides_one_list_of_the_preset()
    {
        var dir = Directory.CreateTempSubdirectory("concordia-preset-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "App.slnx"), "<Solution />");
            File.WriteAllText(System.IO.Path.Combine(dir, "concordia.json"), """{ "testGuard": { "markers": ["[MyTest]"] } }""");

            var guard = ConfigLoader.Load(dir)!.TestGuard;

            Assert.Equal(["[MyTest]"], guard.Markers);
            Assert.Contains("Skip =", guard.Skips);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string SystemMessage(string stdout)
    {
        using var json = JsonDocument.Parse(stdout);
        return json.RootElement.GetProperty("systemMessage").GetString()!;
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

    private void UseConfig(string build) =>
        Write("concordia.json", $$"""
            {
              "commands": { "format": "", "build": "{{build}}", "test": "" },
              "watch": ["**/*.cs"],
              "testGuard": {
                "paths": ["**/tests/**/*.cs"],
                "markers": ["[Fact]", "[Fact(", "[Theory]", "[InlineData("],
                "skips": ["Skip ="]
              }
            }
            """);

    private void Commit()
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", "change");
    }

    private string Path(string relative) => System.IO.Path.Combine(_root, relative);

    private void Write(string relative, string content)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Git(params string[] args)
    {
        var result = ProcessRunner.Run("git", args, _root, TimeSpan.FromSeconds(30));
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }
}
