namespace Hypa.Runtime.Application;

internal static class PathJail
{
    internal static bool IsWithinRoot(string resolvedPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return resolvedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
