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

    /// <summary>Hash of HEAD plus the current content of every changed file.</summary>
    public static string Fingerprint(Repo repo, string head, IReadOnlyList<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(head + "\n"));
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n"));
            var full = Path.Combine(repo.Root, file);
            hash.AppendData(File.Exists(full) ? File.ReadAllBytes(full) : "<deleted>"u8.ToArray());
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
