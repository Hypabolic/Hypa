namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Mux path stays <c>HYPA_MUX_LOG</c> / <c>mux.log</c>. Attach is per-pid.
/// Do not write a remote attach log into the remote mux file.
/// Remote attach uses <c>~/.hypa/logs</c>. Do not fall back to a shared temp dir.
/// </summary>
public static class ProcessLogPaths
{
    public const string MuxLogVariable = "HYPA_MUX_LOG";
    public const string DefaultMuxFileName = "mux.log";
    public const string DisabledSuffix = ".disabled";
    public const long DefaultMaxBytes = 5L * 1024 * 1024;
    public const int DefaultRetainFiles = 3;

    public static string AttachFileName(int pid) =>
        string.Concat("attach.", pid.ToString(), ".log");

    public static string? TryUserLogDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            return null;
        return Path.Combine(home, ".hypa", "logs");
    }

    public static string DefaultMuxPath(string socketPath)
    {
        var dir = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        return Path.Combine(dir, DefaultMuxFileName);
    }

    public static string ResolveMuxPath(string socketPath, string? explicitPath)
    {
        var path = !string.IsNullOrWhiteSpace(explicitPath)
            ? explicitPath.Trim()
            : DefaultMuxPath(socketPath);
        return NormalizeLogPath(path);
    }

    /// <summary>
    /// Resolve a relative log path against the process working directory so
    /// <c>HYPA_MUX_LOG=mux.log</c> has a parent for owner-private validation.
    /// </summary>
    public static string NormalizeLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public static string ResolveAttachPath(string socketPath, int pid)
    {
        var dir = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        return Path.Combine(dir, AttachFileName(pid));
    }

    public static string DisabledMarkerPath(string logPath) =>
        logPath + DisabledSuffix;
}
