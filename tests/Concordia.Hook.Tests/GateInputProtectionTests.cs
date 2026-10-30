using System.Text.Json;
using Concordia.Hook.Commands;
using Concordia.Hook.Config;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Tests;

/// <summary>The brake on what the gate reads, and the gate's timeouts. Requires git on PATH.</summary>
public class GateInputProtectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("concordia protect ").FullName;

    public GateInputProtectionTests()
    {
        Git("init", "-q");
        Git("config", "user.email", "test@concordia.local");
        Git("config", "user.name", "Concordia Tests");
        Write("src/App.cs", "class App {}");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("concordia.json")]
    [InlineData(".claude/concordia.json")]
    public void Edit_of_a_committed_config_is_blocked(string relative)
    {
        CommitFile(relative, """{ "commands": { "build": "echo ok" } }""");

        var (code, stderr) = Edit(Path.Combine(_root, relative));

        Assert.Equal(PreToolCommand.Block, code);
        Assert.Contains("ask them", stderr);
    }

    [Theory]
    [InlineData("concordia.json")]
    [InlineData(".claude/concordia.json")]
    public void Edit_of_an_uncommitted_config_is_allowed(string relative)
    {
        CommitFile("src/App.cs", "class App {}");
        Assert.Equal(0, Edit(Path.Combine(_root, relative)).Code);

        Write(relative, "{}");
        Assert.Equal(0, Edit(Path.Combine(_root, relative)).Code);
    }

    [Fact]
    public void Edit_of_a_committed_config_is_blocked_even_when_the_file_does_not_parse()
    {
        CommitFile("concordia.json", "{ invalid");

        Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(_root, "concordia.json")).Code);
    }

    [Fact]
    public void Edit_of_a_committed_config_is_blocked_through_a_relative_or_indirect_path()
    {
        CommitFile("concordia.json", "{}");

        Assert.Equal(PreToolCommand.Block, Edit("concordia.json").Code);
        Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(_root, "src", "..", "concordia.json")).Code);
    }

    [Fact]
    public void A_new_root_config_may_not_override_a_committed_one_in_dot_claude()
    {
        CommitFile(".claude/concordia.json", "{}");

        Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(_root, "concordia.json")).Code);
    }

    [Fact]
    public void A_config_in_a_subfolder_is_not_the_gate_config()
    {
        CommitFile("samples/concordia.json", "{}");

        Assert.Equal(0, Edit(Path.Combine(_root, "samples/concordia.json")).Code);
    }

    [Fact]
    public void Edit_under_the_state_directory_is_blocked()
    {
        CommitFile("src/App.cs", "class App {}");

        Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(_root, ".git", "concordia", "state.json")).Code);
        Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(_root, ".git", "concordia", "anything", "new.txt")).Code);
    }

    [Fact]
    public void Edit_of_the_state_of_a_linked_worktree_is_blocked_from_the_main_tree()
    {
        CommitFile("src/App.cs", "class App {}");
        var linked = Path.Combine(Directory.CreateTempSubdirectory("concordia-linked-").FullName, "wt");
        try
        {
            Git("worktree", "add", "-q", linked);
            var gitDir = Repo.Find(linked)!.GitDir;

            Assert.Equal(PreToolCommand.Block, Edit(Path.Combine(gitDir, "concordia", "state.json")).Code);
            Assert.Equal(PreToolCommand.Block, Run(new ToolInput { FilePath = Path.Combine(gitDir, "concordia", "state.json") }, linked).Code);
        }
        finally
        {
            Git("worktree", "remove", "--force", linked);
        }
    }

    [Theory]
    [InlineData("rm concordia.json")]
    [InlineData("sed -i s/true/false/ ./concordia.json")]
    [InlineData("echo '{}' > concordia.json")]
    [InlineData("echo '{}'>concordia.json")]
    [InlineData("rm .claude/concordia.json")]
    [InlineData("rm .claude\\concordia.json")]
    [InlineData("rm \"./CONCORDIA.JSON\"")]
    public void A_command_citing_a_committed_config_is_blocked(string command)
    {
        CommitFile("concordia.json", "{}");
        CommitFile(".claude/concordia.json", "{}");

        Assert.Equal(PreToolCommand.Block, Shell(command).Code);
    }

    [Fact]
    public void A_command_citing_the_config_from_a_subfolder_is_blocked()
    {
        CommitFile("concordia.json", "{}");
        var src = Path.Combine(_root, "src");

        Assert.Equal(PreToolCommand.Block, Run(new ToolInput { Command = "rm ../concordia.json" }, src).Code);
        Assert.Equal(PreToolCommand.Block, Run(new ToolInput { Command = "cd .. && rm concordia.json" }, src).Code);
    }

    [Theory]
    [InlineData("rm .git/concordia/state.json")]
    [InlineData("rm -r .git\\concordia")]
    [InlineData("rm -rf .git/concordia")]
    [InlineData("cat .git/worktrees/x/concordia/state.json")]
    public void A_command_citing_the_state_directory_is_blocked(string command)
    {
        Assert.Equal(PreToolCommand.Block, Shell(command).Code);
    }

    [Fact]
    public void A_command_citing_a_config_by_absolute_path_is_blocked_even_with_a_space_in_the_root()
    {
        Assert.Contains(' ', _root);
        CommitFile("concordia.json", "{}");

        Assert.Equal(PreToolCommand.Block, Shell($"rm \"{Path.Combine(_root, "concordia.json")}\"").Code);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("cat samples/concordia.json")]
    [InlineData("cat concordia.json.bak")]
    [InlineData("git log concordia-notes.md")]
    [InlineData("cat .git/config")]
    [InlineData("cat .git/concordia-notes")]
    [InlineData("\"/plugin/concordia-hook\" gate status")]
    public void A_command_citing_something_else_is_allowed(string command)
    {
        CommitFile("concordia.json", "{}");

        Assert.Equal(0, Shell(command).Code);
    }

    [Fact]
    public void A_command_citing_an_uncommitted_config_is_allowed()
    {
        CommitFile("src/App.cs", "class App {}");
        Write("concordia.json", "{}");

        Assert.Equal(0, Shell("cat concordia.json").Code);
    }

    [Fact]
    public void Invalid_committed_config_asks_for_the_user_instead_of_a_fix_it_cannot_make()
    {
        Write("src/App.cs", "class App { }");
        CommitFile("concordia.json", "{ invalid");

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("ask them to fix it", stderr);
        Assert.DoesNotContain("Fix the file", stderr);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("tests")]
    public void A_timeout_blocks_once_and_then_warns_without_running_again(string step)
    {
        UseSlowStep(step);
        Write("src/App.cs", "class App { int X; }");

        var (firstCode, firstOut, firstError) = Stop();
        Assert.Equal(StopCommand.Block, firstCode);
        Assert.Contains($"{step} timed out", firstError);
        Assert.Contains("nothing was verified", firstError);
        Assert.Empty(firstOut);
        Assert.Equal(1, Runs());

        var (secondCode, secondOut, _) = Stop();
        Assert.Equal(0, secondCode);
        Assert.Contains($"{step} timed out", secondOut);
        Assert.Contains("NOTHING was verified", secondOut);
        Assert.Equal(1, Runs());
    }

    [Fact]
    public void A_timeout_blocks_again_when_the_tree_changes()
    {
        UseSlowStep("build");
        Write("src/App.cs", "class App { int X; }");
        Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Equal(0, Stop().Code);

        Write("src/App.cs", "class App { int Y; }");

        Assert.Equal(StopCommand.Block, Stop().Code);
        Assert.Equal(2, Runs());
    }

    [Fact]
    public void A_timeout_that_leaves_files_behind_still_blocks_only_once()
    {
        // With no watch list, the runs.txt the step appends to changes the tree at every run.
        UseSlowStep("tests", watch: "[]");
        Write("src/App.cs", "class App { int X; }");

        Assert.Equal(StopCommand.Block, Stop().Code);

        var (code, stdout, _) = Stop();
        Assert.Equal(0, code);
        Assert.Contains("NOTHING was verified", stdout);
        Assert.Equal(1, Runs());
    }

    [Fact]
    public void Timeouts_on_a_tree_that_keeps_changing_stop_blocking_at_max_attempts()
    {
        UseSlowStep("build");
        for (var i = 0; i < 3; i++)
        {
            Write("src/App.cs", $"class App {{ int X{i}; }}");
            Assert.Equal(StopCommand.Block, Stop().Code);
        }

        Write("src/App.cs", "class App { int Last; }");
        var (code, stdout, stderr) = Stop();

        Assert.Equal(0, code);
        Assert.Contains("build timed out", stdout);
        Assert.Contains("NOTHING was verified", stdout);
        Assert.Empty(stderr);
        Assert.Equal(4, Runs());

        Assert.Equal(0, Stop().Code);
        Assert.Equal(4, Runs());
    }

    [Fact]
    public void Letting_a_timeout_pass_gives_the_next_change_its_own_block()
    {
        UseSlowStep("build");
        for (var i = 0; i < 4; i++)
        {
            Write("src/App.cs", $"class App {{ int X{i}; }}");
            Assert.Equal(StopCommand.Block, Stop().Code);
            Assert.Equal(0, Stop().Code);
        }
    }

    [Fact]
    public void The_stop_hook_outlives_format_build_and_test_at_their_default_timeout()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Concordia.slnx"))) dir = dir.Parent;
        using var hooks = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "plugins/concordia/hooks/hooks.json")));

        var stop = hooks.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];
        var perStep = Settings.From("custom", new ConcordiaConfig()).CommandTimeout.TotalSeconds;

        Assert.True(stop.GetProperty("timeout").GetInt32() > 3 * perStep);
    }

    [Theory]
    [InlineData("""{ "commands": { "format": "", "build": "echo ok", "test": "echo ok" }, "watch": ["**/*.cs"] }""")]
    [InlineData("""{ "gate": { "enabled": false } }""")]
    [InlineData("not json")]
    public void A_config_changed_during_the_session_does_not_weaken_its_gate(string weakened)
    {
        CommitFile("concordia.json", """{ "commands": { "format": "", "build": "exit 1", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        SessionStart();

        // Committed, so that nothing in HEAD would tell the guard or the gate that it changed.
        CommitFile("concordia.json", weakened);
        Write("src/App.cs", "class App { int X; }");

        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("build failed", stderr);
    }

    [Fact]
    public void A_changed_config_is_reported_and_a_new_session_uses_it()
    {
        CommitFile("concordia.json", """{ "commands": { "format": "", "build": "echo ok", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        SessionStart();
        CommitFile("concordia.json", """{ "commands": { "format": "", "build": "exit 1", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        Write("src/App.cs", "class App { int X; }");

        var (code, stdout, _) = Stop();
        Assert.Equal(0, code);
        Assert.Contains("changed during this session", stdout);

        Write("src/App.cs", "class App { int Y; }");
        SessionStart("another-session");
        Write("src/App.cs", "class App { int Z; }");
        var (next, _, stderr) = Stop("another-session");
        Assert.Equal(StopCommand.Block, next);
        Assert.Contains("build failed", stderr);
    }

    [Fact]
    public void A_config_created_during_a_session_that_had_none_does_not_replace_the_preset()
    {
        Write("App.slnx", "<Solution />");
        Git("add", "-A");
        Git("commit", "-q", "-m", "stack");
        SessionStart();

        Write("concordia.json", """{ "gate": { "enabled": false } }""");

        var (code, stdout, _) = Stop();

        Assert.Equal(0, code);
        Assert.Contains("changed during this session", stdout);
    }

    [Fact]
    public void A_config_that_cannot_be_read_at_stop_keeps_the_pinned_one()
    {
        CommitFile("concordia.json", """{ "commands": { "format": "", "build": "exit 1", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        SessionStart();
        Write("src/App.cs", "class App { int X; }");

        using var held = new FileStream(Path.Combine(_root, "concordia.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (code, _, stderr) = Stop();

        Assert.Equal(StopCommand.Block, code);
        Assert.Contains("build failed", stderr);
    }

    [Fact]
    public void A_green_result_does_not_count_for_a_different_config()
    {
        CommitFile("concordia.json", """{ "commands": { "format": "", "build": "echo ok", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        SessionStart();
        Write("src/App.cs", "class App { int X; }");
        Assert.Equal(0, Stop().Code);

        // Back on the tree that went green, now judged by a config that fails. HEAD did not move.
        Write("src/App.cs", "class App { int Y; }");
        Write("concordia.json", """{ "commands": { "format": "", "build": "exit 1", "test": "echo ok" }, "watch": ["**/*.cs"] }""");
        SessionStart("another-session");
        Write("src/App.cs", "class App { int X; }");

        Assert.Equal(StopCommand.Block, Stop("another-session").Code);
    }

    private void SessionStart(string session = "test-session") =>
        SessionStartCommand.Run(new HookInput { Cwd = _root, SessionId = session }, new StringWriter());

    private void UseSlowStep(string step, string watch = """["**/*.cs"]""")
    {
        var slow = OperatingSystem.IsWindows()
            ? "echo x>> runs.txt & ping -n 6 127.0.0.1 > nul"
            : "echo x >> runs.txt; sleep 5";
        var build = step == "build" ? slow : "echo ok";
        var test = step == "tests" ? slow : "echo ok";
        Write("concordia.json", $$"""
            {
              "commands": { "format": "", "build": "{{build}}", "test": "{{test}}" },
              "watch": {{watch}},
              "gate": { "timeoutSeconds": 1 }
            }
            """);
        Git("add", "-A");
        Git("commit", "-q", "-m", "slow step");
    }

    private int Runs() => File.ReadAllLines(Path.Combine(_root, "runs.txt")).Length;

    private (int Code, string StdErr) Edit(string path) => Run(new ToolInput { FilePath = path }, _root);

    private (int Code, string StdErr) Shell(string command) => Run(new ToolInput { Command = command }, _root);

    private static (int Code, string StdErr) Run(ToolInput tool, string cwd)
    {
        var stderr = new StringWriter();
        var code = PreToolCommand.Run(new HookInput { Cwd = cwd, ToolInput = tool }, stderr);
        return (code, stderr.ToString());
    }

    private (int Code, string StdOut, string StdErr) Stop(string session = "test-session")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = StopCommand.Run(new HookInput { Cwd = _root, SessionId = session }, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void CommitFile(string relative, string content)
    {
        Write(relative, content);
        Git("add", "-A");
        Git("commit", "-q", "-m", $"add {relative}");
    }

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
