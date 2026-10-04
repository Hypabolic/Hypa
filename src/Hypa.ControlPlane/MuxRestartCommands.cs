namespace Hypa.ControlPlane;

/// <summary>
/// Shell commands that restart one specific mux. A bare <c>hypa mux stop</c>
/// resolves the configured session or HYPA_RUNTIME_SOCKET, so it can stop a
/// different mux. These name the session, and the socket when it is not the
/// session's default path.
/// </summary>
public static class MuxRestartCommands
{
    public static string Stop(string session, string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath)
            || string.Equals(
                Path.GetFullPath(socketPath),
                UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false),
                StringComparison.Ordinal))
        {
            return $"hypa mux stop --session {session}";
        }

        return $"hypa mux stop --socket {QuotePath(Path.GetFullPath(socketPath))}";
    }

    public static string Attach(string session) => $"hypa attach --session {session}";

    /// <summary>Run `stop`, then `attach`, then <paramref name="tail"/>.</summary>
    public static string Hint(string session, string? socketPath, string tail) =>
        $"Run `{Stop(session, socketPath)}`, then `{Attach(session)}`, {tail}";

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
