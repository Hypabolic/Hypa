using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Continuity.Application;
using Hypa.ControlPlane;

namespace Hypa.Placement;

/// <summary>
/// Dest occupant start over the mux control-plane Unix socket (NDJSON).
/// Passes cube_home / cwd / env so dest resume uses the Continuity placement.
/// </summary>
public sealed class ProtocolDestOccupantStarter : IDestOccupantStarter
{
    public async ValueTask<DestOccupantStartResult> StartResumeAsync(
        DestOccupantStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SocketPath))
            return DestOccupantStartResult.Fail("dest socket path is empty");
        if (string.IsNullOrWhiteSpace(request.PaneId))
            return DestOccupantStartResult.Fail("dest pane_id is required for agent.start");
        if (string.IsNullOrWhiteSpace(request.CubeHome))
            return DestOccupantStartResult.Fail("dest cube_home is required for agent.start");
        if (string.IsNullOrWhiteSpace(request.Workspace))
            return DestOccupantStartResult.Fail("dest workspace cwd is required for agent.start");

        try
        {
            await using var client = new ControlPlaneClient(request.SocketPath);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var start = new AgentStartParams
            {
                PaneId = request.PaneId,
                Occupant = request.HarnessId,
                Args = request.ResumeArgs,
                CubeHome = request.CubeHome,
                Cwd = request.Workspace,
                Env = request.Env,
                WorkId = request.WorkId,
                Generation = request.Generation,
            };
            var payload = JsonSerializer.Serialize(start, ProtocolJsonContext.Default.AgentStartParams);
            var parameters = JsonNode.Parse(payload)!.AsObject();

            var result = await client.CallAsync(
                    ProtocolMethods.AgentStart,
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);

            var paneId = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("pane_id", out var p)
                ? p.GetString()
                : request.PaneId;
            var occupant = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("occupant", out var o)
                ? o.GetString()
                : request.HarnessId;
            var alive = true;
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("alive", out var a))
            {
                alive = a.ValueKind == JsonValueKind.True;
            }

            IDestOccupantLiveness liveness = new ProtocolDestOccupantLiveness(
                request.SocketPath,
                paneId ?? request.PaneId!,
                alive);
            return DestOccupantStartResult.Pass(paneId, occupant, alive, liveness);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DestOccupantStartResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Polls <c>agent.status</c> for dest occupant liveness after <c>agent.start</c>.
/// </summary>
public sealed class ProtocolDestOccupantLiveness : IDestOccupantLiveness
{
    private readonly string _socketPath;
    private readonly string _paneId;
    private readonly bool _startedAlive;

    public ProtocolDestOccupantLiveness(string socketPath, string paneId, bool startedAlive)
    {
        _socketPath = socketPath;
        _paneId = paneId;
        _startedAlive = startedAlive;
    }

    public async ValueTask<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        if (!_startedAlive)
            return false;
        if (string.IsNullOrWhiteSpace(_socketPath) || string.IsNullOrWhiteSpace(_paneId))
            return false;

        try
        {
            await using var client = new ControlPlaneClient(_socketPath);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallAsync(
                    ProtocolMethods.AgentStatus,
                    new JsonObject { ["pane_id"] = _paneId },
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("alive", out var alive)
                && alive.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Polls <c>pane.process_info</c> for a user-typed occupant. File presence
/// and pane-shell liveness are not a live occupant.
/// </summary>
public sealed class ProtocolForegroundOccupantLiveness : IDestOccupantLiveness
{
    private readonly string _socketPath;
    private readonly string _paneId;
    private readonly string _occupantId;
    private readonly bool _startedAlive;

    public ProtocolForegroundOccupantLiveness(
        string socketPath,
        string paneId,
        string occupantId,
        bool startedAlive)
    {
        _socketPath = socketPath;
        _paneId = paneId;
        _occupantId = occupantId;
        _startedAlive = startedAlive;
    }

    public async ValueTask<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        if (!_startedAlive)
            return false;
        if (string.IsNullOrWhiteSpace(_socketPath)
            || string.IsNullOrWhiteSpace(_paneId)
            || string.IsNullOrWhiteSpace(_occupantId))
        {
            return false;
        }

        try
        {
            await using var client = new ControlPlaneClient(_socketPath);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallAsync(
                    ProtocolMethods.PaneProcessInfo,
                    new JsonObject { ["pane_id"] = _paneId },
                    cancellationToken)
                .ConfigureAwait(false);
            var command = ProtocolLivePaneOccupantProbe.ReadForegroundCommand(result);
            var mapped = ProtocolLivePaneOccupantProbe.OccupantIdFromForegroundCommand(command);
            return string.Equals(mapped, _occupantId, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
