using Concordia.Hook.Gate;

namespace Concordia.Hook.Tests;

public class OutputSummarizerTests
{
    private const string Root = "/repo";

    [Theory]
    [InlineData("Common.targets(5096,5): error MSB3027: Could not copy a.dll. The file is locked by: testhost [a.csproj]\nCommon.targets(5096,5): error MSB3021: Unable to copy file a.dll. [a.csproj]", true)]
    [InlineData("Common.targets(5096,5): error MSB3027: Could not copy a.dll. [a.csproj]", true)]
    [InlineData("Common.targets(5096,5): error MSB3021: Access to the path a.dll is denied. [a.csproj]", false)]
    [InlineData("Common.targets(5096,5): error MSB3027: Could not copy a.dll.\nsrc/App.cs(1,1): error CS1002: ; expected", false)]
    [InlineData("Common.targets(5096,5): warning MSB3026: Could not copy a.dll. Beginning retry 1.", false)]
    [InlineData("  Failed App.Tests.Lock [1 ms]\n  error MSB3027: quoted in a test message", false)]
    [InlineData("", false)]
    public void Only_locked_output_files_are_recognized_as_such(string output, bool expected) =>
        Assert.Equal(expected, OutputSummarizer.FailedOnlyOnLockedFiles(output));

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

    [Fact]
    public void Pytest_short_summary_is_kept()
    {
        const string output = """
            ============================= test session starts ==============================
            collected 12 items

            tests/test_cart.py ..F.........                                          [100%]
            =========================== short test summary info ============================
            FAILED tests/test_cart.py::test_discount - assert 90 == 81
            ========================= 1 failed, 11 passed in 0.31s =========================
            """;

        var summary = OutputSummarizer.Summarize(output, Root, 30);

        Assert.StartsWith("FAILED tests/test_cart.py::test_discount - assert 90 == 81", summary);
    }

    [Fact]
    public void Jest_failure_keeps_the_name_message_and_first_source_frame()
    {
        const string output = """
             FAIL  src/cart.test.ts
              ● cart › applies the discount

                expect(received).toBe(expected) // Object.is equality

                Expected: 81
                Received: 90

                  at Object.<anonymous> (src/cart.test.ts:12:23)
                  at Promise.then.completed (node_modules/jest-circus/build/utils.js:298:28)
            """;

        var lines = OutputSummarizer.Summarize(output, Root, 30).Split('\n');

        Assert.Contains("FAIL  src/cart.test.ts", lines);
        Assert.Contains("● cart › applies the discount", lines);
        Assert.Contains(lines, l => l.Contains("Expected: 81"));
        Assert.Contains("  at Object.<anonymous> (src/cart.test.ts:12:23)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("node_modules"));
    }

    [Fact]
    public void Maven_errors_are_kept_without_the_closing_advice()
    {
        const string output = """
            [ERROR] /repo/src/main/java/shop/Cart.java:[14,9] cannot find symbol
            [ERROR] Failed to execute goal org.apache.maven.plugins:maven-compiler-plugin:3.13.0:compile (default-compile) on project shop: Compilation failure
            [ERROR] -> [Help 1]
            [ERROR] Re-run Maven using the -X switch to enable full debug logging.
            """;

        var summary = OutputSummarizer.Summarize(output, Root, 30);

        Assert.Equal("[ERROR] src/main/java/shop/Cart.java:[14,9] cannot find symbol", summary);
    }

    [Fact]
    public void Gradle_and_javac_failures_are_kept()
    {
        const string output = """
            > Task :compileJava FAILED
            src/main/java/shop/Cart.java:14: error: cannot find symbol
            CartTest > appliesDiscount() FAILED
                org.opentest4j.AssertionFailedError: expected: <81> but was: <90>
                    at app//shop.CartTest.appliesDiscount(CartTest.java:21)
            """;

        var lines = OutputSummarizer.Summarize(output, Root, 30).Split('\n');

        Assert.Contains("src/main/java/shop/Cart.java:14: error: cannot find symbol", lines);
        Assert.Contains("CartTest > appliesDiscount() FAILED", lines);
        Assert.Contains("  org.opentest4j.AssertionFailedError: expected: <81> but was: <90>", lines);
        Assert.Contains("  at app//shop.CartTest.appliesDiscount(CartTest.java:21)", lines);
    }

    [Fact]
    public void TypeScript_compiler_errors_are_kept()
    {
        const string output = "src/cart.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.";

        Assert.Equal("src/cart.ts(12,5): TS2322 Type 'string' is not assignable to type 'number'.", OutputSummarizer.Summarize(output, Root, 30));
    }
}
