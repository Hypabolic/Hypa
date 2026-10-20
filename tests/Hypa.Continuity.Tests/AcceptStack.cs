using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Tests.Support;
using Xunit;

namespace Hypa.Continuity.Tests;

/// <summary>
/// The real accept path on loopback: a mux Unix socket, the
/// <see cref="LocalUnixBridge"/>, and a <see cref="TcpTlsConnectivityAccept"/>
/// that listens on TCP and, with a certificate, on QUIC. The mux side is a
/// <see cref="FakeMuxUnix"/>, or the socket of a live mux.
/// </summary>
internal sealed class AcceptStack : IAsyncDisposable
{
    private readonly FakeMuxUnix? _fakeMux;
    private readonly LocalUnixBridge _bridge;
    private readonly TcpTlsConnectivityAccept _accept;

    private AcceptStack(FakeMuxUnix? fakeMux, LocalUnixBridge bridge, TcpTlsConnectivityAccept accept)
    {
        _fakeMux = fakeMux;
        _bridge = bridge;
        _accept = accept;
    }

    public FakeMuxUnix FakeMux => _fakeMux ?? throw new InvalidOperationException("this stack bridges to a live mux");

    public TcpTlsConnectivityAccept Accept => _accept;

    public static Task<AcceptStack> StartAsync(
        JoinBootstrap muxBoot,
        X509Certificate2? tlsCertificate = null,
        int maxConcurrentHandshakes = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
        string? bindHost = null,
        IJoinTrustGate? trustGate = null) =>
        StartWithBindAsync(
            muxSocketPath: null,
            muxBoot,
            LoopbackBind(bindHost, tlsCertificate is not null),
            tlsCertificate,
            maxConcurrentHandshakes,
            trustGate);

    /// <summary>Bridges the accept to the Unix socket of a live mux.</summary>
    public static Task<AcceptStack> StartForMuxSocketAsync(
        string muxSocketPath,
        JoinBootstrap muxBoot,
        X509Certificate2? tlsCertificate,
        string? bindHost = null,
        IJoinTrustGate? trustGate = null) =>
        StartWithBindAsync(
            muxSocketPath,
            muxBoot,
            LoopbackBind(bindHost, tlsCertificate is not null),
            tlsCertificate,
            ConnectivityAcceptLimits.MaxConcurrentHandshakes,
            trustGate);

    public static Task<AcceptStack> StartCleartextWithCertAsync(
        JoinBootstrap muxBoot,
        X509Certificate2 tlsCertificate) =>
        StartWithBindAsync(
            muxSocketPath: null,
            muxBoot,
            new ConnectivityAcceptBind
            {
                Host = IPAddress.Loopback.ToString(),
                Port = 0,
                Tls = false,
            },
            tlsCertificate,
            ConnectivityAcceptLimits.MaxConcurrentHandshakes,
            trustGate: null);

    private static ConnectivityAcceptBind LoopbackBind(string? bindHost, bool tls) =>
        new()
        {
            Host = bindHost ?? IPAddress.Loopback.ToString(),
            Port = 0,
            Tls = tls,
            TlsServerName = "127.0.0.1",
        };

    private static async Task<AcceptStack> StartWithBindAsync(
        string? muxSocketPath,
        JoinBootstrap muxBoot,
        ConnectivityAcceptBind bind,
        X509Certificate2? tlsCertificate,
        int maxConcurrentHandshakes,
        IJoinTrustGate? trustGate)
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var fakeMux = muxSocketPath is null ? await FakeMuxUnix.StartAsync() : null;
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(muxSocketPath ?? fakeMux!.SocketPath, new AcceptPathJoin(coordinator));
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
            // The accept settles the QUIC start: a listener, or a startup detail.
            await Poll.GuardAsync(accept.WaitUntilReadyAsync(), "the QUIC start of the accept");
        }

        return new AcceptStack(fakeMux, bridge, accept);
    }

    public async ValueTask DisposeAsync()
    {
        await _accept.DisposeAsync();
        await _bridge.DisposeAsync();
        if (_fakeMux is not null)
            await _fakeMux.DisposeAsync();
    }
}

/// <summary>A Unix socket listener that stands in for the mux.</summary>
internal sealed class FakeMuxUnix : IAsyncDisposable
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
