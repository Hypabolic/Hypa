using System.Globalization;

namespace Hypa.Connectivity.Domain;

/// <summary>Join role. Mux and client both dial out.</summary>
public enum JoinRole
{
    Mux = 0,
    Client = 1,
}

/// <summary>Control is bounded NDJSON. Binary is terminal bytes.</summary>
public enum StreamClass
{
    Control = 0,
    Binary = 1,
}

/// <summary>One protocol. Self-hosted is single-tenant. Hosted is the product story.</summary>
public enum RendezvousDeployment
{
    SelfHosted = 0,
    Hosted = 1,
}

/// <summary>
/// Short-lived join capability bound. Developer issuer and CLI mint inside this window.
/// </summary>
public static class JoinCapabilityLifetime
{
    public const int MaxSeconds = 300;

    public static TimeSpan Max { get; } = TimeSpan.FromSeconds(MaxSeconds);
}

/// <summary>Round-trip UTC timestamps for issued capability expiry.</summary>
public static class ConnectivityTimestamp
{
    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);

    public static bool Equal(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime() == right.ToUniversalTime();
}

/// <summary>
/// Short-lived join capability fields. Bound to operator, placement, device, role, expiry, and nonce.
/// </summary>
public sealed record JoinCapability
{
    public required OperatorIdentity OperatorId { get; init; }
    public required PlacementId PlacementId { get; init; }
    public required DeviceId DeviceId { get; init; }
    public required JoinRole Role { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required JoinNonce Nonce { get; init; }
    public JoinSecret Secret { get; init; } = JoinSecret.Empty;
}

/// <summary>
/// Versioned bootstrap fields for one outbound join leg.
/// </summary>
public sealed record JoinBootstrap
{
    public required ConnectivityProtocolVersion ProtocolVersion { get; init; }
    public required JoinRole Role { get; init; }
    public required PlacementId PlacementId { get; init; }
    public required JoinNonce Nonce { get; init; }
    public required StreamClass StreamClass { get; init; }
    public required JoinCapability Capability { get; init; }
    public required string Audience { get; init; }
    public required string TenantScope { get; init; }
    public string EphPublicKey { get; init; } = "";
    public string EphPublicMac { get; init; } = "";
    public string DeviceSignature { get; init; } = "";
}

/// <summary>Named result of a matched mux and client join.</summary>
public sealed record JoinBinding
{
    public required PlacementId PlacementId { get; init; }
    public required StreamClass StreamClass { get; init; }
    public required JoinRole Role { get; init; }
    public string PeerEphPublicKey { get; init; } = "";
    public string PeerEphPublicMac { get; init; } = "";
}

/// <summary>Matched pair after two outbound legs bind.</summary>
public sealed record JoinMatch
{
    public required JoinBinding First { get; init; }
    public required JoinBinding Second { get; init; }
}

/// <summary>
/// Application ciphertext. The relay may forward bytes. It must not read them.
/// </summary>
public sealed record OpaqueFrame
{
    public required StreamClass StreamClass { get; init; }
    public required ReadOnlyMemory<byte> Ciphertext { get; init; }
}

public static class JoinRoleRules
{
    public static bool IsDefined(JoinRole value) =>
        value is JoinRole.Mux or JoinRole.Client;

    public static bool AreComplementary(JoinRole left, JoinRole right) =>
        (left == JoinRole.Mux && right == JoinRole.Client)
        || (left == JoinRole.Client && right == JoinRole.Mux);

    public static bool TryParse(string? value, out JoinRole role)
    {
        role = default;
        if (string.Equals(value, "mux", StringComparison.OrdinalIgnoreCase))
        {
            role = JoinRole.Mux;
            return true;
        }

        if (string.Equals(value, "client", StringComparison.OrdinalIgnoreCase))
        {
            role = JoinRole.Client;
            return true;
        }

        return false;
    }
}

public static class StreamClassRules
{
    public static bool IsDefined(StreamClass value) =>
        value is StreamClass.Control or StreamClass.Binary;

    public static bool TryParse(string? value, out StreamClass streamClass)
    {
        streamClass = default;
        if (string.Equals(value, "control", StringComparison.OrdinalIgnoreCase))
        {
            streamClass = StreamClass.Control;
            return true;
        }

        if (string.Equals(value, "binary", StringComparison.OrdinalIgnoreCase))
        {
            streamClass = StreamClass.Binary;
            return true;
        }

        return false;
    }
}

public static class RendezvousDeploymentRules
{
    public static bool IsDefined(RendezvousDeployment value) =>
        value is RendezvousDeployment.SelfHosted or RendezvousDeployment.Hosted;
}

public static class JoinBootstrapRules
{
    public static ConnectivityOutcome<JoinBootstrap> Validate(JoinBootstrap? bootstrap)
    {
        if (bootstrap is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "bootstrap is required");
        }

        if (!JoinRoleRules.IsDefined(bootstrap.Role))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "role must be mux or client");
        }

        if (!StreamClassRules.IsDefined(bootstrap.StreamClass))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "stream class must be control or binary");
        }

        if (string.IsNullOrEmpty(bootstrap.PlacementId.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "placement id must start with plc_");
        }

        if (string.IsNullOrWhiteSpace(bootstrap.Audience))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "audience is required");
        }

        if (string.IsNullOrWhiteSpace(bootstrap.TenantScope))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "tenant scope is required");
        }

        if (string.IsNullOrEmpty(bootstrap.Nonce.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join nonce is required");
        }

        var capability = bootstrap.Capability;
        if (capability is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability is required");
        }

        if (string.IsNullOrEmpty(capability.PlacementId.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability placement id must start with plc_");
        }

        if (string.IsNullOrEmpty(capability.OperatorId.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability operator id is required");
        }

        if (string.IsNullOrEmpty(capability.DeviceId.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is required");
        }

        if (!JoinRoleRules.IsDefined(capability.Role))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability role must be mux or client");
        }

        if (string.IsNullOrEmpty(capability.Nonce.Value))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability nonce is required");
        }

        if (!bootstrap.Capability.Secret.IsEmpty)
        {
            if (!JoinEphPublicKeys.TryDecode(bootstrap.EphPublicKey, out _))
            {
                return ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    "ephemeral public key is required");
            }

            if (!JoinEphPublicMacs.TryDecode(bootstrap.EphPublicMac, out _))
            {
                return ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    "ephemeral public mac is required");
            }
        }

        return ConnectivityOutcome<JoinBootstrap>.Success(bootstrap);
    }
}
