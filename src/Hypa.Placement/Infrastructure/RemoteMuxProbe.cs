using Hypa.AgentRuntime.Protocol;
using Hypa.Placement.Application;

namespace Hypa.Placement.Infrastructure;

internal static class RemoteMuxProbe
{
    internal static string BuildShell(string session) =>
        "uname -s; uname -m; echo ---; "
        + "sock=\"$HOME/.config/hypa/runtime/"
        + session
        + "/hypa.sock\"; "
        + "if [ -S \"$sock\" ]; then echo running; hypa --socket \"$sock\" ping 2>/dev/null || echo '{\"ok\":false}'; else echo absent; fi";

    internal static RemoteMuxProbeResult Parse(string stdout)
    {
        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        var os = lines.Length > 0 ? lines[0] : "";
        var arch = lines.Length > 1 ? lines[1] : "";
        if (RemotePlatform.LooksLikeWindows(os))
        {
            return RemoteMuxProbeResult.Fail(RemoteMuxOutcome.Failure(
                RemoteMuxReasons.PlatformUnsupported,
                "Windows as a remote host is refused."));
        }

        if (!RemotePlatform.TryParse(os, arch, out var platform))
        {
            return RemoteMuxProbeResult.Fail(RemoteMuxOutcome.Failure(
                RemoteMuxReasons.PlatformUnsupported,
                $"Remote platform '{os.Trim()}' on '{arch.Trim()}' is not supported."));
        }

        _ = platform;
        var running = lines.Any(line => line == "running");
        var restart = RemoteServerRestartReason.None;
        if (running)
        {
            var pingLine = lines
                .SkipWhile(line => line != "running")
                .Skip(1)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (!TryReadPingProtocol(pingLine, out var protocol)
                || protocol != ProtocolVersion.Current)
            {
                restart = RemoteServerRestartReason.EndpointProtocol;
            }
        }

        return new RemoteMuxProbeResult
        {
            Ok = true,
            ServerRunning = running,
            RestartReason = restart,
        };
    }

    private static bool TryReadPingProtocol(string? line, out int protocol)
    {
        protocol = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var trimmed = line.Trim();
        var protoIndex = trimmed.IndexOf("\"protocol\"", StringComparison.Ordinal);
        if (protoIndex < 0)
            return false;
        var colon = trimmed.IndexOf(':', protoIndex);
        if (colon < 0)
            return false;
        var start = colon + 1;
        while (start < trimmed.Length && char.IsWhiteSpace(trimmed[start]))
            start++;
        var end = start;
        while (end < trimmed.Length && char.IsDigit(trimmed[end]))
            end++;
        return end > start && int.TryParse(trimmed[start..end], out protocol);
    }
}
