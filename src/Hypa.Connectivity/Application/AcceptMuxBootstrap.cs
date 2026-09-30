using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Builds a mux-role bootstrap from an admitted client join.
/// The accept helper stamps a live ephemeral key after this factory returns.
/// </summary>
public static class AcceptMuxBootstrap
{
    public const string LocalMuxDeviceId = "dev_muxhost";

    public static ConnectivityOutcome<JoinBootstrap> FromClient(JoinBootstrap client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.Role != JoinRole.Client)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "accept helper expects client role");
        }

        if (!DeviceId.TryParse(LocalMuxDeviceId, out var muxDevice))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "mux device id is invalid");
        }

        var now = DateTimeOffset.UtcNow;
        var expires = client.Capability.ExpiresAt;
        if (expires <= now)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        var max = now.Add(JoinCapabilityLifetime.Max);
        if (expires > max)
            expires = max;

        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            client.PlacementId,
            muxDevice,
            JoinRole.Mux,
            client.Nonce,
            expires,
            now,
            client.Capability.Secret);
        if (!issued.Ok || issued.Value is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                issued.Reason ?? ConnectivityReasons.BootstrapInvalid,
                issued.Detail ?? "mux capability is invalid");
        }

        return ConnectivityOutcome<JoinBootstrap>.Success(new JoinBootstrap
        {
            ProtocolVersion = client.ProtocolVersion,
            Role = JoinRole.Mux,
            PlacementId = client.PlacementId,
            Nonce = client.Nonce,
            StreamClass = client.StreamClass,
            Capability = issued.Value,
            Audience = client.Audience,
            TenantScope = client.TenantScope,
        });
    }
}
