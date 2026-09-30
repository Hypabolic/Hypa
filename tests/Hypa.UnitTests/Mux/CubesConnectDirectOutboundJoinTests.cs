using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectDirectOutboundJoinTests
{
    [Fact]
    public async Task Direct_connect_attempts_quic_before_tcp_fallback()
    {
        var failingQuic = new CountingBytePath(BytePathProviders.Quic, ConnectivityReasons.PeerUnavailable);
        var recordingTcp = new CountingBytePath(BytePathProviders.Tcp, ConnectivityReasons.PeerUnavailable);
        var bytePath = new QuicTcpFallbackBytePath(
            probe: new SupportedQuicProbe(),
            quicPath: failingQuic,
            tcpPath: recordingTcp);
        var material = CreateMaterial(endpoint: new BytePathEndpoint
        {
            Host = "192.168.1.10",
            Port = 443,
            Tls = true,
            TlsServerName = "192.168.1.10",
            QuicListening = true,
        });

        var session = await CubesConnectDirectOutboundJoin.ConnectAsync(
            material,
            bytePath,
            CancellationToken.None);

        Assert.Null(session);
        Assert.Equal(1, failingQuic.OpenAttempts);
        Assert.Equal(1, recordingTcp.OpenAttempts);
    }

    [Fact]
    public async Task Direct_connect_skips_quic_when_accept_reports_tcp_only()
    {
        var failingQuic = new CountingBytePath(BytePathProviders.Quic, ConnectivityReasons.PeerUnavailable);
        var recordingTcp = new CountingBytePath(BytePathProviders.Tcp, ConnectivityReasons.PeerUnavailable);
        var bytePath = new QuicTcpFallbackBytePath(
            probe: new SupportedQuicProbe(),
            quicPath: failingQuic,
            tcpPath: recordingTcp);
        var material = CreateMaterial(endpoint: new BytePathEndpoint
        {
            Host = "192.168.1.10",
            Port = 443,
            Tls = true,
            TlsServerName = "192.168.1.10",
            QuicListening = false,
        });

        var session = await CubesConnectDirectOutboundJoin.ConnectAsync(
            material,
            bytePath,
            CancellationToken.None);

        Assert.Null(session);
        Assert.Equal(0, failingQuic.OpenAttempts);
        Assert.Equal(1, recordingTcp.OpenAttempts);
    }

    private static CubesConnectDirectMaterial CreateMaterial(BytePathEndpoint endpoint)
    {
        Assert.True(JoinNonce.TryParse("nonce001", out var nonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var deviceId));
        Assert.True(PlacementId.TryParse("plc_quic01", out var joinPlacement));
        var secret = JoinSecret.Create();
        var now = DateTimeOffset.UtcNow;
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            joinPlacement,
            deviceId,
            JoinRole.Client,
            nonce,
            now.AddMinutes(1),
            now,
            secret);
        Assert.True(issued.Ok, issued.Detail);
        return new CubesConnectDirectMaterial
        {
            Endpoint = endpoint,
            Bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = joinPlacement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = issued.Value!,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            },
        };
    }

    private sealed class SupportedQuicProbe : IQuicTransportCapabilityProbe
    {
        public QuicTransportCapabilityReport Probe() =>
            new()
            {
                RuntimeIdentifier = "test",
                IsSupported = true,
                NativeLibraryFound = true,
                NativeLibraryLocation = "test",
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = false,
            };
    }

    private sealed class CountingBytePath(string provider, string reason) : IBytePath
    {
        public int OpenAttempts { get; private set; }

        public string Provider => provider;

        public ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            OpenAttempts++;
            return ValueTask.FromResult(ConnectivityOutcome<BytePathHandle>.Failure(
                reason,
                "stub path failed"));
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
        {
            _ = handle;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }
}
