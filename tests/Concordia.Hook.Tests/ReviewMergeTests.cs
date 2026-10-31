using Concordia.Hook.Commands;
using Concordia.Hook.Review;

namespace Concordia.Hook.Tests;

public class ReviewMergeTests
{
    private static string Answer(string lens, params string[] findings) =>
        $$"""{ "lens": "{{lens}}", "verdict": "{{(findings.Length == 0 ? "approve" : "changes_requested")}}", "findings": [{{string.Join(",", findings)}}], "escalate": [] }""";

    private static string Finding(string file, int line, string severity, double confidence, string title) =>
        $$"""{ "file": "{{file}}", "line": {{line}}, "severity": "{{severity}}", "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "title": "{{title}}", "scenario": "s", "fix": "f" }""";

    private static MergeResult Merge(params string[] answers) =>
        ReviewMerger.Merge(answers.Select((text, i) => new ReviewInput($"r{i + 1}.json", text)).ToList());

    [Fact]
    public void Same_finding_from_two_lenses_is_reported_once_with_both_lenses()
    {
        var result = Merge(
            Answer("general", Finding("src/A.cs", 42, "medium", 0.6, "Total ignores discounts")),
            Answer("domain", Finding("src/A.cs", 42, "medium", 0.8, "Total ignores discounts")));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(0.8, finding.Confidence);
        Assert.Equal(["general", "domain"], finding.Lenses);
    }

    [Fact]
    public void Nearby_lines_with_a_title_that_differs_only_in_case_and_spacing_are_duplicates()
    {
        var near = 42 + ReviewMerger.NearbyLines;
        var result = Merge(
            Answer("general", Finding("src/A.cs", 42, "low", 0.7, "Total  ignores discounts")),
            Answer("domain", Finding("src\\\\A.cs", near, "low", 0.7, "total ignores Discounts")));

        Assert.Single(result.Findings);
    }

    [Fact]
    public void Lines_further_than_the_limit_or_other_files_or_other_titles_are_kept_apart()
    {
        var far = 42 + ReviewMerger.NearbyLines + 1;
        var result = Merge(
            Answer("general",
                Finding("src/A.cs", 42, "low", 0.7, "Same"),
                Finding("src/A.cs", far, "low", 0.7, "Same"),
                Finding("src/B.cs", 42, "low", 0.7, "Same"),
                Finding("src/A.cs", 42, "low", 0.7, "Other")));

        Assert.Equal(4, result.Findings.Count);
    }

    [Fact]
    public void Conflicting_severities_keep_the_highest_and_the_highest_confidence()
    {
        var result = Merge(
            Answer("general", Finding("src/A.cs", 10, "low", 0.9, "Null deref")),
            Answer("security", Finding("src/A.cs", 11, "high", 0.55, "Null deref")),
            Answer("domain", Finding("src/A.cs", 10, "medium", 0.7, "Null deref")));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("high", finding.Severity);
        Assert.Equal(0.9, finding.Confidence);
    }

    [Fact]
    public void Findings_are_sorted_by_severity_then_confidence()
    {
        var result = Merge(Answer("general",
            Finding("src/A.cs", 1, "low", 0.9, "c"),
            Finding("src/A.cs", 100, "high", 0.6, "a"),
            Finding("src/A.cs", 200, "medium", 0.8, "b"),
            Finding("src/A.cs", 300, "high", 0.9, "d")));

        Assert.Equal(["d", "a", "b", "c"], result.Findings.Select(f => f.Title));
    }

    [Theory]
    [InlineData("medium", 0.7, "changes_requested")]
    [InlineData("high", 0.95, "changes_requested")]
    [InlineData("medium", 0.69, "approve")]
    [InlineData("low", 0.99, "approve")]
    public void Verdict_follows_the_severity_and_confidence_threshold(string severity, double confidence, string expected)
    {
        var result = Merge(Answer("general", Finding("src/A.cs", 1, severity, confidence, "t")));

        Assert.Equal(expected, result.Verdict);
    }

    [Theory]
    [InlineData("{}", "lens is missing")]
    [InlineData("""{"lens":"general","findings":[]}""", "verdict must be")]
    [InlineData("""{"lens":"general","verdict":"maybe","findings":[]}""", "verdict must be")]
    [InlineData("""{"lens":"general","verdict":"approve"}""", "findings is missing")]
    [InlineData("""{"lens":"general","verdict":"approve","findings":null}""", "findings is missing")]
    [InlineData("""{"lens":"general","verdict":"changes_requested","findings":[]}""", "changes_requested with no findings")]
    [InlineData("""{"lens":"general","verdict":"approve","findings":[null]}""", "finding 1 is null")]
    [InlineData("""{"lens":"general","verdict":"approve","findings":[],"escalate":[null]}""", "escalate 1 is null")]
    public void An_incomplete_answer_never_approves(string answer, string reason)
    {
        var result = Merge(answer);

        Assert.Equal("incomplete", result.Verdict);
        Assert.Contains(reason, Assert.Single(result.Invalid).Reason);
    }

    [Fact]
    public void An_empty_review_is_approved_with_no_findings()
    {
        var result = Merge(Answer("general"), Answer("security"));

        Assert.Empty(result.Findings);
        Assert.Empty(result.Invalid);
        Assert.Equal("approve", result.Verdict);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "lens": "general", "findings": [ { "file": "a.cs", "line": 1, "severity": "urgent", "confidence": 0.9, "title": "t" } ] }""")]
    [InlineData("""{ "lens": "general", "findings": [ { "file": "a.cs", "line": 1, "severity": "high", "title": "t" } ] }""")]
    [InlineData("""{ "lens": "general", "findings": [ { "line": 1, "severity": "high", "confidence": 0.9, "title": "t" } ] }""")]
    public void An_invalid_answer_is_reported_and_never_ends_in_approve(string text)
    {
        var result = Merge(Answer("general"), text);

        var invalid = Assert.Single(result.Invalid);
        Assert.Equal("r2.json", invalid.Source);
        Assert.NotEmpty(invalid.Reason);
        Assert.Equal("incomplete", result.Verdict);
    }

    [Fact]
    public void An_invalid_answer_does_not_hide_the_findings_of_the_valid_ones()
    {
        var result = Merge("{ broken", Answer("general", Finding("src/A.cs", 1, "high", 0.9, "t")));

        Assert.Single(result.Findings);
        Assert.Single(result.Invalid);
        Assert.Equal("changes_requested", result.Verdict);
    }

    [Fact]
    public void A_missing_answer_text_is_invalid()
    {
        var result = ReviewMerger.Merge([new ReviewInput("gone.json", null)]);

        Assert.Single(result.Invalid);
        Assert.Equal("incomplete", result.Verdict);
    }

    [Fact]
    public void An_answer_wrapped_in_a_markdown_fence_is_accepted()
    {
        var result = Merge("```json\n" + Answer("general", Finding("src/A.cs", 1, "low", 0.8, "t")) + "\n```");

        Assert.Empty(result.Invalid);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Escalations_are_collected_once_per_lens()
    {
        var one = """{ "lens": "general", "verdict": "approve", "findings": [], "escalate": [ { "lens": "performance", "reason": "Loads all orders" } ] }""";
        var two = """{ "lens": "domain", "verdict": "approve", "findings": [], "escalate": [ { "lens": "performance", "reason": "N+1" } ] }""";

        var result = Merge(one, two);

        var escalation = Assert.Single(result.Escalations);
        Assert.Equal("performance", escalation.Lens);
    }

    [Fact]
    public void The_command_reads_files_reports_the_missing_one_and_prints_the_verdict()
    {
        var dir = Directory.CreateTempSubdirectory("concordia-merge").FullName;
        try
        {
            var good = Path.Combine(dir, "general.json");
            File.WriteAllText(good, Answer("general", Finding("src/A.cs", 3, "high", 0.9, "Boom")));
            var stdout = new StringWriter();

            var code = ReviewMergeCommand.Run([good, Path.Combine(dir, "missing.json")], stdout, new StringWriter());

            var text = stdout.ToString();
            Assert.Equal(0, code);
            Assert.Contains("Boom", text);
            Assert.Contains("INVALID", text);
            Assert.Contains("missing.json", text);
            Assert.Contains("Verdict: changes_requested", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_command_without_files_is_a_usage_error()
    {
        var stderr = new StringWriter();

        Assert.Equal(1, ReviewMergeCommand.Run([], new StringWriter(), stderr));
        Assert.Contains("review-merge", stderr.ToString());
    }
}
