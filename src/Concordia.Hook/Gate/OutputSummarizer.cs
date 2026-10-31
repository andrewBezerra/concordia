using System.Text.RegularExpressions;

namespace Concordia.Hook.Gate;

/// <summary>
/// Turns build and test output into the few lines the agent needs. Everything printed here goes
/// into the model context, so this class is where Concordia saves tokens.
/// </summary>
internal static partial class OutputSummarizer
{
    private const int MaxLineLength = 300;
    private const int LinesAfterFailure = 8;

    [GeneratedRegex(@"^\s*(?<loc>.+?\(\d+,\d+\)): (?<kind>error|warning) (?<code>[A-Za-z]+\d+): (?<msg>.+?)(?: \[[^\]]+\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildDiagnostic();

    [GeneratedRegex(@"(?:^|\s)error\s+[A-Z]{2,}\d+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex GenericError();

    [GeneratedRegex(@"(?:^|\s)error\s+(?<code>[A-Z]+\d+)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCode();

    /// <summary>javac, Gradle, gcc-style and Python tools: <c>File.java:12: error: cannot find symbol</c>.</summary>
    [GeneratedRegex(@"^\S[^:]*:\d+(?::\d+)?:\s*error:", RegexOptions.CultureInvariant)]
    private static partial Regex ColonError();

    /// <summary>A stack frame that points at a source line, in .NET, Java, Kotlin, JavaScript, TypeScript or Python.</summary>
    [GeneratedRegex(@":line \d+|\.(?:cs|fs|java|kt|ts|tsx|js|jsx|mjs|cjs|py):\d+", RegexOptions.CultureInvariant)]
    private static partial Regex SourceFrame();

    /// <summary>Maven's closing advice, the same in every failed build and useless to the agent.</summary>
    private static readonly string[] MavenNoise =
    [
        "[ERROR] -> [Help", "[ERROR] Re-run Maven", "[ERROR] To see the full stack trace", "[ERROR] For more information",
        "[ERROR] Failed to execute goal", "[ERROR] After correcting the problems", "[ERROR] mvn <args>",
    ];

    public static string Summarize(string output, string repoRoot, int maxLines)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var picked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string line, bool dedupe)
        {
            var text = Shorten(line, repoRoot);
            if (text.Length == 0) return;
            if (dedupe && !seen.Add(text)) return;
            picked.Add(text);
        }

        var capture = 0;
        var stackFrameAdded = false;
        var blankLinesInside = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();

            var diagnostic = MsBuildDiagnostic().Match(raw);
            if (diagnostic.Success)
            {
                capture = 0;
                if (diagnostic.Groups["kind"].Value == "error")
                {
                    Add($"{diagnostic.Groups["loc"].Value}: {diagnostic.Groups["code"].Value} {diagnostic.Groups["msg"].Value}", dedupe: true);
                }

                continue;
            }

            if (IsFailureHeader(trimmed))
            {
                Add(trimmed, dedupe: true);
                capture = LinesAfterFailure;
                stackFrameAdded = false;

                // Jest separates the name, the message and the frames with blank lines; other runners end a block with one.
                blankLinesInside = trimmed.StartsWith("● ", StringComparison.Ordinal);
                continue;
            }

            if (capture > 0)
            {
                if (trimmed.Length == 0)
                {
                    if (!blankLinesInside) capture = 0;
                    continue;
                }

                capture--;
                if (trimmed is "Error Message:" or "Stack Trace:") continue;

                if (trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    // Keep only the first frame that points at a source line outside dependencies: it is where the failure is.
                    if (!stackFrameAdded && SourceFrame().IsMatch(trimmed) && !trimmed.Contains("node_modules", StringComparison.Ordinal))
                    {
                        Add("  " + trimmed, dedupe: false);
                        stackFrameAdded = true;
                    }

                    continue;
                }

                Add("  " + trimmed, dedupe: false);
                continue;
            }

            if (GenericError().IsMatch(trimmed) || ColonError().IsMatch(trimmed) || IsMavenError(trimmed)) Add(trimmed, dedupe: true);
        }

        if (picked.Count == 0)
        {
            // Unknown format: the tail of the output is usually the most useful part.
            picked.AddRange(lines.Select(l => Shorten(l, repoRoot)).Where(l => l.Length > 0).TakeLast(maxLines));
        }

        var result = picked.Take(maxLines).ToList();
        var omitted = picked.Count - result.Count;
        if (omitted > 0) result.Add($"... {omitted} more line(s) omitted");
        return string.Join('\n', result);
    }

    /// <summary>
    /// True when every error says a process holds a build output, usually a test run left in the background. That says
    /// nothing about the code. MSB3021 alone can be a denied path, so MSB3027, which MSBuild gives after its retries, is required.
    /// </summary>
    public static bool FailedOnlyOnLockedFiles(string output)
    {
        var locked = false;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (IsFailureHeader(raw.Trim())) return false;

            var code = ErrorCode().Match(raw);
            if (!code.Success) continue;
            if (code.Groups["code"].Value is not ("MSB3027" or "MSB3021")) return false;
            locked |= code.Groups["code"].Value == "MSB3027";
        }

        return locked;
    }

    /// <summary>
    /// The line that names a failed test: VSTest and Microsoft.Testing.Platform (<c>Failed</c>/<c>failed</c>), xUnit
    /// (<c>[FAIL]</c>), pytest (<c>FAILED</c>, <c>ERROR</c> for collection errors), Jest and Vitest (<c>●</c>, <c>×</c>,
    /// <c>FAIL</c>) and Gradle (<c>... FAILED</c> at the end).
    /// </summary>
    private static bool IsFailureHeader(string line) =>
        line.StartsWith("Failed ", StringComparison.Ordinal)
        || line.StartsWith("failed ", StringComparison.Ordinal)
        || line.Contains("[FAIL]", StringComparison.Ordinal)
        || line.StartsWith("FAILED ", StringComparison.Ordinal)
        || line.StartsWith("ERROR ", StringComparison.Ordinal)
        || line.StartsWith("FAIL ", StringComparison.Ordinal)
        || line.StartsWith("● ", StringComparison.Ordinal)
        || line.StartsWith("× ", StringComparison.Ordinal)
        || (line.EndsWith(" FAILED", StringComparison.Ordinal) && line.Contains(" > ", StringComparison.Ordinal));

    /// <summary>A Maven <c>[ERROR]</c> line with content: compiler errors and failed assertions, without the closing advice.</summary>
    private static bool IsMavenError(string line) =>
        line.StartsWith("[ERROR] ", StringComparison.Ordinal)
        && line.Length > "[ERROR] ".Length
        && !MavenNoise.Any(noise => line.StartsWith(noise, StringComparison.Ordinal));

    private static string Shorten(string line, string repoRoot)
    {
        var text = line.TrimEnd();
        if (repoRoot.Length > 0)
        {
            var root = repoRoot.TrimEnd('/', '\\');
            text = text
                .Replace(root + "\\", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(root + "/", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return text.Length > MaxLineLength ? text[..MaxLineLength] + "…" : text;
    }
}
