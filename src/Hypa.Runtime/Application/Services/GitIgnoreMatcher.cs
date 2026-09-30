using System.Text;
using System.Text.RegularExpressions;

namespace Hypa.Runtime.Application.Services;

internal sealed class GitIgnoreMatcher
{
    private readonly List<Rule> _rules;

    private GitIgnoreMatcher(List<Rule> rules)
    {
        _rules = rules;
    }

    public static GitIgnoreMatcher Load(string root)
    {
        var rules = new List<Rule>();
        foreach (var ignoreFile in Directory.EnumerateFiles(root, ".gitignore", SearchOption.AllDirectories)
                     .Where(p => !IsUnderGitDirectory(root, p))
                     .Order(StringComparer.Ordinal))
        {
            var ignoreDir = Path.GetDirectoryName(ignoreFile) ?? root;
            var basePath = NormalizeRelativePath(root, ignoreDir);
            var ordinal = rules.Count;
            foreach (var line in File.ReadLines(ignoreFile))
            {
                var rule = ParseLine(line, basePath, ordinal++);
                if (rule is not null)
                    rules.Add(rule);
            }
        }

        return new GitIgnoreMatcher(rules);
    }

    public static Func<string, bool> LoadForGlob(string pattern)
    {
        var rule = ParseLine(pattern, basePath: "", ordinal: 0);
        return rule is null ? _ => false : path => rule.IsMatch(Normalize(path).Trim('/'));
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var normalized = Normalize(relativePath).Trim('/');
        if (normalized.Length == 0)
            return false;

        bool? ignored = null;
        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDirectory && !IsInMatchedDirectory(normalized, rule))
                continue;

            if (rule.IsMatch(normalized))
                ignored = !rule.Negated;
        }

        return ignored == true;
    }

    private static Rule? ParseLine(string line, string basePath, int ordinal)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var negated = trimmed.StartsWith('!');
        if (negated)
            trimmed = trimmed[1..];

        if (trimmed.Length == 0)
            return null;

        var dirOnly = trimmed.EndsWith('/');
        if (dirOnly)
            trimmed = trimmed[..^1];

        // Git anchoring rule: a pattern containing a slash anywhere (including a leading one,
        // checked before it is stripped) is anchored to the .gitignore's own directory;
        // otherwise it matches at any depth below that directory.
        var anchored = trimmed.Contains('/');
        trimmed = trimmed.TrimStart('/');
        if (trimmed.Length == 0)
            return null;

        var prefix = basePath.Length == 0 ? "" : $"{Regex.Escape(basePath)}/";
        var body = TranslateGlob(trimmed);
        var pattern = $"^{prefix}{(anchored ? "" : "(?:.*/)?")}{body}($|/.*)";
        return new Rule(pattern, dirOnly, negated, ordinal);
    }

    private static bool IsInMatchedDirectory(string path, Rule rule)
    {
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var parent = string.Join('/', parts.Take(i));
            if (rule.IsMatch(parent))
                return true;
        }

        return false;
    }

    private static string TranslateGlob(string pattern)
    {
        var segments = pattern.Split('/');
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (segments[i] == "**")
            {
                // "a/**" matches everything inside a; "**/b" and "a/**/b" match across zero or more directories.
                sb.Append(last ? ".*" : "(?:.*/)?");
                continue;
            }

            TranslateSegment(segments[i], sb);
            if (!last)
                sb.Append('/');
        }

        return sb.ToString();
    }

    private static void TranslateSegment(string segment, StringBuilder sb)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '\\' when i + 1 < segment.Length:
                    sb.Append(Regex.Escape(segment[i + 1].ToString()));
                    i++;
                    break;
                case '[':
                    var close = segment.IndexOf(']', i + 1);
                    if (close > i + 1)
                    {
                        var body = segment[(i + 1)..close];
                        if (body.StartsWith('!'))
                            body = $"^{body[1..]}";
                        sb.Append('[').Append(body).Append(']');
                        i = close;
                    }
                    else
                    {
                        sb.Append(Regex.Escape(c.ToString()));
                    }

                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "" : Normalize(relative).Trim('/');
    }

    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsUnderGitDirectory(string root, string path) =>
        NormalizeRelativePath(root, path).Split('/').Contains(".git", StringComparer.Ordinal);

    private sealed record Rule(string Pattern, bool DirOnly, bool Negated, int Ordinal)
    {
        private readonly Regex _regex = new(Pattern, RegexOptions.CultureInvariant);

        public bool IsMatch(string path) => _regex.IsMatch(path);
    }
}
