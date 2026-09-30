namespace Hypa.Runtime.Application;

internal static class PathJail
{
    // Case-insensitive on Windows (where the file system is case-insensitive by default),
    // case-sensitive on Linux/macOS to avoid containment over-match on case-sensitive file systems.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static bool IsWithinRoot(string resolvedPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return resolvedPath.Equals(normalizedRoot, PathComparison)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }
}
