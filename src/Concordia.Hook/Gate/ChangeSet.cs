using System.Security.Cryptography;
using System.Text;
using Concordia.Hook.Infra;

namespace Concordia.Hook.Gate;

internal static class ChangeSet
{
    /// <summary>Watched files that differ from HEAD: modified, staged, untracked, deleted or renamed.</summary>
    public static IReadOnlyList<string> WatchedChanges(Repo repo, GlobSet watch)
    {
        var status = repo.Git("status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (!status.Succeeded) return [];
        return ParsePorcelain(status.StdOut).Where(f => watch.IsEmpty || watch.Matches(f)).ToList();
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z</c>. Rename and copy entries carry the original path as the next field.</summary>
    internal static IReadOnlyList<string> ParsePorcelain(string output)
    {
        var entries = output.Split('\0');
        var files = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].TrimEnd('\r', '\n');
            if (entry.Length < 4) continue;

            files.Add(GlobSet.Normalize(entry[3..]));
            if (entry[0] is 'R' or 'C') i++;
        }

        return files.ToList();
    }

    /// <summary>Content hash per file, used to tell later edits from what was already dirty when the session started.</summary>
    public static Dictionary<string, string> Hashes(Repo repo, IReadOnlyList<string> files) =>
        files.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(Content(repo, f))), StringComparer.Ordinal);

    /// <summary>
    /// Whether the watched changes differ from the session's starting point. Without a recorded
    /// starting point every change counts: the gate would rather verify too much than too little.
    /// </summary>
    public static bool DiffersFrom(Repo repo, IReadOnlyList<string> changes, Dictionary<string, string>? baseline)
    {
        if (baseline is null) return changes.Count > 0;
        if (baseline.Keys.Any(f => !changes.Contains(f, StringComparer.Ordinal))) return true;

        var current = Hashes(repo, changes);
        return current.Any(c => !baseline.TryGetValue(c.Key, out var hash) || hash != c.Value);
    }

    /// <summary>Hash of HEAD plus the current content of every changed file, and of the config that judged them, when given.</summary>
    public static string Fingerprint(Repo repo, string head, IReadOnlyList<string> files, string? config = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(head + "\n"));
        if (config is not null) hash.AppendData(Encoding.UTF8.GetBytes(config + "\n"));
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n"));
            hash.AppendData(Content(repo, file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Content(Repo repo, string file)
    {
        var full = Path.Combine(repo.Root, file);
        try
        {
            return File.Exists(full) ? File.ReadAllBytes(full) : "<deleted>"u8.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may hold the file. Failing here would skip the whole gate for this stop.
            return "<unreadable>"u8.ToArray();
        }
    }
}
