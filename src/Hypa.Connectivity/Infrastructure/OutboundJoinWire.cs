using System.Net.Sockets;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Shared outbound join wire for relay and direct accept targets.</summary>
internal static class OutboundJoinWire
{
    public static async ValueTask<ConnectivityOutcome<HeldJoinSession>> JoinHeldAsync(
        BytePathEndpoint endpoint,
        JoinBootstrap bootstrap,
        IBytePath bytePath,
        CancellationToken cancellationToken,
        IDeviceKeyStore? deviceKeys = null)
    {
        if (!BytePathEndpointRules.TryValidate(endpoint, out var invalid))
        {
            return ConnectivityOutcome<HeldJoinSession>.Failure(
                ConnectivityReasons.PeerUnavailable,
                invalid);
        }

        BytePathHandle? path = null;
        try
        {
            using var keys = JoinEphemeralKeyPair.Create();
            var prepared = bootstrap.Capability.Secret.IsEmpty
                ? JoinBootstrapRules.Validate(bootstrap)
                : JoinEphAuthenticator.Stamp(bootstrap, keys);
            if (!prepared.Ok || prepared.Value is null)
            {
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    prepared.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                    prepared.Detail ?? "join secret is required");
            }

            var outbound = prepared.Value;
            if (deviceKeys is not null)
            {
                var signed = await JoinDeviceAuthenticator.StampAsync(outbound, deviceKeys, cancellationToken)
                    .ConfigureAwait(false);
                if (!signed.Ok || signed.Value is null)
                {
                    return ConnectivityOutcome<HeldJoinSession>.Failure(
                        signed.Reason ?? ConnectivityReasons.Unauthorized,
                        signed.Detail ?? "device signature is required");
                }

                outbound = signed.Value;
            }

            var admitted = JoinMatcher.Admit(outbound, InferRelay(outbound), DateTimeOffset.UtcNow);
            if (!admitted.Ok)
            {
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    admitted.Reason ?? ConnectivityReasons.JoinDenied,
                    admitted.Detail ?? "bootstrap denied");
            }

            var opened = await bytePath.OpenAsync(
                    new BytePathRequest
                    {
                        Provider = bytePath.Provider,
                        Endpoint = endpoint,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!opened.Ok || opened.Value is null)
            {
                var joinLoss = RelayFailurePolicy.ForJoinLoss(AttemptIds.FromJoinNonce(bootstrap.Nonce));
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    opened.Reason ?? joinLoss.Reason,
                    opened.Detail ?? "path open failed",
                    stage: joinLoss.Stage,
                    attemptId: joinLoss.AttemptId,
                    retryable: joinLoss.Retryable);
            }

            path = opened.Value;
            var stream = path.Stream;
            await BoundedJoinLine.WriteAsync(stream, JoinBootstrapCodec.Write(outbound), cancellationToken)
                .ConfigureAwait(false);
            var line = await BoundedJoinLine.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!line.Ok || line.Value is null)
            {
                var joinLoss = RelayFailurePolicy.ForJoinLoss(AttemptIds.FromJoinNonce(bootstrap.Nonce));
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    line.Reason ?? ConnectivityReasons.PeerUnavailable,
                    line.Detail ?? "peer closed",
                    stage: line.Stage ?? joinLoss.Stage,
                    attemptId: line.AttemptId ?? joinLoss.AttemptId,
                    retryable: line.Retryable ?? joinLoss.Retryable);
            }

            var parsed = JoinBootstrapCodec.ReadResult(line.Value);
            if (!parsed.Ok || parsed.Value is null)
            {
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    parsed.Reason ?? ConnectivityReasons.BootstrapInvalid,
                    parsed.Detail ?? "join result json is invalid",
                    stage: parsed.Stage ?? RelayFailureStageWire.Join,
                    attemptId: parsed.AttemptId ?? AttemptIds.FromJoinNonce(bootstrap.Nonce),
                    retryable: parsed.Retryable);
            }

            var result = parsed.Value;
            if (!result.Ok)
            {
                var reason = string.IsNullOrWhiteSpace(result.Reason)
                    ? ConnectivityReasons.JoinDenied
                    : result.Reason;
                var joinLoss = RelayFailurePolicy.ForJoinLoss(
                    result.AttemptId ?? AttemptIds.FromJoinNonce(bootstrap.Nonce),
                    reason);
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    reason,
                    result.Detail ?? "join denied",
                    stage: result.Stage ?? joinLoss.Stage,
                    attemptId: joinLoss.AttemptId,
                    retryable: result.Retryable ?? joinLoss.Retryable);
            }

            if (string.IsNullOrWhiteSpace(result.PlacementId) || result.StreamClass is null || result.Role is null)
            {
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join result is incomplete");
            }

            if (!PlacementId.TryParse(result.PlacementId, out var placementId))
            {
                return ConnectivityOutcome<HeldJoinSession>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join result placement id is invalid");
            }

            if (!outbound.Capability.Secret.IsEmpty)
            {
                var peer = JoinEphAuthenticator.VerifyPeer(
                    outbound.Capability.Secret,
                    result.PeerEphPublicKey,
                    result.PeerEphPublicMac);
                if (!peer.Ok)
                {
                    return ConnectivityOutcome<HeldJoinSession>.Failure(
                        peer.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                        peer.Detail ?? "peer ephemeral public key is required");
                }
            }

            var session = new HeldJoinSession(
                path.Lifetime,
                stream,
                new JoinBinding
                {
                    PlacementId = placementId,
                    StreamClass = result.StreamClass.Value,
                    Role = result.Role.Value,
                    PeerEphPublicKey = result.PeerEphPublicKey ?? "",
                    PeerEphPublicMac = result.PeerEphPublicMac ?? "",
                });
            path = null;
            return ConnectivityOutcome<HeldJoinSession>.Success(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            var joinLoss = RelayFailurePolicy.ForJoinLoss(AttemptIds.FromJoinNonce(bootstrap.Nonce));
            return ConnectivityOutcome<HeldJoinSession>.Failure(
                joinLoss.Reason,
                ex.Message,
                stage: joinLoss.Stage,
                attemptId: joinLoss.AttemptId,
                retryable: joinLoss.Retryable);
        }
        catch (IOException ex)
        {
            var joinLoss = RelayFailurePolicy.ForJoinLoss(AttemptIds.FromJoinNonce(bootstrap.Nonce));
            return ConnectivityOutcome<HeldJoinSession>.Failure(
                joinLoss.Reason,
                ex.Message,
                stage: joinLoss.Stage,
                attemptId: joinLoss.AttemptId,
                retryable: joinLoss.Retryable);
        }
        finally
        {
            if (path is not null)
                await bytePath.CloseAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static RendezvousRelayIdentity InferRelay(JoinBootstrap bootstrap) =>
        new()
        {
            Deployment = string.Equals(bootstrap.TenantScope, RendezvousTenants.Local, StringComparison.Ordinal)
                ? RendezvousDeployment.SelfHosted
                : RendezvousDeployment.Hosted,
            Audience = bootstrap.Audience,
            TenantScope = bootstrap.TenantScope,
            ProtocolVersion = bootstrap.ProtocolVersion,
        };
}
