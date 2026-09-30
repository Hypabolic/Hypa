using System.Net.Sockets;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class EstablishedJoinSessionTests
{
    [SkippableFact]
    public async Task Watch_keeps_bridged_session_when_admission_expiry_fails()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_estwat", "estwatch1");
        var gate = new ScriptedJoinTrustGate();
        await using var stack = await AcceptStack.StartAsync(muxBoot, gate);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
            await using var client = clientOutcome.Value!;

            gate.AdmissionStillValid = false;
            await Task.Delay(TimeSpan.FromMilliseconds(2500));
            Assert.True(gate.EstablishedCalls >= 1);
            Assert.True((await client.SendAsync(
                StreamFrame.Control(
                    StreamDirection.ClientToMux,
                    0,
                    Encoding.UTF8.GetBytes("""{"ping":true}""")))).Ok);
            var fromMux = await ReadMuxBytesAsync(muxSocket, TimeSpan.FromSeconds(3));
            Assert.Contains("\"ping\":true", Encoding.UTF8.GetString(fromMux), StringComparison.Ordinal);

            gate.EstablishedStillValid = false;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(muxSocket, readCts.Token);
        }
    }

    [SkippableFact]
    public async Task Null_trust_gate_does_not_watch_join()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_estnul", "estnull01");
        await using var stack = await AcceptStack.StartAsync(muxBoot);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
            await using var client = clientOutcome.Value!;
            await Task.Delay(TimeSpan.FromMilliseconds(2500));
            Assert.True((await client.SendAsync(
                StreamFrame.Control(
                    StreamDirection.ClientToMux,
                    0,
                    Encoding.UTF8.GetBytes("""{"ping":true}""")))).Ok);
            var fromMux = await ReadMuxBytesAsync(muxSocket, TimeSpan.FromSeconds(3));
            Assert.Contains("\"ping\":true", Encoding.UTF8.GetString(fromMux), StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Bridged_session_forwards_after_capability_expiry()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var fixture = await PairedJoin.StartAsync("expwatch", "plc_expwch", clock);
        await using var stack = fixture.Stack;
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            fixture.ClientBoot,
            budget: null,
            tlsTrust: null,
            bytePath: null,
            deviceKeys: fixture.Keys,
            certificatePin: null);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
            await using var client = clientOutcome.Value!;

            clock.UtcNow = fixture.ExpiresAt.AddMinutes(5);
            Assert.False(await fixture.Pairing.IsJoinStillValidAsync(
                fixture.ClientBoot,
                clock.GetUtcNow()));
            Assert.True(await fixture.Pairing.IsEstablishedJoinStillValidAsync(
                fixture.ClientBoot,
                clock.GetUtcNow()));
            await Task.Delay(TimeSpan.FromMilliseconds(2500));
            Assert.True((await client.SendAsync(
                StreamFrame.Control(
                    StreamDirection.ClientToMux,
                    0,
                    Encoding.UTF8.GetBytes("""{"ping":true}""")))).Ok);
            var fromMux = await ReadMuxBytesAsync(muxSocket, TimeSpan.FromSeconds(3));
            Assert.Contains("\"ping\":true", Encoding.UTF8.GetString(fromMux), StringComparison.Ordinal);

            var loaded = await fixture.Store.LoadAsync();
            await fixture.Store.SaveAsync(loaded with { JoinCapabilities = [] });
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(muxSocket, readCts.Token);
        }
    }

    [SkippableFact]
    public async Task Deleted_capability_ends_bridged_session()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var fixture = await PairedJoin.StartAsync("delwatch", "plc_delwch");
        await using var stack = fixture.Stack;
        var join = new OutboundDirectJoin(stack.Accept.DialEndpoint, deviceKeys: fixture.Keys);
        var joinTask = join.JoinHeldAsync(fixture.ClientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var outcome = await joinTask;
            Assert.True(outcome.Ok, outcome.Detail);
            await using var held = outcome.Value!;
            var loaded = await fixture.Store.LoadAsync();
            await fixture.Store.SaveAsync(loaded with { JoinCapabilities = [] });
            await AssertHeldClosedAsync(held);
        }
    }

    [SkippableFact]
    public async Task Nonce_mismatch_ends_bridged_session()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var fixture = await PairedJoin.StartAsync("noncwtch", "plc_noncwt");
        await using var stack = fixture.Stack;
        var join = new OutboundDirectJoin(stack.Accept.DialEndpoint, deviceKeys: fixture.Keys);
        var joinTask = join.JoinHeldAsync(fixture.ClientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var outcome = await joinTask;
            Assert.True(outcome.Ok, outcome.Detail);
            await using var held = outcome.Value!;
            Assert.True(JoinNonce.TryParse("nonceoth", out var otherNonce));
            var loaded = await fixture.Store.LoadAsync();
            var capabilities = loaded.JoinCapabilities
                .Select(c => c with { Nonce = otherNonce })
                .ToList();
            await fixture.Store.SaveAsync(loaded with { JoinCapabilities = capabilities });
            await AssertHeldClosedAsync(held);
        }
    }

    [Fact]
    public async Task Expired_capability_fails_accept_admission()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var temp = Path.Combine(Path.GetTempPath(), "hypa-estadm-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys, clock);
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse("admexp01", out var nonce));
        Assert.True(PlacementId.TryParse("plc_admex1", out var placement));
        var issued = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            now.AddMinutes(1));
        Assert.True(issued.Ok, issued.Detail);
        var clientBoot = new JoinBootstrap
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
        var signed = await JoinDeviceAuthenticator.StampAsync(clientBoot, keys);
        Assert.True(signed.Ok, signed.Detail);
        clientBoot = signed.Value!;
        clock.UtcNow = now.AddMinutes(2);
        var muxBoot = JoinTestPairs.SelfHosted("plc_admex1", "admexp01").Mux;
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            trustGate: pairing,
            clock: clock);
        var join = new OutboundDirectJoin(accept.DialEndpoint, deviceKeys: keys);
        var outcome = await join.JoinHeldAsync(clientBoot);
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.JoinExpired, outcome.Reason);
        Assert.Equal("join capability expired", outcome.Detail);
        Assert.Equal(0, recording.BindCalls);
    }

    [Fact]
    public async Task Pre_bridge_expiry_check_still_denies_join()
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_prebrg", "prebridg1");
        var gate = new ScriptedJoinTrustGate { AdmissionStillValid = false };
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            trustGate: gate);
        var join = new OutboundDirectJoin(accept.DialEndpoint);
        var outcome = await join.JoinHeldAsync(clientBoot);
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, outcome.Reason);
        Assert.Equal("device is revoked", outcome.Detail);
        Assert.Equal(0, recording.BindCalls);
        Assert.Equal(0, gate.EstablishedCalls);
    }

    [Fact]
    public async Task Relay_pre_match_wait_denies_expired_capability()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var gate = new LocalSelfHostedJoinTrustGate();
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback(gate, clock);
        var (muxBoot, _) = JoinTestPairs.SelfHosted("plc_relpre", "relpre001", utcNow: now);
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var join = new OutboundRendezvousJoin(url);
        var muxTask = join.JoinHeldAsync(muxBoot).AsTask();
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!relay.HasPendingJoin(muxBoot.Nonce))
        {
            waitCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, waitCts.Token);
        }

        clock.UtcNow = now.AddMinutes(2);
        var outcome = await muxTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, outcome.Reason);
        Assert.Equal("device is revoked", outcome.Detail);
    }

    private static async Task AssertHeldClosedAsync(HeldJoinSession held)
    {
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[1];
        try
        {
            var read = await held.Stream.ReadAsync(buffer, readCts.Token);
            Assert.Equal(0, read);
        }
        catch (IOException)
        {
        }
    }

    private static async Task AssertClosedAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            var read = await socket.ReceiveAsync(buffer, cancellationToken);
            Assert.Equal(0, read);
        }
        catch (SocketException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task<byte[]> ReadMuxBytesAsync(Socket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        try
        {
            var read = await socket.ReceiveAsync(buffer, cts.Token);
            return buffer.AsSpan(0, read).ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    private sealed class ScriptedJoinTrustGate : IJoinTrustGate
    {
        public bool AdmissionOk { get; set; } = true;
        public bool AdmissionStillValid { get; set; } = true;
        public bool EstablishedStillValid { get; set; } = true;
        public int EstablishedCalls;

        public ValueTask<ConnectivityOutcome> AdmitAsync(
            JoinBootstrap bootstrap,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = utcNow;
            _ = cancellationToken;
            return ValueTask.FromResult(
                AdmissionOk
                    ? ConnectivityOutcome.Success()
                    : ConnectivityOutcome.Failure(
                        ConnectivityReasons.JoinExpired,
                        "join capability expired"));
        }

        public ValueTask<bool> IsJoinStillValidAsync(
            JoinBootstrap bootstrap,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = utcNow;
            _ = cancellationToken;
            return ValueTask.FromResult(AdmissionStillValid);
        }

        public ValueTask<bool> IsEstablishedJoinStillValidAsync(
            JoinBootstrap bootstrap,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = utcNow;
            _ = cancellationToken;
            Interlocked.Increment(ref EstablishedCalls);
            return ValueTask.FromResult(EstablishedStillValid);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingBridge : ILocalUnixBridge
    {
        public int BindCalls { get; private set; }

        public bool ExposesUnixSocketPath => false;

        public ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap muxBootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = muxBootstrap;
            _ = cancellationToken;
            BindCalls++;
            return ValueTask.FromResult(
                ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.Internal,
                    "recording bridge does not bind"));
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation) => _ = reservation;

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            _ = reservation;
            return null;
        }
    }

    private sealed class AcceptStack : IAsyncDisposable
    {
        private readonly FakeMux _fakeMux;
        private readonly LocalUnixBridge _bridge;
        private readonly TcpTlsConnectivityAccept _accept;

        private AcceptStack(FakeMux fakeMux, LocalUnixBridge bridge, TcpTlsConnectivityAccept accept)
        {
            _fakeMux = fakeMux;
            _bridge = bridge;
            _accept = accept;
        }

        public FakeMux FakeMux => _fakeMux;
        public TcpTlsConnectivityAccept Accept => _accept;

        public static async Task<AcceptStack> StartAsync(
            JoinBootstrap muxBoot,
            IJoinTrustGate? trustGate = null,
            TimeProvider? clock = null)
        {
            Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
            var fakeMux = await FakeMux.StartAsync();
            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
            var accept = TcpTlsConnectivityAccept.StartLoopback(
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
                trustGate: trustGate,
                clock: clock,
                quicProbe: JoinTestPairs.DisabledQuicProbe);
            return new AcceptStack(fakeMux, bridge, accept);
        }

        public async ValueTask DisposeAsync()
        {
            await _accept.DisposeAsync();
            await _bridge.DisposeAsync();
            await _fakeMux.DisposeAsync();
        }
    }

    private sealed class FakeMux : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        private FakeMux(Socket listener, string socketPath)
        {
            _listener = listener;
            SocketPath = socketPath;
        }

        public string SocketPath { get; }

        public static async Task<FakeMux> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-estmux-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
#pragma warning disable CA1416
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = Path.Combine(dir, "mux.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            listener.Listen(4);
            return await Task.FromResult(new FakeMux(listener, socketPath));
        }

        public async Task<Socket> AcceptAsync()
        {
            return await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _listener.Dispose();
            _cts.Dispose();
            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);
                var dir = Path.GetDirectoryName(SocketPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PairedJoin
    {
        private PairedJoin(
            AcceptStack stack,
            FileDevicePairingStore store,
            IDeviceKeyStore keys,
            JoinBootstrap clientBoot,
            DevicePairingService pairing,
            DateTimeOffset expiresAt)
        {
            Stack = stack;
            Store = store;
            Keys = keys;
            ClientBoot = clientBoot;
            Pairing = pairing;
            ExpiresAt = expiresAt;
        }

        public AcceptStack Stack { get; }
        public FileDevicePairingStore Store { get; }
        public IDeviceKeyStore Keys { get; }
        public JoinBootstrap ClientBoot { get; }
        public DevicePairingService Pairing { get; }
        public DateTimeOffset ExpiresAt { get; }

        public static async Task<PairedJoin> StartAsync(
            string nonceValue,
            string placementValue,
            TimeProvider? clock = null)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hypa-estpair-" + Guid.NewGuid().ToString("N"));
            var store = new FileDevicePairingStore(temp);
            var keys = new FileDeviceKeyStore(temp);
            var pairing = new DevicePairingService(store, keys, clock);
            var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
            Assert.True(started.Ok, started.Detail);
            var approved = await pairing.ApproveAsync(
                OperatorIdentity.LocalSelfHosted,
                started.Value!.PairingCode,
                PairingApprover.SelfHostedConsole);
            Assert.True(approved.Ok, approved.Detail);
            Assert.True(JoinNonce.TryParse(nonceValue, out var nonce));
            Assert.True(PlacementId.TryParse(placementValue, out var placement));
            var now = clock?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            var expires = now.AddMinutes(1);
            var muxCap = await pairing.IssueJoinCapabilityAsync(
                OperatorIdentity.LocalSelfHosted,
                started.Value.DeviceId,
                placement,
                JoinRole.Mux,
                nonce,
                expires);
            var clientCap = await pairing.IssueJoinCapabilityAsync(
                OperatorIdentity.LocalSelfHosted,
                started.Value.DeviceId,
                placement,
                JoinRole.Client,
                nonce,
                expires);
            Assert.True(muxCap.Ok, muxCap.Detail);
            Assert.True(clientCap.Ok, clientCap.Detail);
            var muxBoot = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Mux,
                PlacementId = placement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = muxCap.Value!,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
                EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
            };
            var clientBoot = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = placement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = clientCap.Value!,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
                EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
            };
            var signed = await JoinDeviceAuthenticator.StampAsync(clientBoot, keys);
            Assert.True(signed.Ok, signed.Detail);
            clientBoot = signed.Value!;
            var stack = await AcceptStack.StartAsync(muxBoot, pairing, clock);
            return new PairedJoin(stack, store, keys, clientBoot, pairing, expires);
        }
    }
}
