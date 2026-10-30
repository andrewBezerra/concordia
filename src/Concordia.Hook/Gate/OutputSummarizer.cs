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
                continue;
            }

            if (capture > 0)
            {
                if (trimmed.Length == 0)
                {
                    capture = 0;
                    continue;
                }

                capture--;
                if (trimmed is "Error Message:" or "Stack Trace:") continue;

                if (trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    // Keep only the first frame that points at a source line: it is where the failure is.
                    if (!stackFrameAdded && trimmed.Contains(":line ", StringComparison.Ordinal))
                    {
                        Add("  " + trimmed, dedupe: false);
                        stackFrameAdded = true;
                    }

                    continue;
                }

                Add("  " + trimmed, dedupe: false);
                continue;
            }

            if (GenericError().IsMatch(trimmed)) Add(trimmed, dedupe: true);
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

    private static bool IsFailureHeader(string line) =>
        line.StartsWith("Failed ", StringComparison.Ordinal)
        || line.StartsWith("failed ", StringComparison.Ordinal)
        || line.Contains("[FAIL]", StringComparison.Ordinal);

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
