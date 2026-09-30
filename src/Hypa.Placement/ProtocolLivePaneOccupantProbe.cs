using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypa.ControlPlane;

namespace Hypa.Placement;

/// <summary>
/// Live pane occupant snapshot over the mux control-plane Unix socket.
/// Continuity does not speak NDJSON.
/// </summary>
public sealed class ProtocolLivePaneOccupantProbe : ILivePaneOccupantProbe
{
    public async ValueTask<LivePaneProbeResult> ProbeAsync(
        string socketPath,
        string paneId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            return LivePaneProbeResult.Fail(ContinuityReasons.Internal, "mux socket path is empty");
        if (string.IsNullOrWhiteSpace(paneId))
            return LivePaneProbeResult.Fail(ContinuityReasons.Internal, "pane id is required");

        var socket = StripUnixPrefix(socketPath);
        try
        {
            await using var client = new ControlPlaneClient(socket);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var paneIdValue = paneId.Trim();
            var agent = await client.CallAsync(
                    ProtocolMethods.AgentGet,
                    new JsonObject { ["pane_id"] = paneIdValue },
                    cancellationToken)
                .ConfigureAwait(false);
            var pane = await client.CallAsync(
                    ProtocolMethods.PaneGet,
                    new JsonObject { ["pane_id"] = paneIdValue },
                    cancellationToken)
                .ConfigureAwait(false);

            var process = await client.CallAsync(
                    ProtocolMethods.PaneProcessInfo,
                    new JsonObject { ["pane_id"] = paneIdValue },
                    cancellationToken)
                .ConfigureAwait(false);

            var startedId = ReadManagedStartOccupantId(agent) ?? ReadManagedStartOccupantId(pane) ?? "";
            var kind = ReadString(agent, "agent") ?? ReadString(pane, "agent") ?? "";
            var paneAlive = ReadBool(agent, "alive") || ReadBool(pane, "alive");
            var cwd = ReadString(pane, "cwd") ?? "";
            var home = HomeFromAgentSession(
                startedId.Length > 0 ? startedId : kind,
                ReadAgentSessionValue(agent) ?? ReadAgentSessionValue(pane));
            if (string.IsNullOrWhiteSpace(home))
                home = ReadString(pane, "home") ?? "";
            var tokens = ReadStringMap(agent, "tokens");
            if (tokens.Count == 0)
                tokens = ReadStringMap(pane, "tokens");
            var foregroundCommand = ReadForegroundCommand(process);
            var identity = ResolveOccupantIdentity(
                startedId,
                foregroundCommand,
                paneAlive,
                managedStart: startedId.Length > 0);
            IDestOccupantLiveness liveness = identity.UsePaneLiveness
                ? new ProtocolDestOccupantLiveness(socket, paneId.Trim(), identity.Alive)
                : new ProtocolForegroundOccupantLiveness(
                    socket,
                    paneId.Trim(),
                    identity.OccupantId,
                    identity.Alive);
            return LivePaneProbeResult.Found(new LivePaneOccupant
            {
                PaneId = paneId.Trim(),
                OccupantId = identity.OccupantId,
                Home = home,
                Cwd = cwd,
                Alive = identity.Alive,
                Liveness = liveness,
                ResumeStartArgs = LiveResumeStartArgs.FromLiveHome(home, tokens),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LivePaneProbeResult.Fail(ContinuityReasons.Internal, ex.Message);
        }
    }

    public static string HomeFromAgentSession(string occupantId, string? sessionValue)
    {
        if (string.IsNullOrWhiteSpace(sessionValue))
            return "";
        var full = Path.GetFullPath(sessionValue.Trim());
        var suffix = occupantId switch
        {
            CertifiedHarnessIds.Pi => Path.Combine(".pi", "agent"),
            CertifiedHarnessIds.Fake => Path.Combine(".fake", "agent"),
            _ => "",
        };
        if (suffix.Length == 0)
            return "";
        var normalizedSuffix = Path.DirectorySeparatorChar + suffix;
        if (full.EndsWith(normalizedSuffix, StringComparison.Ordinal)
            || full.EndsWith(Path.AltDirectorySeparatorChar + suffix, StringComparison.Ordinal))
        {
            return full[..^suffix.Length].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return "";
    }

    /// <summary>
    /// Map the foreground-group command basename to an occupant id.
    /// The command that owns the group is the identity. Shell names are idle.
    /// </summary>
    public static string OccupantIdFromForegroundCommand(string? command)
    {
        var name = CommandBaseName(command);
        if (name.Length == 0 || IsShellCommand(name))
            return "";
        if (string.Equals(name, CertifiedHarnessIds.Pi, StringComparison.Ordinal))
            return CertifiedHarnessIds.Pi;
        if (string.Equals(name, CertifiedHarnessIds.Fake, StringComparison.Ordinal))
            return CertifiedHarnessIds.Fake;
        return name;
    }

    public static bool IsShellCommand(string? command)
    {
        var name = CommandBaseName(command);
        return name.Equals("sh", StringComparison.Ordinal)
            || name.Equals("bash", StringComparison.Ordinal)
            || name.Equals("zsh", StringComparison.Ordinal)
            || name.Equals("fish", StringComparison.Ordinal)
            || name.Equals("dash", StringComparison.Ordinal)
            || name.Equals("ksh", StringComparison.Ordinal)
            || name.Equals("csh", StringComparison.Ordinal)
            || name.Equals("tcsh", StringComparison.Ordinal)
            || name.Equals("pwsh", StringComparison.Ordinal)
            || name.Equals("powershell", StringComparison.Ordinal)
            || name.Equals("login", StringComparison.Ordinal)
            || name.Equals("stub", StringComparison.Ordinal);
    }

    /// <summary>
    /// Live foreground command is the occupant identity. A stale
    /// <c>agent.start</c> id cannot certify sleep, Claude, or Codex.
    /// An idle shell falls back only to a managed <c>agent.start</c> occupant.
    /// Detector <c>AgentKind</c> is not that provenance.
    /// </summary>
    public static OccupantIdentity ResolveOccupantIdentity(
        string startedId,
        string? foregroundCommand,
        bool paneAlive,
        bool managedStart = false)
    {
        var fromCommand = OccupantIdFromForegroundCommand(foregroundCommand);
        if (fromCommand.Length > 0)
        {
            return new OccupantIdentity(
                fromCommand,
                Alive: true,
                UsePaneLiveness: false);
        }

        if (managedStart && CertifiedHarnessIds.Contains(startedId))
        {
            return new OccupantIdentity(
                startedId,
                paneAlive,
                UsePaneLiveness: true);
        }

        return new OccupantIdentity(
            OccupantId: "",
            Alive: false,
            UsePaneLiveness: false);
    }

    /// <summary>
    /// Occupant id from a managed <c>agent.start</c> session. Detector
    /// <c>AgentKind</c> is not a start record.
    /// </summary>
    public static string? ReadManagedStartOccupantId(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("agent_session", out var session)
            || session.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!session.TryGetProperty("session_start_source", out var source)
            || source.ValueKind != JsonValueKind.String
            || !string.Equals(source.GetString(), "startup", StringComparison.Ordinal))
        {
            return null;
        }

        if (!session.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = agent.GetString();
        return CertifiedHarnessIds.Contains(id) ? id : null;
    }

    public readonly record struct OccupantIdentity(
        string OccupantId,
        bool Alive,
        bool UsePaneLiveness);

    public static string ReadForegroundCommand(JsonElement processInfo)
    {
        if (processInfo.ValueKind != JsonValueKind.Object
            || !processInfo.TryGetProperty("foreground_processes", out var processes)
            || processes.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var item in processes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!item.TryGetProperty("command", out var command)
                || command.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = command.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string CommandBaseName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOf('/');
        var name = slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
        var backslash = name.LastIndexOf('\\');
        if (backslash >= 0 && backslash < name.Length - 1)
            name = name[(backslash + 1)..];
        return name;
    }

    private static string StripUnixPrefix(string socketPath)
    {
        const string unix = "unix:";
        var trimmed = socketPath.Trim();
        return trimmed.StartsWith(unix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[unix.Length..]
            : trimmed;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadAgentSessionValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty("agent_session", out var session)
            || session.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return session.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;
            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            result[property.Name] = value;
        }

        return result;
    }
}
