using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Domain;
using Microsoft.Extensions.DependencyInjection;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

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

    /// <summary>
    /// The join attempts to make, in order. The caller moves to the next one
    /// only when the host denies the join. A source with one device returns one.
    /// An empty list means no material. An attempt makes its material when the
    /// caller starts it, so a capability does not age while an earlier attempt runs.
    /// </summary>
    async ValueTask<IReadOnlyList<CubesConnectDirectAttempt>> GetCandidatesAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        var material = await GetAsync(placement, cancellationToken).ConfigureAwait(false);
        return material is null
            ? []
            : [new CubesConnectDirectAttempt(_ => new ValueTask<CubesConnectDirectMaterial>(material))];
    }
}

/// <summary>
/// One join attempt. <see cref="MintAsync"/> makes the material just before the dial.
/// It throws <see cref="InvalidDataException"/> with a user message when it cannot.
/// </summary>
public sealed record CubesConnectDirectAttempt(
    Func<CancellationToken, ValueTask<CubesConnectDirectMaterial>> MintAsync);

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
        var attempts = await GetCandidatesAsync(placement, cancellationToken).ConfigureAwait(false);
        return attempts.Count == 0
            ? null
            : await attempts[0].MintAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A placement that has an enrolled device value uses only that device, with
    /// no fallback. A value that is not a device id fails closed. With no
    /// enrolled device the attempts are the paired devices that hold a key:
    /// trusted ones first, then pending ones (see
    /// <see cref="DevicePairingService.ListJoinCandidatesAsync"/>).
    /// </summary>
    public async ValueTask<IReadOnlyList<CubesConnectDirectAttempt>> GetCandidatesAsync(
        PlacementRecord placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();

        if (placement.Quic is not { Enabled: true } quic)
            return [];

        if (!TlsCertificatePin.TryParse(quic.CertificateSha256, out _))
            return [];
        if (!QuicReachTarget.TryValidate(quic.Target, out var normalized, out _))
            return [];
        if (!QuicReachTarget.TryParseHostPort(normalized, out var host, out var port, out _))
            return [];

        var endpoint = new BytePathEndpoint
        {
            Host = host,
            Port = port,
            Tls = true,
            TlsServerName = host,
            QuicListening = null,
        };
        if (!BytePathEndpointRules.TryValidate(endpoint, out _))
            return [];
        if (!Hypa.Connectivity.Domain.PlacementId.TryParse(placement.Id.Value, out var joinPlacement))
            return [];

        var pairing = _pairing ?? TryCreateDefaultPairing();
        if (pairing is null)
            return [];

        var devices = new List<DeviceId>();
        if (!string.IsNullOrWhiteSpace(quic.EnrolledDeviceId))
        {
            // Any enrolled value is the choice of the user. It is never skipped.
            if (!DeviceId.TryParse(quic.EnrolledDeviceId.Trim(), out var enrolled))
            {
                throw new InvalidDataException(
                    "The enrolled device id of this Placement is not valid. "
                    + "Run add-quic --device DEVICE_ID with a paired device id.");
            }

            devices.Add(enrolled);
        }
        else
        {
            var listed = await pairing.ListJoinCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Ok || listed.Value is null)
                return [];
            devices.AddRange(listed.Value);
            if (devices.Count == 0)
            {
                throw new InvalidDataException(
                    "No paired device with a key was found. Pair this machine, or run add-quic --device DEVICE_ID.");
            }
        }

        return devices
            .Select(device => new CubesConnectDirectAttempt(async ct =>
            {
                var minted = await pairing.MintClientJoinCapabilityAsync(
                        joinPlacement,
                        JoinRole.Client,
                        _clock.GetUtcNow().AddMinutes(1),
                        device,
                        ct)
                    .ConfigureAwait(false);
                if (!minted.Ok || minted.Value is null)
                {
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(minted.Detail)
                            ? "The join capability could not be made. Run add-quic --device DEVICE_ID."
                            : minted.Detail);
                }

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
            }))
            .ToList();
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
    public static Task<Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>> ConnectAsync(
        CubesConnectDirectMaterial material,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(material, bytePath: null, cancellationToken);

    public static async Task<Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>> ConnectAsync(
        CubesConnectDirectMaterial material,
        IBytePath? bytePath,
        CancellationToken cancellationToken = default)
    {
        const string pinInvalid = "certificate pin is invalid";
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
            {
                return Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Fail(
                    new ConnectError(ConnectivityReasons.BootstrapInvalid, pinInvalid));
            }
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
        {
            var joinFailure = connected.Ok && connected.Value is not null
                ? null
                : connected.Reason ?? CubesConnectErrorReasons.DialFailed;
            stages.Stamp(CubesConnectStages.Join, joinFailure);
        }

        if (!connected.Ok || connected.Value is null)
        {
            return Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Fail(
                connected.Ok
                    ? new ConnectError(CubesConnectErrorReasons.DialFailed)
                    : ConnectErrors.FromOutcome(connected));
        }

        return Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>.Ok(connected.Value);
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
