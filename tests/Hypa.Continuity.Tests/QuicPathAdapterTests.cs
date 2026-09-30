using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class QuicPathAdapterTests
{
    [Fact]
    public async Task Unsupported_quic_provider_is_rejected_by_tcp_byte_path()
    {
        var path = new TcpTlsBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = BytePathProviders.Quic,
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = 1,
                },
            });
        Assert.False(opened.Ok);
        Assert.Equal(ConnectivityReasons.Internal, opened.Reason);
    }

    [Fact]
    public async Task Quic_byte_path_fails_closed_when_runtime_is_unsupported()
    {
        if (QuicTransportRuntime.IsUsable())
            return;

        var path = new QuicBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = BytePathProviders.Quic,
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = 1,
                    Tls = true,
                    TlsServerName = "127.0.0.1",
                },
            });
        Assert.False(opened.Ok);
        Assert.Equal(ConnectivityReasons.QuicUnsupported, opened.Reason);
    }

    [SkippableFact]
    public async Task Fallback_uses_tcp_immediately_on_cleartext_endpoint()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var failingQuic = new FailingQuicBytePath();
        var recordingTcp = new RecordingBytePath(new TcpTlsBytePath());
        var bytePath = new QuicTcpFallbackBytePath(
            probe: new SupportedQuicProbe(),
            quicPath: failingQuic,
            tcpPath: recordingTcp);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qclr", "qclr0001");
        await using var stack = await AcceptStack.StartAsync(muxBoot, tlsCertificate: null);
        var started = DateTimeOffset.UtcNow;
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }

        Assert.Equal(0, failingQuic.OpenAttempts);
        Assert.Equal(1, recordingTcp.OpenCount);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task Fallback_uses_tcp_when_quic_path_fails()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        SkipOnHostedMacOs();
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var failingQuic = new FailingQuicBytePath();
        var recordingTcp = new RecordingBytePath(new TcpTlsBytePath(cert));
        var bytePath = new QuicTcpFallbackBytePath(
            tlsTrust: cert,
            probe: new SupportedQuicProbe(),
            quicPath: failingQuic,
            tcpPath: recordingTcp);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qfst", "qfst0001");
        await using var stack = await AcceptStack.StartAsync(muxBoot, tlsCertificate: cert);
        var endpoint = stack.Accept.DialEndpoint with { QuicListening = true };
        var started = DateTimeOffset.UtcNow;
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            endpoint,
            clientBoot,
            tlsTrust: cert,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }

        Assert.Equal(1, failingQuic.OpenAttempts);
        Assert.Equal(1, recordingTcp.OpenCount);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [SkippableFact]
    public async Task Fallback_skips_quic_when_accept_reports_tcp_only()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        SkipOnHostedMacOs();
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var failingQuic = new FailingQuicBytePath();
        var recordingTcp = new RecordingBytePath(new TcpTlsBytePath(cert));
        var bytePath = new QuicTcpFallbackBytePath(
            tlsTrust: cert,
            probe: new SupportedQuicProbe(),
            quicPath: failingQuic,
            tcpPath: recordingTcp);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qtcp", "qtcp0001");
        await using var stack = await AcceptStack.StartAsync(muxBoot, tlsCertificate: cert);
        var endpoint = stack.Accept.DialEndpoint with { QuicListening = false };
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            endpoint,
            clientBoot,
            tlsTrust: cert,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }

        Assert.Equal(0, failingQuic.OpenAttempts);
        Assert.Equal(1, recordingTcp.OpenCount);
    }

    [SkippableFact]
    public async Task Tcp_join_succeeds_while_quic_accept_waits_with_one_shared_slot()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qidl", "qidl0001");
        await using var stack = await AcceptStack.StartAsync(
            muxBoot,
            tlsCertificate: null,
            maxConcurrentHandshakes: 1);
        using var idleCancel = new CancellationTokenSource();
        var idleAccept = stack.Accept.AcceptOneQuicConnectionForTestAsync(
            WaitForeverForQuicConnectionAsync,
            idleCancel.Token);
        await Task.Delay(100);
        Assert.Equal(1, stack.Accept.AvailableHandshakeSlots);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(stack.Accept.DialEndpoint, clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }

        idleCancel.Cancel();
        try
        {
            await idleAccept;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [SkippableFact]
    public async Task Cleartext_bind_with_certificate_does_not_start_quic_listener()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qclt", "qclt0001");
        await using var stack = await AcceptStack.StartCleartextWithCertAsync(muxBoot, cert);
        Assert.False(stack.Accept.UsesTls);
        Assert.False(stack.Accept.QuicListening);
        Assert.False(stack.Accept.DialEndpoint.Tls);
        Assert.False(stack.Accept.DialEndpoint.QuicListening ?? false);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot,
            bytePath: new TcpTlsBytePath());
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }
    }

    [SkippableFact]
    public async Task Simulated_quic_auth_failures_do_not_stop_accept_loop()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qaut", "qaut0001");
        await using var stack = await AcceptStack.StartAsync(muxBoot, tlsCertificate: null);
        for (var i = 0; i < ConnectivityAcceptLimits.MaxConcurrentHandshakes + 8; i++)
        {
            await stack.Accept.AcceptOneQuicConnectionForTestAsync(
                static _ => throw new AuthenticationException("simulated quic handshake failure"),
                CancellationToken.None);
        }

        Assert.Equal(ConnectivityAcceptLimits.MaxConcurrentHandshakes, stack.Accept.AvailableHandshakeSlots);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(stack.Accept.DialEndpoint, clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }
    }

    [SkippableFact]
    public async Task Simulated_quic_accept_failures_do_not_exhaust_handshake_slots()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qsls", "qsls0001");
        await using var stack = await AcceptStack.StartAsync(muxBoot, tlsCertificate: null);
        for (var i = 0; i < ConnectivityAcceptLimits.MaxConcurrentHandshakes + 8; i++)
        {
            await stack.Accept.AcceptOneQuicConnectionForTestAsync(
                static _ => throw new IOException("simulated quic accept failure"),
                CancellationToken.None);
        }

        Assert.Equal(ConnectivityAcceptLimits.MaxConcurrentHandshakes, stack.Accept.AvailableHandshakeSlots);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(stack.Accept.DialEndpoint, clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }
    }

    [Fact]
    public async Task Abandoned_quic_attempt_disposes_late_success_handle()
    {
        var lifetime = new TrackingLifetime();
        var lateTask = Task.Run(async () =>
        {
            await Task.Delay(150);
            return ConnectivityOutcome<BytePathHandle>.Success(new BytePathHandle
            {
                Stream = new MemoryStream(),
                Lifetime = lifetime,
            });
        });

        _ = QuicTcpFallbackBytePath.ObserveAbandonedQuicAttemptForTestAsync(lateTask);
        Assert.False(lifetime.Disposed);
        await Task.Delay(400);
        Assert.True(lifetime.Disposed);
    }

    [Fact]
    public async Task Abandoned_quic_attempt_observes_late_exceptions()
    {
        var lateTask = Task.Run(ThrowLateQuicDialFailureAsync);
        await QuicTcpFallbackBytePath.ObserveAbandonedQuicAttemptForTestAsync(lateTask);
    }

    private static async Task<ConnectivityOutcome<BytePathHandle>> ThrowLateQuicDialFailureAsync()
    {
        await Task.Delay(50);
        throw new IOException("late quic dial failed");
    }

    private static async Task<System.Net.Quic.QuicConnection> WaitForeverForQuicConnectionAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public async Task Caller_cancel_aborts_open_without_tcp_fallback()
    {
        var blockingQuic = new BlockingQuicBytePath();
        var recordingTcp = new RecordingBytePath(new TcpTlsBytePath());
        var bytePath = new QuicTcpFallbackBytePath(
            probe: new SupportedQuicProbe(),
            quicPath: blockingQuic,
            tcpPath: recordingTcp);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var endpoint = new BytePathEndpoint
        {
            Host = "127.0.0.1",
            Port = 9,
            Tls = true,
            TlsServerName = "127.0.0.1",
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bytePath.OpenAsync(
                    new BytePathRequest
                    {
                        Provider = BytePathProviders.Quic,
                        Endpoint = endpoint,
                    },
                    cancel.Token)
                .AsTask());
        Assert.Equal(1, blockingQuic.OpenAttempts);
        Assert.Equal(0, recordingTcp.OpenCount);
    }

    [SkippableFact]
    [Trait("Category", "LiveQuic")]
    public async Task Live_quic_direct_join_completes_without_rendezvous()
    {
        Skip.IfNot(QuicTransportRuntime.IsUsable());
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qjoin", "qjoin001");
        await using var stack = await AcceptStack.StartAsync(
            muxBoot,
            tlsCertificate: cert,
            bindHost: IPAddress.Any.ToString(),
            trustGate: new LocalSelfHostedJoinTrustGate());
        Skip.IfNot(stack.Accept.QuicListening);
        var bytePath = new QuicBytePath(cert);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint with { Host = IPAddress.Loopback.ToString() },
            clientBoot,
            tlsTrust: cert,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
            await using var client = clientOutcome.Value!;
            Assert.True((await client.SendAsync(
                StreamFrame.Control(
                    StreamDirection.ClientToMux,
                    0,
                    Encoding.UTF8.GetBytes("""{"quic":true}""")))).Ok);
        }
    }

    [SkippableFact]
    [Trait("Category", "LiveQuic")]
    public async Task Live_quic_framed_session_preserves_sequence_and_encryption()
    {
        Skip.IfNot(QuicTransportRuntime.IsUsable());
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qfrm", "qfrm0001");
        await using var stack = await AcceptStack.StartAsync(
            muxBoot,
            tlsCertificate: cert,
            bindHost: IPAddress.Any.ToString(),
            trustGate: new LocalSelfHostedJoinTrustGate());
        Skip.IfNot(stack.Accept.QuicListening);
        var bytePath = new QuicBytePath(cert);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint with { Host = IPAddress.Loopback.ToString() },
            clientBoot,
            tlsTrust: cert,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
            await using var client = clientOutcome.Value!;
            Assert.True(client.ApplicationEncryptionEnabled);
            Assert.True((await client.SendAsync(
                StreamFrame.Binary(
                    StreamDirection.ClientToMux,
                    1,
                    0,
                    Encoding.UTF8.GetBytes("payload")))).Ok);
            Assert.Empty(client.LastReceived);
        }
    }

    [SkippableFact]
    public async Task Rendezvous_relay_still_works_with_tcp_byte_path()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_qrelay", "qrelay01");
        var bytePath = new TcpTlsBytePath();
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var muxTask = OutboundFramedSession.ConnectAsync(url, muxBoot, bytePath: bytePath);
        var clientTask = OutboundFramedSession.ConnectAsync(url, clientBoot, bytePath: bytePath);
        var muxOutcome = await muxTask;
        var clientOutcome = await clientTask;
        Assert.True(muxOutcome.Ok, muxOutcome.Detail);
        Assert.True(clientOutcome.Ok, clientOutcome.Detail);
    }

    [Fact]
    public async Task Accept_helper_starts_without_error_when_tls_is_configured()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var bind = new ConnectivityAcceptBind
        {
            Host = IPAddress.Loopback.ToString(),
            Port = 0,
            Tls = true,
            TlsServerName = "127.0.0.1",
        };
        await using var accept = new TcpTlsConnectivityAccept(
            bind,
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(ConnectivityReasons.Internal, "unused"),
            tlsCertificate: cert);
        accept.Run();
        Assert.True(accept.Port > 0);
        if (QuicTransportRuntime.IsUsable())
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (accept.QuicListening || accept.QuicStartupDetail is not null)
                    break;
                await Task.Delay(50);
            }

            Assert.True(accept.QuicListening || accept.QuicStartupDetail is not null);
        }
        else
        {
            Assert.False(accept.QuicListening);
        }

        if (accept.QuicListening)
            Assert.True(accept.DialEndpoint.QuicListening);
        else
            Assert.False(accept.DialEndpoint.QuicListening ?? false);
    }

    // The TLS handshake in these two fallback tests fails on the hosted macOS
    // runner ("Authentication failed"). Both pass on Linux and on a developer Mac.
    // Other TLS direct-join tests pass on that runner.
    private static void SkipOnHostedMacOs() =>
        Skip.If(
            OperatingSystem.IsMacOS()
            && string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            "the hosted macOS runner fails the TLS handshake in this test");

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

    private sealed class FailingQuicBytePath : IBytePath
    {
        public int OpenAttempts { get; private set; }

        public string Provider => BytePathProviders.Quic;

        public ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            OpenAttempts++;
            return ValueTask.FromResult(ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.PeerUnavailable,
                "quic stub failed"));
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
        {
            _ = handle;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingQuicBytePath : IBytePath
    {
        public int OpenAttempts { get; private set; }

        public string Provider => BytePathProviders.Quic;

        public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            OpenAttempts++;
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return ConnectivityOutcome<BytePathHandle>.Failure(
                ConnectivityReasons.Internal,
                "unreachable");
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
        {
            _ = handle;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingLifetime : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingBytePath(IBytePath inner) : IBytePath
    {
        public int OpenCount { get; private set; }

        public string Provider => inner.Provider;

        public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return await inner.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default) =>
            inner.CloseAsync(handle, cancellationToken);
    }

    private sealed class AcceptStack : IAsyncDisposable
    {
        private readonly FakeMuxUnix _fakeMux;
        private readonly LocalUnixBridge _bridge;
        private readonly TcpTlsConnectivityAccept _accept;

        private AcceptStack(FakeMuxUnix fakeMux, LocalUnixBridge bridge, TcpTlsConnectivityAccept accept)
        {
            _fakeMux = fakeMux;
            _bridge = bridge;
            _accept = accept;
        }

        public FakeMuxUnix FakeMux => _fakeMux;
        public TcpTlsConnectivityAccept Accept => _accept;

        public static Task<AcceptStack> StartAsync(
            JoinBootstrap muxBoot,
            X509Certificate2? tlsCertificate = null,
            int maxConcurrentHandshakes = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
            string? bindHost = null,
            IJoinTrustGate? trustGate = null) =>
            StartWithBindAsync(
                muxBoot,
                new ConnectivityAcceptBind
                {
                    Host = bindHost ?? IPAddress.Loopback.ToString(),
                    Port = 0,
                    Tls = tlsCertificate is not null,
                    TlsServerName = "127.0.0.1",
                },
                tlsCertificate,
                maxConcurrentHandshakes,
                trustGate);

        public static Task<AcceptStack> StartCleartextWithCertAsync(
            JoinBootstrap muxBoot,
            X509Certificate2 tlsCertificate) =>
            StartWithBindAsync(
                muxBoot,
                new ConnectivityAcceptBind
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = 0,
                    Tls = false,
                },
                tlsCertificate);

        private static async Task<AcceptStack> StartWithBindAsync(
            JoinBootstrap muxBoot,
            ConnectivityAcceptBind bind,
            X509Certificate2? tlsCertificate,
            int maxConcurrentHandshakes = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
            IJoinTrustGate? trustGate = null)
        {
            Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
            var fakeMux = await FakeMuxUnix.StartAsync();
            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
            var accept = new TcpTlsConnectivityAccept(
                bind,
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
                trustGate: trustGate,
                tlsCertificate: tlsCertificate,
                maxConcurrentHandshakes: maxConcurrentHandshakes);
            accept.Run();
            if (bind.Tls && tlsCertificate is not null && QuicTransportRuntime.IsUsable())
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (accept.QuicListening || accept.QuicStartupDetail is not null)
                        break;
                    await Task.Delay(50);
                }
            }

            return new AcceptStack(fakeMux, bridge, accept);
        }

        public async ValueTask DisposeAsync()
        {
            await _accept.DisposeAsync();
            await _bridge.DisposeAsync();
            await _fakeMux.DisposeAsync();
        }
    }

    private sealed class FakeMuxUnix : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        private FakeMuxUnix(Socket listener, string socketPath)
        {
            _listener = listener;
            SocketPath = socketPath;
        }

        public string SocketPath { get; }

        public static async Task<FakeMuxUnix> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-qmux-" + Guid.NewGuid().ToString("N"));
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
            return await Task.FromResult(new FakeMuxUnix(listener, socketPath));
        }

        public async Task<Socket> AcceptAsync() =>
            await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);

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

    private sealed class RecordingLocalUnixBridge : ILocalUnixBridge
    {
        public bool ExposesUnixSocketPath => false;

        public ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = cancellationToken;
            return ValueTask.FromResult(ConnectivityOutcome<MuxBridgeBind>.Failure(
                ConnectivityReasons.Internal,
                "recording bridge does not bind"));
        }

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            _ = reservation;
            return null;
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation)
        {
            _ = reservation;
        }
    }
}
