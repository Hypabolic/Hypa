using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.Mux;

/// <summary>
/// Outbound join fields for a Cubes Connect retarget. Unix stays the
/// same-host path. This is not a Work move.
/// </summary>
public sealed record CubesConnectJoinMaterial
{
    public required RendezvousUrl Url { get; init; }
    public required JoinBootstrap Bootstrap { get; init; }
}

public interface ICubesConnectJoinMaterialSource
{
    ValueTask<CubesConnectJoinMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Test map of Placement id to join material. Production attach uses
/// <see cref="EnvironmentCubesConnectJoinMaterialSource"/>.
/// </summary>
public sealed class MapCubesConnectJoinMaterialSource : ICubesConnectJoinMaterialSource
{
    private readonly Func<PlacementRecord, CancellationToken, ValueTask<CubesConnectJoinMaterial?>> _resolve;

    public MapCubesConnectJoinMaterialSource(
        Func<PlacementRecord, CancellationToken, ValueTask<CubesConnectJoinMaterial?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    public MapCubesConnectJoinMaterialSource(CubesConnectJoinMaterial material)
        : this((_, _) => ValueTask.FromResult<CubesConnectJoinMaterial?>(material))
    {
        ArgumentNullException.ThrowIfNull(material);
    }

    public ValueTask<CubesConnectJoinMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return _resolve(placement, cancellationToken);
    }
}

/// <summary>
/// Production join material. The operator supplies a reachable rendezvous
/// URL, join secret, nonce, and device id. Pairing replaces this issuer.
/// </summary>
public sealed class EnvironmentCubesConnectJoinMaterialSource : ICubesConnectJoinMaterialSource
{
    public const string UrlVariable = "HYPA_RENDEZVOUS_URL";
    public const string SecretVariable = "HYPA_JOIN_SECRET";
    public const string NonceVariable = "HYPA_JOIN_NONCE";
    public const string DeviceVariable = "HYPA_DEVICE_ID";

    private readonly Func<string, string?> _getenv;
    private readonly DeveloperJoinCapabilityIssuer _issuer;

    [ActivatorUtilitiesConstructor]
    public EnvironmentCubesConnectJoinMaterialSource()
        : this(getenv: null, issuer: null)
    {
    }

    public EnvironmentCubesConnectJoinMaterialSource(
        Func<string, string?>? getenv = null,
        DeveloperJoinCapabilityIssuer? issuer = null)
    {
        _getenv = getenv ?? Environment.GetEnvironmentVariable;
        _issuer = issuer ?? new DeveloperJoinCapabilityIssuer();
    }

    public ValueTask<CubesConnectJoinMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();

        var urlRaw = _getenv(UrlVariable);
        var secretRaw = _getenv(SecretVariable);
        var nonceRaw = _getenv(NonceVariable);
        var deviceRaw = _getenv(DeviceVariable);

        if (!RendezvousUrl.TryParse(urlRaw, out var url))
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);
        if (!JoinSecret.TryParse(secretRaw, out var secret) || secret.IsEmpty)
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);
        if (!JoinNonce.TryParse(nonceRaw, out var nonce))
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);
        if (!DeviceId.TryParse(deviceRaw, out var deviceId))
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);
        if (!Hypa.Connectivity.Domain.PlacementId.TryParse(placement.Id.Value, out var joinPlacement))
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);

        var now = DateTimeOffset.UtcNow;
        var issued = _issuer.Issue(
            joinPlacement,
            deviceId,
            JoinRole.Client,
            nonce,
            now.AddMinutes(1),
            now,
            secret);
        if (!issued.Ok || issued.Value is null)
            return ValueTask.FromResult<CubesConnectJoinMaterial?>(null);

        return ValueTask.FromResult<CubesConnectJoinMaterial?>(new CubesConnectJoinMaterial
        {
            Url = url,
            Bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = joinPlacement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = issued.Value,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            },
        });
    }
}

internal static class CubesConnectOutboundJoin
{
    public static async Task<IFramedSession?> ConnectAsync(
        CubesConnectJoinMaterial material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        var connected = await OutboundFramedSession.ConnectAsync(
                material.Url,
                material.Bootstrap,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Ok || connected.Value is null)
            return null;
        return connected.Value;
    }
}
