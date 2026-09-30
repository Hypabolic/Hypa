using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Developer-mode capability mint. Device pairing replaces this issuer.
/// </summary>
public sealed class DeveloperJoinCapabilityIssuer
{
    public ConnectivityOutcome<JoinCapability> Issue(
        PlacementId placementId,
        DeviceId deviceId,
        JoinRole role,
        JoinNonce nonce,
        DateTimeOffset expiresAt,
        DateTimeOffset utcNow,
        JoinSecret secret = default)
    {
        if (string.IsNullOrEmpty(placementId.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "placement id must start with plc_");
        }

        if (string.IsNullOrEmpty(deviceId.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is required");
        }

        if (!JoinRoleRules.IsDefined(role))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "role must be mux or client");
        }

        if (string.IsNullOrEmpty(nonce.Value))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join nonce is required");
        }

        if (expiresAt <= utcNow)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        if (expiresAt - utcNow > JoinCapabilityLifetime.Max)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability lifetime exceeds maximum");
        }

        return ConnectivityOutcome<JoinCapability>.Success(new JoinCapability
        {
            OperatorId = OperatorIdentity.LocalSelfHosted,
            PlacementId = placementId,
            DeviceId = deviceId,
            Role = role,
            ExpiresAt = expiresAt,
            Nonce = nonce,
            Secret = secret.IsEmpty ? JoinSecret.Empty : secret,
        });
    }
}
