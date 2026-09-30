namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Lexical cwd admit for sidebar git. Infrastructure adds nofollow before spawn.
/// </summary>
public static class SidebarGitCwdGuard
{
    public static bool TryCanonicalize(string? cwd, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        var trimmed = cwd.Trim();
        if (trimmed.IndexOf('\0') >= 0)
            return false;
        if (!Path.IsPathRooted(trimmed))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(full) || !Path.IsPathRooted(full))
            return false;

        canonical = full;
        return true;
    }

    public static bool IsUnderAllowedRoot(string canonical, IReadOnlyList<string> allowedRoots)
    {
        return TryFindAllowedRoot(canonical, allowedRoots, out _);
    }

    public static bool TryFindAllowedRoot(
        string canonical,
        IReadOnlyList<string> allowedRoots,
        out string allowedRoot)
    {
        allowedRoot = "";
        if (string.IsNullOrWhiteSpace(canonical) || allowedRoots is null || allowedRoots.Count == 0)
            return false;

        var comparison = PathComparison();
        string? best = null;
        foreach (var raw in allowedRoots)
        {
            if (!TryCanonicalize(raw, out var root))
                continue;
            if (!IsUnder(canonical, root, comparison))
                continue;
            if (best is null || root.Length > best.Length)
                best = root;
        }

        if (best is null)
            return false;

        allowedRoot = best;
        return true;
    }

    public static bool IsUnder(string canonical, string root)
    {
        return IsUnder(canonical, root, PathComparison());
    }

    private static bool IsUnder(string canonical, string root, StringComparison comparison)
    {
        if (string.Equals(canonical, root, comparison))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return canonical.StartsWith(prefix, comparison);
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
