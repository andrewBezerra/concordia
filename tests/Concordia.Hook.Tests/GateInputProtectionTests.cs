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
    public void The_stop_hook_outlives_format_build_and_test_at_their_default_timeout()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Concordia.slnx"))) dir = dir.Parent;
        using var hooks = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "plugins/concordia/hooks/hooks.json")));

        var stop = hooks.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];
        var perStep = Settings.From("custom", new ConcordiaConfig()).CommandTimeout.TotalSeconds;

        Assert.True(stop.GetProperty("timeout").GetInt32() > 3 * perStep);
    }

    private void UseSlowStep(string step)
    {
        var slow = OperatingSystem.IsWindows()
            ? "echo x>> runs.txt & ping -n 6 127.0.0.1 > nul"
            : "echo x >> runs.txt; sleep 5";
        var build = step == "build" ? slow : "echo ok";
        var test = step == "tests" ? slow : "echo ok";
        Write("concordia.json", $$"""
            {
              "commands": { "format": "", "build": "{{build}}", "test": "{{test}}" },
              "watch": ["**/*.cs"],
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

    private (int Code, string StdOut, string StdErr) Stop()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = StopCommand.Run(new HookInput { Cwd = _root, SessionId = "test-session" }, stdout, stderr);
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
