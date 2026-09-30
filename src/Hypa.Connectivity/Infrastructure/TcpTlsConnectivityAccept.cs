using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// TLS/TCP and optional QUIC accept helper outside the mux. Trust runs before the Unix bridge.
/// </summary>
public sealed class TcpTlsConnectivityAccept : IConnectivityAccept, IActiveJoinCloser
{
    private readonly TcpListener _listener;
    private readonly AcceptJoinStreamHandler _streamHandler;
    private readonly AcceptJoinCoordinator _coordinator;
    private readonly ILocalUnixBridge _unixBridge;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly SemaphoreSlim _handshakeSlots;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _handlerGate = new();
    private readonly List<Task> _handlers = [];
    private readonly ConnectivityAcceptBind _bind;
    private readonly IPAddress _bindAddress;
    private readonly bool _startQuicListener;
    private bool _quicListening;
    private string? _quicStartupDetail;
    private IPAddress? _quicBindAddress;
    private QuicListener? _quicListener;
    private Task? _tcpAccept;
    private Task? _quicAccept;
    private readonly TaskCompletionSource _quicSettled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TcpTlsConnectivityAccept(
        ConnectivityAcceptBind bind,
        ILocalUnixBridge unixBridge,
        AcceptJoinCoordinator coordinator,
        Func<JoinBootstrap, ConnectivityOutcome<JoinBootstrap>> muxBootstrapFactory,
        IJoinTrustGate? trustGate = null,
        TimeProvider? clock = null,
        X509Certificate2? tlsCertificate = null,
        int maxConcurrentHandshakes = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
        IQuicTransportCapabilityProbe? quicProbe = null,
        Action<DeviceRecord>? onInviteRedeemed = null)
    {
        if (!ConnectivityAcceptBindRules.TryResolveBindAddress(bind, out var address, out var invalid))
            throw new ArgumentException(invalid, nameof(bind));

        ArgumentNullException.ThrowIfNull(unixBridge);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(muxBootstrapFactory);
        if (bind.Tls && tlsCertificate is null)
            throw new ArgumentException("tls certificate is required when accept bind uses tls", nameof(tlsCertificate));
        if (!ConnectivityAcceptBindRules.IsLoopbackOnly(address))
        {
            if (trustGate is null)
            {
                throw new ArgumentException(
                    "trust gate is required for non-loopback accept bind",
                    nameof(trustGate));
            }

            if (!bind.Tls || tlsCertificate is null)
            {
                throw new ArgumentException(
                    "tls is required for non-loopback accept bind",
                    nameof(bind));
            }
        }

        if (maxConcurrentHandshakes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentHandshakes));

        _unixBridge = unixBridge;
        _coordinator = coordinator;
        _tlsCertificate = tlsCertificate;
        _handshakeSlots = new SemaphoreSlim(maxConcurrentHandshakes, maxConcurrentHandshakes);
        _streamHandler = new AcceptJoinStreamHandler(
            unixBridge,
            coordinator,
            muxBootstrapFactory,
            trustGate,
            clock ?? TimeProvider.System,
            onInviteRedeemed);

        _bind = bind;
        _listener = new TcpListener(address, bind.Port);
        if (IPAddress.IPv6Any.Equals(address))
            _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        UsesTls = bind.Tls;
        _bindAddress = address;
        var tlsReady = UsesTls && _tlsCertificate is not null;
        // Load MsQuic only when this accept can use it.
        var report = tlsReady ? QuicTransportRuntime.Probe(quicProbe) : null;
        var quicCapable = tlsReady && report!.IsSupported;
        // MsQuic ListenerStart ignores the address for the UDP socket.
        // It binds a dual-mode wildcard socket and filters later.
        // A specific address must not leave that wildcard socket open.
        _startQuicListener = quicCapable && ConnectivityAcceptBindRules.IsWildcard(address);
        _quicListening = false;
        if (quicCapable && !_startQuicListener)
        {
            _quicStartupDetail = QuicAcceptBindNotice.SpecificAddressDetail;
            DialEndpoint = BuildDialEndpoint(quicListening: false);
            _quicSettled.TrySetResult();
        }
        else if (tlsReady && !report!.IsSupported)
        {
            _quicStartupDetail = CapabilityDetail(report);
            DialEndpoint = BuildDialEndpoint(quicListening: false);
            _quicSettled.TrySetResult();
        }
        else
        {
            DialEndpoint = BuildDialEndpoint(quicListening: quicCapable ? null : false);
            if (!quicCapable)
                _quicSettled.TrySetResult();
        }
    }

    private static string CapabilityDetail(QuicTransportCapabilityReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.Detail))
            return report.Detail;
        if (!string.IsNullOrWhiteSpace(report.Reason))
            return report.Reason;
        return "quic transport is not supported on this host";
    }

    public IPEndPoint TcpListenEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public IPAddress? QuicBindAddress => _quicBindAddress;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        _quicSettled.Task.WaitAsync(cancellationToken);

    public int Port { get; }

    public bool UsesTls { get; }

    public bool QuicListening => _quicListening;

    public string? QuicStartupDetail => _quicStartupDetail;

    public BytePathEndpoint DialEndpoint { get; private set; }

    public static TcpTlsConnectivityAccept StartLoopback(
        ILocalUnixBridge unixBridge,
        AcceptJoinCoordinator coordinator,
        Func<JoinBootstrap, ConnectivityOutcome<JoinBootstrap>> muxBootstrapFactory,
        IJoinTrustGate? trustGate = null,
        TimeProvider? clock = null,
        X509Certificate2? tlsCertificate = null,
        int maxConcurrentHandshakes = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
        IQuicTransportCapabilityProbe? quicProbe = null,
        Action<DeviceRecord>? onInviteRedeemed = null)
    {
        var bind = new ConnectivityAcceptBind
        {
            Host = IPAddress.Loopback.ToString(),
            Port = 0,
            Tls = tlsCertificate is not null,
            TlsServerName = QuicTransportConstants.LoopbackServerName,
        };
        var accept = new TcpTlsConnectivityAccept(
            bind,
            unixBridge,
            coordinator,
            muxBootstrapFactory,
            trustGate,
            clock,
            tlsCertificate,
            maxConcurrentHandshakes,
            quicProbe,
            onInviteRedeemed);
        accept.Run();
        return accept;
    }

    public void Run()
    {
        _tcpAccept ??= TcpAcceptLoopAsync(_cts.Token);
        if (_startQuicListener)
            _quicAccept ??= StartQuicAcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _listener.Stop();
        _quicSettled.TrySetResult();
        if (_quicListener is not null)
        {
            try
            {
#pragma warning disable CA1416
                await _quicListener.DisposeAsync().ConfigureAwait(false);
#pragma warning restore CA1416
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var acceptTasks = new List<Task>();
        if (_tcpAccept is not null)
            acceptTasks.Add(_tcpAccept);
        if (_quicAccept is not null)
            acceptTasks.Add(_quicAccept);

        foreach (var acceptTask in acceptTasks)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AuthenticationException)
            {
            }
        }

        await WaitHandlersAsync().ConfigureAwait(false);
        _coordinator.RevokeAllSessions();
        _cts.Dispose();
        _handshakeSlots.Dispose();
        if (_unixBridge is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    public void CloseJoinsForDevice(DeviceId deviceId) =>
        _coordinator.CloseJoinsForDevice(deviceId);

    private async Task StartQuicAcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (!UsesTls || _tlsCertificate is null)
            return;

#pragma warning disable CA1416
        try
        {
            _quicListener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(_bindAddress, Port),
                        ListenBacklog = ConnectivityAcceptLimits.MaxConcurrentHandshakes,
                        ApplicationProtocols = [QuicTransportConstants.ApplicationProtocol],
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
                            new QuicServerConnectionOptions
                            {
                                DefaultCloseErrorCode = QuicTransportConstants.DefaultCloseErrorCode,
                                DefaultStreamErrorCode = QuicTransportConstants.DefaultStreamErrorCode,
                                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                                {
                                    ApplicationProtocols = [QuicTransportConstants.ApplicationProtocol],
                                    ServerCertificateContext = SslStreamCertificateContext.Create(
                                        _tlsCertificate,
                                        additionalCertificates: null),
                                    ClientCertificateRequired = false,
                                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                },
                                MaxInboundBidirectionalStreams =
                                    QuicTransportConstants.SingleBidirectionalStreamLimit,
                                MaxInboundUnidirectionalStreams = 0,
                            }),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var quicLocal = _quicListener.LocalEndPoint;
            if (!ConnectivityAcceptBindRules.IsWildcard(quicLocal.Address))
            {
                await _quicListener.DisposeAsync().ConfigureAwait(false);
                _quicListener = null;
                _quicListening = false;
                _quicBindAddress = null;
                _quicStartupDetail = QuicAcceptBindNotice.SpecificAddressDetail;
                RefreshDialEndpoint();
                _quicSettled.TrySetResult();
                return;
            }

            _quicBindAddress = quicLocal.Address;
            _quicListening = true;
            _quicStartupDetail = null;
            RefreshDialEndpoint();
            _quicSettled.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            _quicSettled.TrySetResult();
            return;
        }
        catch (IOException ex)
        {
            _quicListening = false;
            _quicStartupDetail = ex.Message;
            RefreshDialEndpoint();
            _quicSettled.TrySetResult();
            return;
        }

        await QuicAcceptLoopAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1416
    }

    private async Task TcpAcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            if (!await _handshakeSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            QueueTcpHandler(client, cancellationToken);
        }
    }

    private async Task QuicAcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_quicListener is null)
            return;

#pragma warning disable CA1416
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AcceptOneQuicConnectionAsync(
                        cancellationToken,
                        token => _quicListener!.AcceptConnectionAsync(token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
#pragma warning restore CA1416
    }

    private async Task AcceptOneQuicConnectionAsync(
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<QuicConnection>> acceptConnectionAsync)
    {
#pragma warning disable CA1416
        QuicConnection? connection = null;
        var slotOwned = false;
        try
        {
            connection = await acceptConnectionAsync(cancellationToken).ConfigureAwait(false);

            if (!await _handshakeSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return;

            slotOwned = true;
            QueueQuicHandler(connection, cancellationToken);
            slotOwned = false;
            connection = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (IOException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
        }
        catch (AuthenticationException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            if (slotOwned)
                _handshakeSlots.Release();
        }
#pragma warning restore CA1416
    }

    internal int AvailableHandshakeSlots => _handshakeSlots.CurrentCount;

    internal Task AcceptOneQuicConnectionForTestAsync(
        Func<CancellationToken, Task<QuicConnection>> acceptConnectionAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptConnectionAsync);
#pragma warning disable CA1416
        return AcceptOneQuicConnectionAsync(
            cancellationToken,
            token => new ValueTask<QuicConnection>(acceptConnectionAsync(token)));
#pragma warning restore CA1416
    }

    private BytePathEndpoint BuildDialEndpoint(bool? quicListening) =>
        ConnectivityAcceptBindRules.ToDialEndpoint(_bind, Port, quicListening);

    private void RefreshDialEndpoint() =>
        DialEndpoint = BuildDialEndpoint(_quicListening ? true : false);

    private void QueueTcpHandler(TcpClient client, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            _handshakeSlots.Release();
            return;
        }

        var handler = HandleTcpClientSafeAsync(client, cancellationToken);
        TrackHandler(handler);
    }

    private void QueueQuicHandler(QuicConnection connection, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
#pragma warning disable CA1416
            _ = connection.DisposeAsync();
#pragma warning restore CA1416
            _handshakeSlots.Release();
            return;
        }

        var handler = HandleQuicConnectionSafeAsync(connection, cancellationToken);
        TrackHandler(handler);
    }

    private void TrackHandler(Task handler)
    {
        lock (_handlerGate)
            _handlers.Add(handler);
        _ = handler.ContinueWith(
            _ =>
            {
                lock (_handlerGate)
                    _handlers.Remove(handler);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleTcpClientSafeAsync(TcpClient client, CancellationToken shutdownToken)
    {
        AcceptClientSession? session = null;
        using (client)
        {
            try
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                handshakeTimeout.CancelAfter(ConnectivityAcceptLimits.HandshakeTimeout);
                session = await AdmitTcpClientAsync(client, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _handshakeSlots.Release();
            }

            if (session is null)
                return;

            await RunEstablishedSessionSafeAsync(session, shutdownToken).ConfigureAwait(false);
        }
    }

    private async Task HandleQuicConnectionSafeAsync(QuicConnection connection, CancellationToken shutdownToken)
    {
        AcceptClientSession? session = null;
#pragma warning disable CA1416
        await using (connection)
        {
            try
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                handshakeTimeout.CancelAfter(ConnectivityAcceptLimits.HandshakeTimeout);
                var stream = await connection.AcceptInboundStreamAsync(handshakeTimeout.Token)
                    .ConfigureAwait(false);
                session = await _streamHandler.AdmitClientAsync(stream, handshakeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _handshakeSlots.Release();
            }

            if (session is null)
                return;

            await RunEstablishedSessionSafeAsync(session, shutdownToken).ConfigureAwait(false);
        }
#pragma warning restore CA1416
    }

    private async Task RunEstablishedSessionSafeAsync(
        AcceptClientSession session,
        CancellationToken shutdownToken)
    {
        try
        {
            await _streamHandler.RunEstablishedSessionAsync(session, shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _coordinator.CompleteSession(session);
        }
    }

    private async Task WaitHandlersAsync()
    {
        Task[] snapshot;
        lock (_handlerGate)
            snapshot = _handlers.ToArray();

        foreach (var handler in snapshot)
        {
            try
            {
                await handler.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<AcceptClientSession?> AdmitTcpClientAsync(
        TcpClient client,
        CancellationToken handshakeToken)
    {
        client.NoDelay = true;
        Stream stream;
        try
        {
            stream = client.GetStream();
            if (UsesTls && _tlsCertificate is not null)
            {
                var wrapped = await RendezvousTls.WrapServerAsync(stream, _tlsCertificate, handshakeToken)
                    .ConfigureAwait(false);
                if (!wrapped.Ok || wrapped.Value is null)
                    return null;
                stream = wrapped.Value;
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }

        return await _streamHandler.AdmitClientAsync(stream, handshakeToken).ConfigureAwait(false);
    }
}
