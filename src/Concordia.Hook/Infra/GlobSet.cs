using System.Text;
using System.Text.RegularExpressions;

namespace Concordia.Hook.Infra;

/// <summary>
/// Minimal glob matcher for repository-relative paths.
/// <c>**/</c> matches any number of directories, <c>*</c> any characters except <c>/</c>, <c>?</c> one character.
/// A pattern without <c>/</c> is matched against the file name only (like .gitignore). Matching ignores case.
/// </summary>
internal sealed class GlobSet
{
    private readonly string[] _globs;
    private readonly Regex[] _patterns;

    public GlobSet(IEnumerable<string> globs)
    {
        _globs = globs.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => Normalize(g.Trim())).ToArray();
        _patterns = _globs.Select(ToRegex).ToArray();
    }

    public IReadOnlyList<string> Globs => _globs;

    public bool IsEmpty => _globs.Length == 0;

    public bool Matches(string relativePath) => Match(relativePath) is not null;

    /// <summary>Returns the first glob that matches the path, or null.</summary>
    public string? Match(string relativePath)
    {
        var path = Normalize(relativePath);
        var name = path[(path.LastIndexOf('/') + 1)..];

        for (var i = 0; i < _patterns.Length; i++)
        {
            var target = _globs[i].Contains('/') ? path : name;
            if (_patterns[i].IsMatch(target)) return _globs[i];
        }

        return null;
    }

    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    internal static Regex ToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                var isDoubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (!isDoubleStar)
                {
                    builder.Append("[^/]*");
                }
                else if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    builder.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    builder.Append(".*");
                    i += 1;
                }
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
