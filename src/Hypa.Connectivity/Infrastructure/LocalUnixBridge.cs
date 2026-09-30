using System.Net.Sockets;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Same-uid Unix client on the mux host. Remote clients never receive the path.
/// The other side speaks Connectivity join frames. This type does not listen.
/// </summary>
public sealed class LocalUnixBridge : ILocalUnixBridge, IAsyncDisposable
{
    // Best-effort silence interval. CancelAfter does not end the wait at this
    // mark. The timer and the socket cancellation run on the thread pool.
    // Under thread-pool delay the peek can last longer. This is not a latency bound.
    private const int PeerRejectSilenceMilliseconds = 2;

    private readonly string _unixSocketPath;
    private readonly IRendezvousJoin _join;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _bindAdmission = new(1, 1);
    private Socket? _reservedMux;
    private MuxBridgeReservation? _activeReservation;
    private int _disposed;

    public LocalUnixBridge(string unixSocketPath, IRendezvousJoin join)
    {
        ArgumentException.ThrowIfNullOrEmpty(unixSocketPath);
        ArgumentNullException.ThrowIfNull(join);
        _unixSocketPath = unixSocketPath;
        _join = join;
    }

    public bool ExposesUnixSocketPath => false;

    public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_activeReservation != reservation || _reservedMux is null)
                return null;

            var stream = new NetworkStream(_reservedMux, ownsSocket: true);
            _reservedMux = null;
            _activeReservation = null;
            ReleaseBindAdmission();
            return stream;
        }
    }

    public void ReleaseMuxReservation(MuxBridgeReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_activeReservation != reservation)
                return;

            ReleaseReservedMuxLocked();
            _activeReservation = null;
            ReleaseBindAdmission();
        }
    }

    public async ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var validated = JoinBootstrapRules.Validate(bootstrap);
        if (!validated.Ok || validated.Value is null)
        {
            return ConnectivityOutcome<MuxBridgeBind>.Failure(
                validated.Reason ?? ConnectivityReasons.BootstrapInvalid,
                validated.Detail ?? "bootstrap is required");
        }

        if (validated.Value.Role != JoinRole.Mux)
        {
            return ConnectivityOutcome<MuxBridgeBind>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "local unix bridge role must be mux");
        }

        await _bindAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        MuxBridgeReservation? created = null;
        try
        {
            lock (_gate)
            {
                if (_activeReservation is not null || _reservedMux is not null)
                {
                    return ConnectivityOutcome<MuxBridgeBind>.Failure(
                        ConnectivityReasons.Internal,
                        "local unix bridge is already bound");
                }
            }

            var privateOk = LocalUnixSocketRules.EnsurePrivate(_unixSocketPath);
            if (!privateOk.Ok)
            {
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    privateOk.Reason ?? ConnectivityReasons.PeerUnavailable,
                    privateOk.Detail ?? "unix socket is not private");
            }

            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            }
            catch (SocketException ex)
            {
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    ex.Message);
            }

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_unixSocketPath), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    ex.Message);
            }
            catch (ArgumentException ex)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
            }

            bool rejected;
            try
            {
                rejected = await PeerRejectedAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }

            if (rejected)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "unix peer rejected");
            }

            ConnectivityOutcome<JoinBinding> joined;
            try
            {
                joined = await _join.JoinAsync(validated.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }

            if (!joined.Ok || joined.Value is null)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    joined.Reason ?? ConnectivityReasons.JoinDenied,
                    joined.Detail ?? "join denied");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.JoinDenied,
                    "device is revoked");
            }

            // Silence after this probe means acceptance. The old poll waited up to 150 ms on every bind.
            // A later close still refuses the peer: the client sees a closed connection and gets no access.
            if (RejectedNow(socket))
            {
                socket.Dispose();
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    ConnectivityReasons.Unauthorized,
                    "unix peer rejected");
            }

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _activeReservation is not null || _reservedMux is not null)
                {
                    socket.Dispose();
                    return ConnectivityOutcome<MuxBridgeBind>.Failure(
                        ConnectivityReasons.Internal,
                        "local unix bridge is already bound");
                }

                created = MuxBridgeReservation.Create();
                _activeReservation = created;
                _reservedMux = socket;
            }

            return ConnectivityOutcome<MuxBridgeBind>.Success(new MuxBridgeBind
            {
                Binding = joined.Value,
                Reservation = created,
            });
        }
        finally
        {
            if (created is null)
                ReleaseBindAdmission();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        lock (_gate)
        {
            ReleaseReservedMuxLocked();
            _activeReservation = null;
            ReleaseBindAdmission();
        }

        _bindAdmission.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void ReleaseReservedMuxLocked()
    {
        _reservedMux?.Dispose();
        _reservedMux = null;
    }

    private void ReleaseBindAdmission()
    {
        try
        {
            _bindAdmission.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // An accepted mux writes nothing, so a positive poll always runs out.
    // The accept thread closes a rejected peer after connect returns.
    // A zero wait misses that close. Peek completes on EOF. Silence cancels it.
    // Queued bytes stay unread. Those bytes are not a close.
    private static async ValueTask<bool> PeerRejectedAsync(Socket socket, CancellationToken cancellationToken)
    {
        if (IsPeerClosed(socket, TimeSpan.Zero))
            return true;

        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        silence.CancelAfter(TimeSpan.FromMilliseconds(PeerRejectSilenceMilliseconds));
        var buffer = new byte[1];
        try
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.Peek, silence.Token)
                .ConfigureAwait(false);
            return read == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IsPeerClosed(socket, TimeSpan.Zero);
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.OperationAborted
            && !cancellationToken.IsCancellationRequested)
        {
            return IsPeerClosed(socket, TimeSpan.Zero);
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    // Zero-timeout poll, then a non-blocking peek, before success is stored.
    // A close during JoinAsync maps to "unix peer rejected".
    // A close after BindMuxAsync returns, before the handler writes the join
    // reply, can still be reported as success. There is no mux acceptance signal.
    private static bool RejectedNow(Socket socket)
    {
        if (IsPeerClosed(socket, TimeSpan.Zero))
            return true;

        return NonBlockingPeekReadsEof(socket);
    }

    private static bool NonBlockingPeekReadsEof(Socket socket)
    {
        var blocking = true;
        try
        {
            if (!socket.Connected)
                return true;

            blocking = socket.Blocking;
            socket.Blocking = false;
            var buffer = new byte[1];
            var read = socket.Receive(buffer, 0, 1, SocketFlags.Peek, out var error);
            if (error is SocketError.WouldBlock or SocketError.TryAgain)
                return false;
            if (error != SocketError.Success)
                return true;

            return read == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        finally
        {
            try
            {
                socket.Blocking = blocking;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private static bool IsPeerClosed(Socket socket, TimeSpan wait)
    {
        try
        {
            if (!socket.Connected)
                return true;

            var readable = socket.Poll(wait, SelectMode.SelectRead);
            return readable && socket.Available == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
