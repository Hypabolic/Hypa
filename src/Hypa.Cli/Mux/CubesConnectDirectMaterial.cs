using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Cli.Mux;

/// <summary>
/// Direct dial fields for a QUIC peer Connect. No rendezvous URL is required.
/// </summary>
public sealed record CubesConnectDirectMaterial
{
    public required BytePathEndpoint Endpoint { get; init; }
    public required JoinBootstrap Bootstrap { get; init; }
    public string? CertificateSha256 { get; init; }
    public IDeviceKeyStore? DeviceKeys { get; init; }
    public IQuicTransportCapabilityProbe? QuicProbe { get; init; }
    internal CubesConnectStageClock? StageClock { get; init; }
}

public interface ICubesConnectDirectMaterialSource
{
    ValueTask<CubesConnectDirectMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production direct join material for QUIC peer rows.
/// </summary>
public sealed class EnvironmentCubesConnectDirectMaterialSource : ICubesConnectDirectMaterialSource
{
    public const string SecretVariable = "HYPA_JOIN_SECRET";
    public const string NonceVariable = "HYPA_JOIN_NONCE";
    public const string DeviceVariable = "HYPA_DEVICE_ID";

    private readonly Func<string, string?> _getenv;
    private readonly DeveloperJoinCapabilityIssuer _issuer;

    [ActivatorUtilitiesConstructor]
    public EnvironmentCubesConnectDirectMaterialSource()
        : this(getenv: null, issuer: null)
    {
    }

    public EnvironmentCubesConnectDirectMaterialSource(
        Func<string, string?>? getenv = null,
        DeveloperJoinCapabilityIssuer? issuer = null)
    {
        _getenv = getenv ?? Environment.GetEnvironmentVariable;
        _issuer = issuer ?? new DeveloperJoinCapabilityIssuer();
    }

    public ValueTask<CubesConnectDirectMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();

        if (placement.Quic is not { Enabled: true } quic)
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);

        if (!QuicReachTarget.TryValidate(quic.Target, out var normalized, out _))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);
        if (!QuicReachTarget.TryParseHostPort(normalized, out var host, out var port, out _))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);

        var endpoint = new BytePathEndpoint
        {
            Host = host,
            Port = port,
            Tls = true,
            TlsServerName = host,
            QuicListening = null,
        };
        if (!BytePathEndpointRules.TryValidate(endpoint, out _))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);

        var secretRaw = _getenv(SecretVariable);
        var nonceRaw = _getenv(NonceVariable);
        var deviceRaw = _getenv(DeviceVariable);

        if (!JoinSecret.TryParse(secretRaw, out var secret) || secret.IsEmpty)
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);
        if (!JoinNonce.TryParse(nonceRaw, out var nonce))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);
        if (!DeviceId.TryParse(deviceRaw, out var deviceId))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);
        if (!Hypa.Connectivity.Domain.PlacementId.TryParse(placement.Id.Value, out var joinPlacement))
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);

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
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(null);

        return ValueTask.FromResult<CubesConnectDirectMaterial?>(new CubesConnectDirectMaterial
        {
            Endpoint = endpoint,
            CertificateSha256 = quic.CertificateSha256,
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

/// <summary>
/// Product direct join material. Pairing mints the capability. Lab secrets stay out.
/// </summary>
public sealed class PairingCubesConnectDirectMaterialSource : ICubesConnectDirectMaterialSource
{
    private readonly DevicePairingService? _pairing;
    private readonly TimeProvider _clock;

    public PairingCubesConnectDirectMaterialSource(
        DevicePairingService? pairing = null,
        TimeProvider? clock = null)
    {
        _pairing = pairing;
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<CubesConnectDirectMaterial?> GetAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();

        if (placement.Quic is not { Enabled: true } quic)
            return null;

        if (!TlsCertificatePin.TryParse(quic.CertificateSha256, out _))
            return null;
        if (!QuicReachTarget.TryValidate(quic.Target, out var normalized, out _))
            return null;
        if (!QuicReachTarget.TryParseHostPort(normalized, out var host, out var port, out _))
            return null;

        var endpoint = new BytePathEndpoint
        {
            Host = host,
            Port = port,
            Tls = true,
            TlsServerName = host,
            QuicListening = null,
        };
        if (!BytePathEndpointRules.TryValidate(endpoint, out _))
            return null;
        if (!Hypa.Connectivity.Domain.PlacementId.TryParse(placement.Id.Value, out var joinPlacement))
            return null;

        var pairing = _pairing ?? TryCreateDefaultPairing();
        if (pairing is null)
            return null;

        DeviceId? enrolled = null;
        if (DeviceId.TryParse(quic.EnrolledDeviceId, out var parsedDevice))
            enrolled = parsedDevice;

        var minted = await pairing.MintClientJoinCapabilityAsync(
                joinPlacement,
                JoinRole.Client,
                _clock.GetUtcNow().AddMinutes(1),
                enrolled,
                cancellationToken)
            .ConfigureAwait(false);
        if (!minted.Ok || minted.Value is null)
            return null;

        return new CubesConnectDirectMaterial
        {
            Endpoint = endpoint,
            CertificateSha256 = quic.CertificateSha256,
            DeviceKeys = pairing.KeyStore,
            Bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = joinPlacement,
                Nonce = minted.Value.Nonce,
                StreamClass = StreamClass.Control,
                Capability = minted.Value,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            },
        };
    }

    private static DevicePairingService? TryCreateDefaultPairing()
    {
        try
        {
            var directory = DevicePairingStatePaths.ResolveFromEnvironment();
            Directory.CreateDirectory(directory);
            var keys = PlatformDeviceKeyStore.CreateOrFallback(
                DevicePairingStatePaths.FallbackKeyDirectory(directory));
            return new DevicePairingService(new FileDevicePairingStore(directory), keys);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class CubesConnectDirectOutboundJoin
{
    public static Task<IFramedSession?> ConnectAsync(
        CubesConnectDirectMaterial material,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(material, bytePath: null, cancellationToken);

    public static async Task<IFramedSession?> ConnectAsync(
        CubesConnectDirectMaterial material,
        IBytePath? bytePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        var stages = material.StageClock;
        IBytePath path;
        TlsCertificatePin? pin = null;
        if (bytePath is not null)
        {
            path = stages is null
                ? bytePath
                : new DialStampBytePath(
                    new TransportNotingBytePath(bytePath, bytePath.Provider, stages),
                    stages);
        }
        else
        {
            if (!TlsCertificatePin.TryParse(material.CertificateSha256, out var parsedPin))
                return null;
            pin = parsedPin;
            var probe = material.QuicProbe
                ?? new CachingQuicTransportCapabilityProbe(new QuicTransportCapabilityProbe());
            if (stages is null)
            {
                path = new QuicTcpFallbackBytePath(probe: probe, certificatePin: parsedPin);
            }
            else
            {
                var quic = new TransportNotingBytePath(
                    new QuicBytePath(tlsTrust: null, probe, parsedPin),
                    BytePathProviders.Quic,
                    stages);
                var tcp = new TransportNotingBytePath(
                    new TcpTlsBytePath(tlsTrust: null, parsedPin),
                    BytePathProviders.Tcp,
                    stages);
                path = new DialStampBytePath(
                    new QuicTcpFallbackBytePath(
                        probe: probe,
                        quicPath: quic,
                        tcpPath: tcp,
                        certificatePin: parsedPin),
                    stages);
            }
        }

        var connected = await OutboundFramedSession.ConnectDirectAsync(
                material.Endpoint,
                material.Bootstrap,
                budget: null,
                tlsTrust: null,
                bytePath: path,
                deviceKeys: material.DeviceKeys,
                certificatePin: pin,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (stages is { DialSucceeded: true })
            stages.Stamp(CubesConnectStages.Join);
        if (!connected.Ok || connected.Value is null)
            return null;
        return connected.Value;
    }
}

/// <summary>
/// Records the byte path that opened. A failed QUIC attempt does not set
/// transport. The successful QUIC or TCP open does.
/// </summary>
internal sealed class TransportNotingBytePath : IBytePath
{
    private readonly IBytePath _inner;
    private readonly string _transport;
    private readonly CubesConnectStageClock _stages;

    public TransportNotingBytePath(IBytePath inner, string transport, CubesConnectStageClock stages)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentNullException.ThrowIfNull(stages);
        _inner = inner;
        _transport = transport;
        _stages = stages;
    }

    public string Provider => _inner.Provider;

    public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
        BytePathRequest request,
        CancellationToken cancellationToken = default)
    {
        var opened = await _inner.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        if (opened.Ok)
        {
            _stages.NoteTransport(_transport);
            _stages.NoteDialSucceeded();
        }

        return opened;
    }

    public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default) =>
        _inner.CloseAsync(handle, cancellationToken);
}

/// <summary>Stamps dial once when the fallback open returns.</summary>
internal sealed class DialStampBytePath : IBytePath
{
    private readonly IBytePath _inner;
    private readonly CubesConnectStageClock _stages;

    public DialStampBytePath(IBytePath inner, CubesConnectStageClock stages)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(stages);
        _inner = inner;
        _stages = stages;
    }

    public string Provider => _inner.Provider;

    public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
        BytePathRequest request,
        CancellationToken cancellationToken = default)
    {
        var opened = await _inner.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        _stages.Stamp(CubesConnectStages.Dial);
        return opened;
    }

    public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default) =>
        _inner.CloseAsync(handle, cancellationToken);
}
