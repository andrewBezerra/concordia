#:property PublishAot=false
#:property Nullable=enable

// Concordia benchmark runner (.NET 10 file-based app).
//
//   dotnet run benchmark/runner/run.cs -- [--variants baseline,concordia] [--tasks S01,L01] [--runs 3]
//                                         [--model <model>] [--shared-config] [--keep]
//   dotnet run benchmark/runner/run.cs -- --check-source <app folder> --tasks L01
//
// For every task x variant x run it copies benchmark/app into a fresh git repository, runs Claude Code
// headless with the task prompt, runs the agent's own test suite, puts the original visible tests back,
// then copies the hidden acceptance tests in and runs both. It also applies the task's source checks.
// --check-source applies only the source checks to an existing folder, with no AI involved.
// Results go to benchmark/results/<timestamp>/results.csv and summary.md.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var options = Options.Parse(args);
var bench = Paths.FindBenchmarkDir();
var repoRoot = Path.GetFullPath(Path.Combine(bench, ".."));
var variants = Variant.LoadAll(Path.Combine(bench, "variants.json"), repoRoot)
    .Where(v => options.Variants is null || options.Variants.Contains(v.Name, StringComparer.OrdinalIgnoreCase))
    .ToList();
var tasks = BenchTask.LoadAll(Path.Combine(bench, "tasks"))
    .Where(t => options.Tasks is null || options.Tasks.Contains(t.Id, StringComparer.OrdinalIgnoreCase))
    .ToList();

if (variants.Count == 0 || tasks.Count == 0)
{
    Console.Error.WriteLine("Nothing to run: check --variants and --tasks.");
    return 1;
}

if (options.CheckSource is not null)
{
    return SourceCheck.Report(options.CheckSource, tasks);
}

if (!options.SharedConfig && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("Isolated config directories need ANTHROPIC_API_KEY. Set it, or pass --shared-config to use your normal login (plugins you installed will then affect every variant).");
    return 1;
}

var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var resultsDir = Path.Combine(bench, "results", stamp);
Directory.CreateDirectory(resultsDir);
var rows = new List<RunResult>();

Console.WriteLine($"Running {tasks.Count} task(s) x {variants.Count} variant(s) x {options.Runs} run(s). Results: {resultsDir}");

foreach (var task in tasks)
foreach (var variant in variants)
for (var run = 1; run <= options.Runs; run++)
{
    Console.WriteLine($"\n== {task.Id} ({task.Size}) · {variant.Name} · run {run}");
    var result = RunOne(task, variant, run);
    rows.Add(result);
    Console.WriteLine($"   hidden {result.HiddenPassed}/{result.HiddenTotal} · visible {result.VisiblePassed}/{result.VisibleTotal} · own suite {result.OwnSuitePassed}/{result.OwnSuiteTotal} · source checks {result.SourceChecksPassed}/{result.SourceChecksTotal} · ${result.CostUsd:0.00} · {result.DurationSeconds:0}s · {result.Turns} turns");
    Report.Write(resultsDir, rows);
}

Console.WriteLine($"\nDone. Summary: {Path.Combine(resultsDir, "summary.md")}");
return 0;

RunResult RunOne(BenchTask task, Variant variant, int run)
{
    var work = Path.Combine(Path.GetTempPath(), "concordia-bench", $"{stamp}-{task.Id}-{variant.Name}-{run}");
    var app = Path.Combine(work, "app");
    Paths.Copy(Path.Combine(bench, "app"), app);

    Shell.Run("git", ["init", "-q"], app);
    Shell.Run("git", ["add", "-A"], app);
    Shell.Run("git", ["-c", "user.name=bench", "-c", "user.email=bench@localhost", "commit", "-q", "-m", "base"], app);

    var prompt = variant.PromptFor(task.Kind, File.ReadAllText(task.PromptPath));
    var claudeArgs = new List<string> { "-p", prompt, "--output-format", "json", "--permission-mode", "bypassPermissions" };
    claudeArgs.AddRange(variant.Args);
    if (options.Model is not null) claudeArgs.AddRange(["--model", options.Model]);

    var env = new Dictionary<string, string>();
    if (!options.SharedConfig)
    {
        var configDir = Path.Combine(bench, ".config", variant.Name);
        Directory.CreateDirectory(configDir);
        env["CLAUDE_CONFIG_DIR"] = configDir;
    }

    var claude = Shell.Run("claude", claudeArgs, app, TimeSpan.FromMinutes(60), env);
    File.WriteAllText(Path.Combine(work, "claude-output.json"), claude.StdOut);
    var usage = ClaudeUsage.Parse(claude.StdOut);

    var score = Scoring.Score(bench, task, app, work);

    var result = new RunResult(task.Id, task.Size, task.Kind, variant.Name, run,
        score.Hidden.Passed, score.Hidden.Total, score.Visible.Passed, score.Visible.Total, score.Hidden.BuildOk && score.Visible.BuildOk,
        score.OwnSuite.Passed, score.OwnSuite.Total, score.SourceChecksPassed, task.SourceChecks.Count,
        usage.CostUsd, usage.DurationMs / 1000.0, usage.Turns, usage.InputTokens, usage.OutputTokens,
        usage.CacheReadTokens, usage.CacheWriteTokens, usage.IsError || claude.TimedOut, work);

    if (!options.Keep && result.Success)
    {
        Paths.TryDelete(work);
    }

    return result;
}

sealed record Options(string[]? Variants, string[]? Tasks, int Runs, string? Model, bool SharedConfig, bool Keep, string? CheckSource)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string[]? List(string name) => Value(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new Options(
            List("--variants"),
            List("--tasks"),
            int.TryParse(Value("--runs"), out var runs) && runs > 0 ? runs : 1,
            Value("--model"),
            args.Contains("--shared-config"),
            args.Contains("--keep"),
            Value("--check-source"));
    }
}

sealed record Variant(string Name, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Prefix)
{
    /// <summary>
    /// Headless runs have nobody to approve a plan. Every variant and task gets this sentence, in these
    /// words, so that no setup stops early or gains from its own way of asking for approval.
    /// </summary>
    public const string HeadlessNote = "No human is available during this run and any plan counts as approved, so continue without waiting for approval.";

    public string PrefixFor(string kind) => Prefix.TryGetValue(kind, out var p) ? p : string.Empty;

    public string PromptFor(string kind, string taskText) => $"{PrefixFor(kind)}{taskText.Trim()}\n\n{HeadlessNote}";

    public static IEnumerable<Variant> LoadAll(string path, string repoRoot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var args = property.Value.GetProperty("args").EnumerateArray()
                .Select(a => a.GetString()!.Replace("{repo}", repoRoot, StringComparison.Ordinal))
                .ToList();
            var prefix = property.Value.TryGetProperty("prefix", out var p)
                ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? string.Empty)
                : new Dictionary<string, string>();
            yield return new Variant(property.Name, args, prefix);
        }
    }
}

sealed record BenchTask(string Id, string Size, string Kind, string PromptPath, string HiddenDir, int HiddenTestCount, IReadOnlyList<SourceCheck> SourceChecks)
{
    public static IEnumerable<BenchTask> LoadAll(string tasksDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(tasksDir).Order(StringComparer.Ordinal))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var root = doc.RootElement;
            var hidden = Path.Combine(dir, "hidden");
            var count = Scoring.CountTests(hidden);
            yield return new BenchTask(
                root.GetProperty("id").GetString()!,
                root.GetProperty("size").GetString()!,
                root.GetProperty("kind").GetString()!,
                Path.Combine(dir, "task.md"),
                hidden,
                count,
                SourceCheck.Load(root));
        }
    }
}

sealed record RunResult(
    string Task, string Size, string Kind, string Variant, int Run,
    int HiddenPassed, int HiddenTotal, int VisiblePassed, int VisibleTotal, bool BuildOk,
    int OwnSuitePassed, int OwnSuiteTotal, int SourceChecksPassed, int SourceChecksTotal,
    double CostUsd, double DurationSeconds, int Turns, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheWriteTokens, bool AgentError, string WorkDir)
{
    public bool Success => BuildOk && HiddenTotal > 0 && HiddenPassed == HiddenTotal && VisiblePassed == VisibleTotal && SourceChecksPassed == SourceChecksTotal;
}

sealed record ClaudeUsage(double CostUsd, double DurationMs, int Turns, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, bool IsError)
{
    public static ClaudeUsage Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var usage = r.TryGetProperty("usage", out var u) ? u : default;

            double Num(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

            return new ClaudeUsage(
                Num(r, "total_cost_usd"),
                Num(r, "duration_ms"),
                (int)Num(r, "num_turns"),
                (long)Num(usage, "input_tokens"),
                (long)Num(usage, "output_tokens"),
                (long)Num(usage, "cache_read_input_tokens"),
                (long)Num(usage, "cache_creation_input_tokens"),
                r.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return new ClaudeUsage(0, 0, 0, 0, 0, 0, 0, IsError: true);
        }
    }
}

sealed record Score(TestRun OwnSuite, TestRun Hidden, TestRun Visible, int SourceChecksPassed);

/// <summary>
/// One thing the task's prompt requires that HTTP and IClock cannot observe, such as a dependency or a
/// constant-time comparison. It reads the agent's <c>src</c> folder: the check holds when a file named
/// by <see cref="Files"/> matches <see cref="Pattern"/>, or when none does if <see cref="Absent"/> is set.
/// A text match proves that the code is there, not that it runs.
/// </summary>
sealed record SourceCheck(string Name, string Files, string Pattern, bool Absent)
{
    public static IReadOnlyList<SourceCheck> Load(JsonElement meta)
    {
        if (!meta.TryGetProperty("sourceChecks", out var checks)) return [];
        return checks.EnumerateArray()
            .Select(c => new SourceCheck(
                c.GetProperty("name").GetString()!,
                c.GetProperty("files").GetString()!,
                c.GetProperty("pattern").GetString()!,
                c.TryGetProperty("absent", out var absent) && absent.GetBoolean()))
            .ToList();
    }

    public bool Holds(string app)
    {
        var found = Paths.SourceFiles(Path.Combine(app, "src"), Files).Any(f => Regex.IsMatch(File.ReadAllText(f), Pattern));
        return found != Absent;
    }

    /// <summary>The --check-source mode: lists the failures and exits with 1 if there are any.</summary>
    public static int Report(string app, IEnumerable<BenchTask> tasks)
    {
        var failed = 0;
        foreach (var task in tasks)
        foreach (var check in task.SourceChecks.Where(c => !c.Holds(app)))
        {
            Console.WriteLine($"{task.Id}: {check.Name}");
            failed++;
        }

        return failed == 0 ? 0 : 1;
    }
}

static class Scoring
{
    private const string TestProject = "tests/VetAgenda.Tests/VetAgenda.Tests.csproj";

    /// <summary>
    /// Scores the agent's finished work. Its own suite runs first, exactly as the agent left it, and is
    /// only reported. Then every variant is judged by the same oracle: the original visible tests and
    /// the hidden tests, against the agent's production code.
    /// </summary>
    public static Score Score(string bench, BenchTask task, string app, string work)
    {
        var ownSuite = TestRun.Execute(app, null, null, Path.Combine(work, "own-suite"), 0);
        var sourceChecksPassed = task.SourceChecks.Count(c => c.Holds(app));

        RestoreVisibleTests(bench, app);

        // Hidden acceptance tests are copied in only after the agent has finished.
        var hiddenDir = Path.Combine(app, "tests", "VetAgenda.Tests", "Hidden");
        Paths.Copy(Path.Combine(bench, "tasks", "_shared"), hiddenDir);
        Paths.Copy(task.HiddenDir, Path.Combine(hiddenDir, task.Id));

        // The project, not the solution: the agent's solution may still list test projects removed above.
        var hidden = TestRun.Execute(app, TestProject, "FullyQualifiedName~VetAgenda.Tests.Hidden", Path.Combine(work, "hidden"), task.HiddenTestCount);
        var visible = TestRun.Execute(app, TestProject, "FullyQualifiedName!~VetAgenda.Tests.Hidden", Path.Combine(work, "visible"), CountTests(Path.Combine(bench, "app", "tests")));
        return new Score(ownSuite, hidden, visible, sourceChecksPassed);
    }

    /// <summary>
    /// Replaces the whole tests folder rather than copying over it, so a test the agent added cannot
    /// break the build when it relied on a helper it changed, and cannot count as a visible test.
    /// </summary>
    public static void RestoreVisibleTests(string bench, string app)
    {
        var tests = Path.Combine(app, "tests");
        if (Directory.Exists(tests)) Paths.Delete(tests);
        Paths.Copy(Path.Combine(bench, "app", "tests"), tests);
    }

    public static int CountTests(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Sum(text => text.Split("[Fact").Length - 1 + text.Split("[Theory").Length - 1);
}

sealed record TestRun(int Passed, int Total, bool BuildOk)
{
    public static TestRun Execute(string app, string? target, string? filter, string resultsDir, int expectedTotal)
    {
        var args = new List<string> { "test" };
        if (target is not null) args.Add(target);
        args.AddRange(["--nologo", "--logger", "trx;LogFilePrefix=results", "--results-directory", resultsDir]);
        if (filter is not null) args.AddRange(["--filter", filter]);
        var result = Shell.Run("dotnet", args, app, TimeSpan.FromMinutes(15));

        // One results file per test project; the agent's own suite may have more than one.
        var trx = Directory.Exists(resultsDir) ? Directory.EnumerateFiles(resultsDir, "*.trx", SearchOption.AllDirectories).ToList() : [];
        if (trx.Count == 0)
        {
            // No results file: the solution did not build (or no test matched).
            File.WriteAllText(resultsDir + ".log", result.StdOut + result.StdErr);
            return new TestRun(0, expectedTotal, BuildOk: false);
        }

        var counters = trx.Select(f => XDocument.Load(f).Descendants().First(e => e.Name.LocalName == "Counters")).ToList();
        int Sum(string name) => counters.Sum(c => int.TryParse(c.Attribute(name)?.Value, out var v) ? v : 0);
        return new TestRun(Sum("passed"), Math.Max(Sum("total"), expectedTotal), BuildOk: true);
    }
}

sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

static class Shell
{
    public static ShellResult Run(string file, IEnumerable<string> args, string cwd, TimeSpan? timeout = null, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (key, value) in env ?? new Dictionary<string, string>()) info.Environment[key] = value;

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        var timedOut = !process.WaitForExit(limit);
        if (timedOut)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        return new ShellResult(timedOut ? -1 : process.ExitCode, stdout.Result, stderr.Result, timedOut);
    }
}

static class Paths
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", "TestResults" };

    public static string FindBenchmarkDir()
    {
        foreach (var candidate in new[] { Path.Combine(Environment.CurrentDirectory, "benchmark"), Environment.CurrentDirectory })
        {
            if (File.Exists(Path.Combine(candidate, "variants.json"))) return Path.GetFullPath(candidate);
        }

        throw new InvalidOperationException("Run from the repository root or the benchmark folder.");
    }

    public static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if (Skip.Contains(Path.GetFileName(dir))) continue;
            Copy(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>The agent's source files with this name pattern, without build output.</summary>
    public static IEnumerable<string> SourceFiles(string dir, string pattern) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar).Any(Skip.Contains))
            : [];

    public static void Delete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    public static void TryDelete(string dir)
    {
        try
        {
            Delete(dir);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

static class Report
{
    public static void Write(string dir, IReadOnlyList<RunResult> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("task,size,kind,variant,run,success,hidden_passed,hidden_total,visible_passed,visible_total,build_ok,own_suite_passed,own_suite_total,source_checks_passed,source_checks_total,cost_usd,duration_s,turns,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,agent_error,workdir\n");
        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(',',
                r.Task, r.Size, r.Kind, r.Variant, r.Run, r.Success, r.HiddenPassed, r.HiddenTotal, r.VisiblePassed, r.VisibleTotal, r.BuildOk,
                r.OwnSuitePassed, r.OwnSuiteTotal, r.SourceChecksPassed, r.SourceChecksTotal,
                r.CostUsd.ToString("0.0000", inv), r.DurationSeconds.ToString("0.0", inv), r.Turns, r.InputTokens, r.OutputTokens,
                r.CacheReadTokens, r.CacheWriteTokens, r.AgentError, $"\"{r.WorkDir}\""));
        }

        File.WriteAllText(Path.Combine(dir, "results.csv"), csv.ToString());

        var md = new StringBuilder("# Benchmark summary\n\n");
        md.AppendLine("Success = every hidden acceptance test, every original pre-existing test and every source check passes. Tests the agent added appear only in the `own_suite` columns of `results.csv` and do not affect success.\n");
        md.AppendLine("| Size | Variant | Runs | Success rate | Hidden tests passed | Mean cost (USD) | Mean time (s) | Mean turns |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var g in rows.GroupBy(r => (r.Size, r.Variant)).OrderBy(g => g.Key.Size).ThenBy(g => g.Key.Variant))
        {
            md.AppendLine(Line(g.Key.Size, g.Key.Variant, g.ToList(), inv));
        }

        md.AppendLine("\n## Per task\n");
        md.AppendLine("| Task | Variant | Runs | Success rate | Hidden tests passed | Mean cost (USD) | Mean time (s) | Mean turns |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var g in rows.GroupBy(r => (r.Task, r.Variant)).OrderBy(g => g.Key.Task).ThenBy(g => g.Key.Variant))
        {
            md.AppendLine(Line(g.Key.Task, g.Key.Variant, g.ToList(), inv));
        }

        File.WriteAllText(Path.Combine(dir, "summary.md"), md.ToString());
    }

    private static string Line(string key, string variant, List<RunResult> runs, CultureInfo inv)
    {
        var success = runs.Count(r => r.Success) * 100.0 / runs.Count;
        var hidden = runs.Sum(r => r.HiddenTotal) == 0 ? 0 : runs.Sum(r => r.HiddenPassed) * 100.0 / runs.Sum(r => r.HiddenTotal);
        return $"| {key} | {variant} | {runs.Count} | {success.ToString("0", inv)}% | {hidden.ToString("0", inv)}% | " +
               $"{runs.Average(r => r.CostUsd).ToString("0.00", inv)} | {runs.Average(r => r.DurationSeconds).ToString("0", inv)} | {runs.Average(r => r.Turns).ToString("0.0", inv)} |";
    }
}
