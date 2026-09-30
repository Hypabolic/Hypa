namespace Hypa.Connectivity.Domain;

/// <summary>
/// One join audience. Self-hosted and hosted use the same service token.
/// Tenant and operator isolate deployments.
/// </summary>
public static class RendezvousAudiences
{
    public const string Rendezvous = "hypa.rendezvous";
}

/// <summary>Self-hosted v0 is one local tenant. Hosted uses an operator tenant id.</summary>
public static class RendezvousTenants
{
    public const string Local = "local";
}

/// <summary>
/// Relay deployment identity. A self-hosted relay rejects a foreign audience.
/// </summary>
public sealed record RendezvousRelayIdentity
{
    public required RendezvousDeployment Deployment { get; init; }
    public required string Audience { get; init; }
    public required string TenantScope { get; init; }
    public required ConnectivityProtocolVersion ProtocolVersion { get; init; }

    public static RendezvousRelayIdentity SelfHostedV0 { get; } = new()
    {
        Deployment = RendezvousDeployment.SelfHosted,
        Audience = RendezvousAudiences.Rendezvous,
        TenantScope = RendezvousTenants.Local,
        ProtocolVersion = ConnectivityProtocolVersion.V0,
    };
}

public static class JoinSlot
{
    public static string Key(JoinNonce nonce) => nonce.Value;
}
