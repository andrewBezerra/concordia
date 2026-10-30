using Concordia.Hook.Gate;

namespace Concordia.Hook.Tests;

public class OutputSummarizerTests
{
    private const string Root = "/repo";

    [Fact]
    public void MsBuild_errors_are_deduplicated_relativized_and_warnings_dropped()
    {
        const string output = """
            /repo/src/Api/Program.cs(12,5): error CS1002: ; expected [/repo/src/Api/Api.csproj]
            /repo/src/Api/Program.cs(20,1): warning CS0168: The variable 'x' is declared but never used [/repo/src/Api/Api.csproj]
            /repo/src/Api/Program.cs(12,5): error CS1002: ; expected [/repo/src/Api/Api.csproj]
            """;

        var summary = OutputSummarizer.Summarize(output, Root, 30);

        Assert.Equal("src/Api/Program.cs(12,5): CS1002 ; expected", summary);
    }

    [Fact]
    public void VsTest_failure_keeps_message_and_first_source_frame_only()
    {
        const string output = """
            Starting test execution, please wait...
              Failed Api.Tests.OrderTests.Total_includes_tax [12 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
            Expected: 110
            Actual:   100
              Stack Trace:
                 at Api.Tests.OrderTests.Total_includes_tax() in /repo/tests/Api.Tests/OrderTests.cs:line 21
                 at System.RuntimeMethodHandle.InvokeMethod(Object target)

            Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11
            """;

        var lines = OutputSummarizer.Summarize(output, Root, 30).Split('\n');

        Assert.Equal("Failed Api.Tests.OrderTests.Total_includes_tax [12 ms]", lines[0]);
        Assert.Contains("  Assert.Equal() Failure: Values differ", lines);
        Assert.Contains("  Expected: 110", lines);
        Assert.Contains("  at Api.Tests.OrderTests.Total_includes_tax() in tests/Api.Tests/OrderTests.cs:line 21", lines);
        Assert.DoesNotContain(lines, l => l.Contains("RuntimeMethodHandle"));
    }

    [Fact]
    public void Unknown_format_falls_back_to_the_tail()
    {
        var output = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));

        var lines = OutputSummarizer.Summarize(output, Root, 5).Split('\n');

        Assert.Equal(new[] { "line 46", "line 47", "line 48", "line 49", "line 50" }, lines);
    }

    [Fact]
    public void Output_is_capped_with_an_omission_note()
    {
        var output = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"/repo/F{i}.cs(1,1): error CS0001: boom"));

        var lines = OutputSummarizer.Summarize(output, Root, 3).Split('\n');

        Assert.Equal(4, lines.Length);
        Assert.Equal("... 7 more line(s) omitted", lines[^1]);
    }
}
