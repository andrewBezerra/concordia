namespace Concordia.Hook.Review;

internal static class DiffParser
{
    /// <summary>Collects the added lines of a unified diff, grouped by the new file path.</summary>
    public static Dictionary<string, List<string>> AddedLines(string unifiedDiff)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var raw in unifiedDiff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = raw[4..].Trim();
                if (path == "/dev/null")
                {
                    current = null;
                    continue;
                }

                if (path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
                if (!result.TryGetValue(path, out current))
                {
                    current = [];
                    result[path] = current;
                }

                continue;
            }

            if (current is not null && raw.StartsWith('+')) current.Add(raw[1..]);
        }

        return result;
    }

    /// <summary>Sums added + deleted lines from <c>git diff --numstat</c>; binary files count as zero.</summary>
    public static (List<string> Files, int ChangedLines) NumStat(string numstat)
    {
        var files = new List<string>();
        var changed = 0;
        foreach (var line in numstat.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (int.TryParse(parts[0], out var added)) changed += added;
            if (int.TryParse(parts[1], out var deleted)) changed += deleted;
            files.Add(parts[^1]);
        }

        return (files, changed);
    }
}
