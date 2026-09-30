namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Deny list for checkpoint workspace files. No I/O. Fail closed.
/// Do not fold into <see cref="DefaultCheckpointExcludes"/> — that path is silent.
/// </summary>
public static class CheckpointSecretFilePolicy
{
    public static IReadOnlyList<string> Labels { get; } =
    [
        ".env",
        ".env.*",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "*.pem",
        "*.key",
        ".ssh/",
        ".aws/",
        ".gnupg/",
        ".netrc",
        ".git-credentials",
    ];

    public static IReadOnlyList<string> ManifestExcludes { get; } =
        [.. DefaultCheckpointExcludes.Patterns, .. Labels];

    public static bool IsDenied(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var posix = relativePath.Replace('\\', '/');
        var parts = posix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (part.Equals(".ssh", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".aws", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".gnupg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var baseName = parts[^1];
        if (baseName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            baseName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("id_dsa", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals(".git-credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(baseName);
        return ext.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only when <paramref name="projectRoot"/> is the filesystem root or exactly
    /// <paramref name="homeDirectory"/>. A workspace under home is not skipped.
    /// </summary>
    public static bool IsExactUnsafeRoot(string projectRoot, string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var trimmed = TrimDirSep(full);
        var fsRoot = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(fsRoot) &&
            string.Equals(trimmed, TrimDirSep(fsRoot), comparison))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
            return false;

        try
        {
            var homeFull = TrimDirSep(Path.GetFullPath(homeDirectory));
            return string.Equals(trimmed, homeFull, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    public static string WarningFor(string relativePath)
    {
        var posix = relativePath.Replace('\\', '/').Trim('/');
        return "workspace: omit secret file " + posix;
    }

    public static string WarningForReplaced(string relativePath)
    {
        var posix = relativePath.Replace('\\', '/').Trim('/');
        return "workspace: omit replaced file " + posix;
    }

    private static string TrimDirSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
