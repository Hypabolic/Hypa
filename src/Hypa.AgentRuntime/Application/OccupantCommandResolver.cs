namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Resolves occupant argv0 before hypa-pty-host <c>execve</c>.
/// <c>execve</c> does not search PATH.
/// </summary>
public static class OccupantCommandResolver
{
    /// <summary>
    /// Fallback PATH when the occupant env and the parent env omit PATH.
    /// Matches <c>ChildEnvironmentPolicy.DefaultPath</c>.
    /// </summary>
    public const string DefaultPath = "/usr/bin:/bin";

    public const string HypaPtyHostProvider = "hypa-pty-host";

    /// <summary>
    /// hypa-pty-host uses <c>execve</c>. Stubs that never execve may keep
    /// the unresolved command.
    /// </summary>
    public static bool RequiresExecvePathSearch(string? ptyProvider) =>
        string.Equals(ptyProvider, HypaPtyHostProvider, StringComparison.Ordinal);

    public static string UnresolvableMessage(string command) =>
        "command not found: " + command;

    /// <summary>
    /// Child PATH order: occupant <c>[env] PATH</c>, then parent PATH, then
    /// <see cref="DefaultPath"/>.
    /// </summary>
    public static string ResolveSearchPath(
        IReadOnlyDictionary<string, string>? occupantEnv,
        string? parentPath)
    {
        if (occupantEnv is not null && occupantEnv.TryGetValue("PATH", out var occupantPath))
            return occupantPath ?? "";

        if (!string.IsNullOrEmpty(parentPath))
            return parentPath;

        return DefaultPath;
    }

    public static bool TryResolve(
        string command,
        string cwd,
        IReadOnlyDictionary<string, string>? occupantEnv,
        string? parentPath,
        out string resolved,
        Func<string, bool>? fileExists = null)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var exists = fileExists ?? File.Exists;
        var workDir = string.IsNullOrWhiteSpace(cwd) ? "." : cwd;
        if (IsPathCommand(command))
            return TryResolvePath(command, workDir, exists, out resolved);

        var searchPath = ResolveSearchPath(occupantEnv, parentPath);
        foreach (var segment in searchPath.Split(':'))
        {
            var directory = segment.Length == 0 ? workDir : segment;
            if (TryCombineExisting(directory, command, exists, out resolved))
                return true;
        }

        return false;
    }

    private static bool IsPathCommand(string command) =>
        command.Contains('/', StringComparison.Ordinal);

    private static bool TryResolvePath(
        string command,
        string cwd,
        Func<string, bool> fileExists,
        out string resolved)
    {
        var candidate = Path.IsPathRooted(command)
            ? command
            : Path.Combine(cwd, command);
        return TryExistingFile(candidate, fileExists, out resolved);
    }

    private static bool TryCombineExisting(
        string directory,
        string command,
        Func<string, bool> fileExists,
        out string resolved)
    {
        resolved = "";
        string candidate;
        try
        {
            candidate = Path.Combine(directory, command);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return TryExistingFile(candidate, fileExists, out resolved);
    }

    private static bool TryExistingFile(
        string candidate,
        Func<string, bool> fileExists,
        out string resolved)
    {
        resolved = "";
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!fileExists(full))
            return false;

        resolved = full;
        return true;
    }
}
