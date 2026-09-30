using System.Net.Sockets;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.ControlPlane;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Read mux identity from a prepared remote session through runtime.health.
/// The value comes from the remote mux. Hypa does not invent it.
/// </summary>
public interface IRemoteMuxIdentityReader
{
    ValueTask<PlacementOutcome<MuxIdentity>> ReadAsync(
        string localSocketPath,
        string sessionName,
        CancellationToken cancellationToken = default);
}

public sealed class ControlPlaneRemoteMuxIdentityReader : IRemoteMuxIdentityReader
{
    public async ValueTask<PlacementOutcome<MuxIdentity>> ReadAsync(
        string localSocketPath,
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSocketPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        try
        {
            await using var client = new ControlPlaneClient(localSocketPath, validatePrivatePath: true);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.CallAsync(ProtocolMethods.RuntimeHealth, null, cancellationToken)
                .ConfigureAwait(false);
            var health = JsonSerializer.Deserialize(
                response.GetRawText(),
                ProtocolJsonContext.Default.RuntimeHealthResult);
            if (health?.RuntimeSessionId is not { Length: > 0 } runtimeSessionId)
            {
                return PlacementOutcome<MuxIdentity>.Failure(
                    PlacementReasons.PreparationFailed,
                    "Remote session did not report a mux identity.");
            }

            if (!RemoteMuxIdentityFormat.TryParse(runtimeSessionId, sessionName, out var mux))
            {
                return PlacementOutcome<MuxIdentity>.Failure(
                    PlacementReasons.PreparationFailed,
                    "Remote session reported an invalid mux identity.");
            }

            return PlacementOutcome<MuxIdentity>.Success(mux);
        }
        catch (ControlPlaneException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (ControlPlaneClientTimeoutException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (IOException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (SocketException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
        catch (JsonException ex)
        {
            return PlacementOutcome<MuxIdentity>.Failure(
                PlacementReasons.PreparationFailed,
                ex.Message);
        }
    }
}

internal static class RemoteMuxIdentityFormat
{
    internal static bool TryParse(string runtimeSessionId, string sessionName, out MuxIdentity identity)
    {
        identity = default;
        if (MuxIdentity.TryParse(runtimeSessionId, out identity))
            return true;

        if (MuxIdentity.TryFromSessionName(sessionName, out identity))
            return true;

        return MuxIdentity.TryFromSessionName(runtimeSessionId, out identity);
    }
}
