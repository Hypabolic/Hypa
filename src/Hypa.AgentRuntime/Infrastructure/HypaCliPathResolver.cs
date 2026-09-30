namespace Hypa.AgentRuntime.Infrastructure;

/// <summary>
/// Resolves the <c>hypa</c> CLI for pane children and plugin children.
/// A mux process named <c>hypa</c> is that CLI.
/// Any other mux binary uses the <c>hypa</c> file beside it.
/// </summary>
public static class HypaCliPathResolver
{
    public const string CliFileName = "hypa";

    public static string? Resolve(string? processPath = null)
    {
        processPath = string.IsNullOrWhiteSpace(processPath)
            ? Environment.ProcessPath
            : processPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(processPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (IsCliName(full))
            return full;

        foreach (var dir in CandidateDirectories(full))
        {
            var sibling = Path.Combine(dir, CliFileNameOnDisk());
            if (File.Exists(sibling))
                return Path.GetFullPath(sibling);
        }

        return null;
    }

    private static bool IsCliName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals(CliFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string CliFileNameOnDisk() =>
        OperatingSystem.IsWindows() ? CliFileName + ".exe" : CliFileName;

    private static IEnumerable<string> CandidateDirectories(string processPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (TryRemember(seen, Path.GetDirectoryName(processPath), out var processDir))
            yield return processDir;

        string? linkDir = null;
        try
        {
            var link = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
            if (link is not null)
                linkDir = Path.GetDirectoryName(link.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (TryRemember(seen, linkDir, out var resolvedDir))
            yield return resolvedDir;
    }

    private static bool TryRemember(HashSet<string> seen, string? dir, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrEmpty(dir))
            return false;

        try
        {
            full = Path.GetFullPath(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return seen.Add(full);
    }
}
