using System.Net;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Hypa.Placement.Domain;
using PlacementId = Hypa.Placement.Domain.PlacementId;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class CubesDirectConnectAttachTests
{
    [SkippableFact]
    public async Task Named_peer_direct_join_reads_session_snapshot_without_rendezvous()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath();
        var pairingDir = Path.Combine(Path.GetTempPath(), "hypa-pair-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var mux = await LiveControlPlane.StartAsync(dir, sock);
            using var cert = JoinTestPairs.CreateLoopbackCertificate();
            var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
            var pairing = new DevicePairingService(
                new FileDevicePairingStore(pairingDir),
                new FileDeviceKeyStore(pairingDir));
            var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
            Assert.True(started.Ok, started.Detail);
            var approved = await pairing.ApproveAsync(
                OperatorIdentity.LocalSelfHosted,
                started.Value!.PairingCode,
                PairingApprover.SelfHostedConsole);
            Assert.True(approved.Ok, approved.Detail);

            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(mux.SocketPath, new AcceptPathJoin(coordinator));
            await using var accept = TcpTlsConnectivityAccept.StartLoopback(
                bridge,
                coordinator,
                AcceptMuxBootstrap.FromClient,
                trustGate: pairing,
                tlsCertificate: cert,
                quicProbe: JoinTestPairs.DisabledQuicProbe);
            pairing.AttachJoinCloser(accept);

            var placement = new PlacementRecord
            {
                Id = PlacementId.Parse("plc_quic01"),
                Owner = DirectoryIdentity.Parse("local:test"),
                DisplayName = "Edge",
                Kind = PlacementDirectoryKind.Peer,
                MuxIdentity = MuxIdentity.Parse("mux_agents"),
                Reachability = PlacementReachability.Reachable,
                LastSeen = DateTimeOffset.UtcNow,
                Quic = new QuicPlacementProfile
                {
                    Id = "plc_quic01",
                    Label = "Edge",
                    Target = $"127.0.0.1:{accept.Port}",
                    Session = "agents",
                    Enabled = true,
                    CertificateSha256 = fingerprint,
                },
            };
            var source = new PairingCubesConnectDirectMaterialSource(pairing);
            var material = await source.GetAsync(placement);
            Assert.NotNull(material);
            Assert.True(material!.Bootstrap.Capability.Secret.IsEmpty);

            await using var session = await CubesConnectDirectOutboundJoin.ConnectAsync(
                material,
                new TcpTlsBytePath(certificatePin: TlsCertificatePin.TryParse(fingerprint, out var pin) ? pin : default));
            Assert.NotNull(session);
            await using var framed = new FramedControlStream(session!, ownsSession: false);
            await using var client = ControlPlaneClient.FromConnectedStream(
                framed,
                callTimeout: TimeSpan.FromSeconds(10));
            await client.ConnectAsync();
            var snapshot = await client.CallAsync(ProtocolMethods.SessionSnapshot);
            Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.GetProperty("session_id").GetString()));
        }
        finally
        {
            TryDeleteTree(dir);
            TryDeleteTree(pairingDir);
        }
    }

    [SkippableFact]
    public async Task Mismatched_certificate_pin_fails_closed()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath();
        try
        {
            await using var mux = await LiveControlPlane.StartAsync(dir, sock);
            using var listenCert = JoinTestPairs.CreateLoopbackCertificate();
            using var otherCert = AcceptListenCertificate.CreateSelfSigned(IPAddress.Loopback);
            var wrongPin = AcceptListenCertificate.Sha256Fingerprint(otherCert);
            Assert.True(TlsCertificatePin.TryParse(wrongPin, out var pin));

            var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_pin01", "pin00001");
            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(mux.SocketPath, new AcceptPathJoin(coordinator));
            await using var accept = TcpTlsConnectivityAccept.StartLoopback(
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
                tlsCertificate: listenCert,
                quicProbe: JoinTestPairs.DisabledQuicProbe);

            var connected = await OutboundFramedSession.ConnectDirectAsync(
                accept.DialEndpoint,
                clientBoot,
                budget: null,
                tlsTrust: null,
                bytePath: new TcpTlsBytePath(certificatePin: pin),
                deviceKeys: null,
                certificatePin: pin);
            Assert.False(connected.Ok);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public async Task Spent_pairing_capability_fails_closed_on_second_admit()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-spent-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(temp),
            new FileDeviceKeyStore(temp));
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse("spent001", out var nonce));
        Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse("plc_spent1", out var placement));
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var issued = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            expires);
        Assert.True(issued.Ok, issued.Detail);
        var bootstrap = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = issued.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var signed = await JoinDeviceAuthenticator.StampAsync(bootstrap, pairing.KeyStore);
        Assert.True(signed.Ok, signed.Detail);
        var first = await pairing.AdmitAsync(signed.Value!, DateTimeOffset.UtcNow);
        Assert.True(first.Ok, first.Detail);
        var second = await pairing.AdmitAsync(signed.Value!, DateTimeOffset.UtcNow);
        Assert.False(second.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, second.Reason);
    }

    private static (string Dir, string Sock) NewPrivateSocketPath()
    {
        var dir = Path.Combine("/tmp", "hypa-qcon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "hypa.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class LiveControlPlane : IAsyncDisposable
    {
        private readonly UnixSocketServer _server;

        private LiveControlPlane(ControlPlaneService controlPlane, UnixSocketServer server, string socketPath)
        {
            ControlPlane = controlPlane;
            _server = server;
            SocketPath = socketPath;
        }

        public ControlPlaneService ControlPlane { get; }

        public string SocketPath { get; }

        public static async Task<LiveControlPlane> StartAsync(string socketDir, string socketPath)
        {
            _ = socketDir;
            var state = new AppState(SessionId.New("quic-con"));
            var cp = new ControlPlaneService(
                state,
                new UnusedPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            var server = new UnixSocketServer(cp, socketPath);
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new LiveControlPlane(cp, server, socketPath);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            await ControlPlane.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class UnusedPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            throw new InvalidOperationException("unused pane factory");
    }

    private sealed class NullIntelligence : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
