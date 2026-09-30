using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class AcceptFrameTerminatorTests
{
    [SkippableFact]
    public async Task Direct_join_session_snapshot_returns_json()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath();
        try
        {
            await using var mux = await LiveControlPlane.StartAsync(dir, sock);
            var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_snap01", "snap0001");
            using var cert = JoinTestPairs.CreateLoopbackCertificate();
            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(mux.SocketPath, new AcceptPathJoin(coordinator));
            await using var accept = TcpTlsConnectivityAccept.StartLoopback(
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
                tlsCertificate: cert,
                quicProbe: JoinTestPairs.DisabledQuicProbe);

            var connected = await OutboundFramedSession.ConnectDirectAsync(
                accept.DialEndpoint,
                clientBoot,
                tlsTrust: cert,
                bytePath: new TcpTlsBytePath(cert));
            Assert.True(connected.Ok, connected.Detail);
            await using var session = connected.Value!;
            await using var framed = new FramedControlStream(session, ownsSession: false);
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
        }
    }

    [Fact]
    public void Mux_bootstrap_factory_stamps_a_complementary_mux_role()
    {
        var (_, clientBoot) = JoinTestPairs.SelfHosted("plc_fact01", "fact0001");
        var mux = AcceptMuxBootstrap.FromClient(clientBoot);
        Assert.True(mux.Ok, mux.Detail);
        Assert.Equal(JoinRole.Mux, mux.Value!.Role);
        Assert.Equal(clientBoot.PlacementId, mux.Value.PlacementId);
        Assert.Equal(clientBoot.Nonce, mux.Value.Nonce);
    }

    [Fact]
    public void Accepted_session_requires_mux_role()
    {
        using var keys = JoinEphemeralKeyPair.Create();
        var cipher = ApplicationFrameCipher.Create(
            new byte[32],
            JoinNonce.TryParse("nonceacc", out var nonce) ? nonce : default,
            "plc_role01",
            JoinRole.Mux);
        Assert.True(cipher.Ok, cipher.Detail);
        using var stream = new MemoryStream();
        var binding = new JoinBinding
        {
            PlacementId = PlacementId.TryParse("plc_role01", out var placement) ? placement : default,
            StreamClass = StreamClass.Control,
            Role = JoinRole.Client,
            PeerEphPublicKey = keys.PublicKey,
        };
        Assert.Throws<ArgumentException>(() =>
            OutboundFramedSession.FromAccepted(stream, binding, cipher.Value!));
    }

    private static (string Dir, string Sock) NewPrivateSocketPath()
    {
        var dir = Path.Combine("/tmp", "hypa-acc-" + Guid.NewGuid().ToString("N")[..8]);
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
            var state = new AppState(SessionId.New("accept-snap"));
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
