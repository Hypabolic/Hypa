using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

internal sealed record AttachEndpointConnectPreflightResult
{
    public required AttachEndpointWelcome TargetWelcome { get; init; }
    public required ulong TargetConnectionGeneration { get; init; }
    public AttachEndpointWelcome? SourceWelcome { get; init; }
    public ulong SourceConnectionGeneration { get; init; }
    public bool SourceAvailable { get; init; }
}

internal static class AttachEndpointConnectPreflight
{
    public static async ValueTask<(AttachEndpointConnectPreflightResult? Result, EndpointActivationPreflightError? Error)>
        RunAsync(
            ControlPlaneClient targetClient,
            ControlPlaneClient? sourceClient,
            string clientId,
            AttachGeometry geometry,
            bool sourceRequested,
            CancellationToken ct,
            ulong existingSourceGeneration = 0,
            string? existingSourceBootId = null)
    {
        var targetRpc = new AttachEndpointRpcClient(targetClient);
        AttachEndpointWelcome targetWelcome;
        ulong targetGeneration;
        try
        {
            (targetWelcome, targetGeneration) = await targetRpc.HelloAsync(
                    AttachEndpointPreflight.BuildHello(clientId, geometry),
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            return (null, new EndpointActivationPreflightError(ex.Message) { Code = AttachEndpointErrorCodes.Incompatible });
        }
        catch (IOException)
        {
            return (null, new EndpointActivationPreflightError("target endpoint hello failed"));
        }

        AttachEndpointWelcome? sourceWelcome = null;
        ulong sourceGeneration = 0;
        var sourceAvailable = false;
        if (sourceRequested && sourceClient is not null)
        {
            // Do not hello again: ApplyHello increments generation and
            // then the source snapshot looks stale.
            if (existingSourceGeneration > 0
                && !string.IsNullOrWhiteSpace(existingSourceBootId))
            {
                sourceGeneration = existingSourceGeneration;
                sourceAvailable = true;
                sourceWelcome = new AttachEndpointWelcome
                {
                    BootId = existingSourceBootId,
                    MuxIdentity = "source",
                    EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
                    ProtocolMajor = (uint)ProtocolVersion.Major,
                    ProtocolMinor = (uint)ProtocolVersion.Minor,
                    SnapshotCodec = AttachEndpointProtocol.SnapshotCodec,
                    SurfaceCodec = AttachEndpointProtocol.SurfaceCodec,
                    InputCodec = AttachEndpointProtocol.InputCodec,
                    Methods = AttachEndpointProtocol.Methods.ToArray(),
                    Capabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
                    ConnectionGeneration = existingSourceGeneration,
                };
            }
            else
            {
                var sourceRpc = new AttachEndpointRpcClient(sourceClient);
                try
                {
                    (sourceWelcome, sourceGeneration) = await sourceRpc.HelloAsync(
                            AttachEndpointPreflight.BuildHello(clientId, geometry),
                            ct)
                        .ConfigureAwait(false);
                    sourceAvailable = true;
                }
                catch (ControlPlaneException ex)
                {
                    return (null, new EndpointActivationPreflightError(ex.Message) { Code = AttachEndpointErrorCodes.Incompatible });
                }
                catch (IOException)
                {
                    return (null, new EndpointActivationPreflightError("source endpoint hello failed"));
                }
            }
        }

        var beginRequest = BuildBeginRequest(
            clientId,
            geometry,
            targetWelcome,
            targetGeneration,
            sourceWelcome,
            sourceGeneration,
            sourceAvailable);
        var preflightError = AttachEndpointPreflight.ValidateBegin(beginRequest, targetWelcome, sourceWelcome);
        if (preflightError is not null)
            return (null, preflightError);

        return (new AttachEndpointConnectPreflightResult
        {
            TargetWelcome = targetWelcome,
            TargetConnectionGeneration = targetGeneration,
            SourceWelcome = sourceWelcome,
            SourceConnectionGeneration = sourceGeneration,
            SourceAvailable = sourceAvailable,
        }, null);
    }

    /// <summary>
    /// source lease when the source surface is live. Connect hello only
    /// has welcome identity, so stamp a provisional lease from that
    /// welcome. <see cref="AttachSession"/> replaces it after snapshot
    /// identity is ready.
    /// </summary>
    internal static EndpointActivationBeginRequest BuildBeginRequest(
        string clientId,
        AttachGeometry geometry,
        AttachEndpointWelcome targetWelcome,
        ulong targetGeneration,
        AttachEndpointWelcome? sourceWelcome,
        ulong sourceGeneration,
        bool sourceAvailable)
    {
        var targetLease = ProvisionalLease("pending", targetGeneration, targetWelcome, clientId);
        EndpointActivationLease? sourceLease = null;
        if (sourceAvailable && sourceWelcome is not null)
            sourceLease = ProvisionalLease("source", sourceGeneration, sourceWelcome, clientId);

        return new EndpointActivationBeginRequest
        {
            ClientId = clientId,
            Geometry = geometry,
            Target = new EndpointActivationIntent { EndpointId = "pending" },
            Source = sourceLease,
            SourceAvailable = sourceAvailable,
            HostFocused = true,
            Epoch = 1,
            TargetLease = targetLease,
        };
    }

    private static EndpointActivationLease ProvisionalLease(
        string endpointId,
        ulong generation,
        AttachEndpointWelcome welcome,
        string clientId) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = welcome.BootId,
            MinimumProjectionRevision = 0,
            ClientId = clientId,
            LeaseId = string.Empty,
        };
}
