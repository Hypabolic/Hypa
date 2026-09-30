using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure;

/// <summary>
/// File-system adapter for <see cref="IAgentPathCanonicalizer"/>.
/// Walks each path component and resolves that component's symlink.
/// A parent directory link is resolved before the next component is joined.
/// </summary>
public sealed class OsAgentPathCanonicalizer : IAgentPathCanonicalizer
{
    public static OsAgentPathCanonicalizer Instance { get; } = new();

    public string? TryCanonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full))
                return null;
            return Walk(full, depth: 0);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Walk(string absolute, int depth)
    {
        if (depth > 32)
            return null;

        var root = Path.GetPathRoot(absolute);
        if (string.IsNullOrEmpty(root))
            return null;

        var current = root;
        var relative = absolute.Length > root.Length ? absolute[root.Length..] : "";
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var link = ReadImmediateLink(current);
            if (link is null)
                continue;

            var target = Path.IsPathRooted(link)
                ? link
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, link));
            var canonical = Walk(target, depth + 1);
            if (canonical is null)
                return null;
            if (i + 1 >= parts.Length)
                return canonical;

            var rest = Path.Combine(parts[(i + 1)..]);
            return Walk(Path.Combine(canonical, rest), depth + 1);
        }

        return current;
    }

    private static string? ReadImmediateLink(string path)
    {
        try
        {
            var info = File.ResolveLinkTarget(path, returnFinalTarget: false);
            if (info is null)
                return null;
            return string.IsNullOrEmpty(info.LinkTarget) ? info.FullName : info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }
}
