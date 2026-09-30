namespace Hypa.Continuity.Harnesses.Pi;

/// <summary>
/// Spec §2.2 capture path list. One conversation under the source encoded cwd.
/// Paths are relative to <c>.pi/agent</c>.
/// </summary>
public static class PiStorePaths
{
    public const string VersionFileName = "version.txt";
    public const string SessionsDirectoryName = "sessions";

    /// <summary>
    /// Concrete relative paths the Pi adapter copies.
    /// <c>{encoded_cwd}</c> and <c>{session_file}</c> are filled per Work.
    /// </summary>
    public static readonly IReadOnlyList<string> CaptureRelativePathList =
    [
        VersionFileName,
        "sessions/--{encoded_cwd}--/{session_file}",
    ];

    public static string SessionsDirectoryRelative(string encodedCwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedCwd);
        return SessionsDirectoryName + "/--" + encodedCwd + "--";
    }

    public static string SessionFileRelative(string encodedCwd, string sessionFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFileName);
        return SessionsDirectoryRelative(encodedCwd) + "/" + sessionFileName;
    }
}
