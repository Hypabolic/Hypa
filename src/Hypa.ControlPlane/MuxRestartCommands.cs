namespace Hypa.ControlPlane;

/// <summary>
/// Shell commands that act on one specific mux. A bare <c>hypa mux restart</c>
/// resolves the configured session or HYPA_RUNTIME_SOCKET, so it can restart a
/// different mux. These name the session, or the socket when it is not the
/// session's default path.
/// </summary>
public static class MuxRestartCommands
{
    /// <summary>Sidebar row that restarts the mux from inside attach.</summary>
    public const string SidebarAction = "↻ restart to update";

    public static string Restart(string session, string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath)
            || string.Equals(
                Path.GetFullPath(socketPath),
                UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false),
                StringComparison.Ordinal))
        {
            return $"hypa mux restart --session {session}";
        }

        return $"hypa mux restart --socket {QuotePath(Path.GetFullPath(socketPath))}";
    }

    public static string Attach(string session) => $"hypa attach --session {session}";

    /// <summary>Run `restart`, then <paramref name="tail"/>.</summary>
    public static string Hint(string session, string? socketPath, string tail) =>
        $"Run `{Restart(session, socketPath)}` {tail}";

    private static string QuotePath(string path)
    {
        foreach (var c in path)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '_' or '-'))
                return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        return path;
    }
}
