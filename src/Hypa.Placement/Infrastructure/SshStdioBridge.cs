using System.Diagnostics;
using System.Net.Sockets;
using Hypa.ControlPlane.Unix;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Local listener that bridges accepted connections over SSH stdio.
/// Each connection starts one ssh child. No copy polls. The upload uses one
/// dedicated thread. The download and the connection use no thread while
/// they wait.
/// </summary>
public sealed class SshStdioBridge : ISshStdioBridge
{
    /// <summary>
    /// After the client ends its input, the child can still write its last
    /// output. The bridge waits this long for the child output to end.
    /// </summary>
    internal static readonly TimeSpan DefaultDownloadGrace = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan DisposeLimit = TimeSpan.FromSeconds(2);
    private const int CopyBufferBytes = 16 * 1024;

    private readonly string _localSocketPath;
    private readonly UnixSocketFileIdentity? _socketIdentity;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly SshStdioBridgeStartRequest _request;
    private readonly Func<SshStdioBridgeStartRequest, ProcessStartInfo> _childStart;
    private readonly TimeProvider _time;
    private readonly TimeSpan _downloadGrace;
    private readonly object _connectionsGate = new();
    private readonly HashSet<Task> _connections = [];
    private int _disposed;

    private SshStdioBridge(
        SshStdioBridgeStartRequest request,
        Socket listener,
        UnixSocketFileIdentity? socketIdentity,
        Func<SshStdioBridgeStartRequest, ProcessStartInfo> childStart,
        TimeProvider time,
        TimeSpan downloadGrace)
    {
        _request = request;
        _localSocketPath = request.LocalSocketPath;
        _listener = listener;
        _socketIdentity = socketIdentity;
        _childStart = childStart;
        _time = time;
        _downloadGrace = downloadGrace;
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public string LocalSocketPath => _localSocketPath;

    public static SshStdioBridge Start(SshStdioBridgeStartRequest request) =>
        Start(request, SshChildStart);

    /// <summary>
    /// Starts the bridge with a given child in place of ssh. Tests use this
    /// seam to run a stand-in child.
    /// </summary>
    internal static SshStdioBridge Start(
        SshStdioBridgeStartRequest request,
        Func<SshStdioBridgeStartRequest, ProcessStartInfo> childStart,
        TimeProvider? time = null,
        TimeSpan? downloadGrace = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(childStart);
        var (listener, identity) = UnixBridgeListener.Bind(request.LocalSocketPath, request.FreshDirectory);
        return new SshStdioBridge(
            request,
            listener,
            identity,
            childStart,
            time ?? TimeProvider.System,
            downloadGrace ?? DefaultDownloadGrace);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The cancel stops the accept loop and each connection. A connection
        // kills its child, and that ends the child output.
        await _stop.CancelAsync().ConfigureAwait(false);
        var started = _time.GetTimestamp();
        try
        {
            // The accept loop ends first, so no connection starts after the snapshot.
            await QuietlyWithinAsync(_acceptLoop, DisposeLimit, _time).ConfigureAwait(false);
            Task[] pending;
            lock (_connectionsGate)
                pending = [.. _connections];
            var left = DisposeLimit - _time.GetElapsedTime(started);
            if (left > TimeSpan.Zero)
                await QuietlyWithinAsync(Task.WhenAll(pending), left, _time).ConfigureAwait(false);
        }
        finally
        {
            UnixBridgeListener.Stop(_localSocketPath, _socketIdentity, _listener);
        }

        // A connection that is still running after the limit keeps its own
        // token source. This source is not disposed, so its token stays valid.
        GC.SuppressFinalize(this);
    }

    private static async Task QuietlyWithinAsync(Task task, TimeSpan limit, TimeProvider time)
    {
        try
        {
            await task.WaitAsync(limit, time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispose does not throw. A connection fault was already handled
            // by that connection, and a timeout leaves the rest to the kill.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var authenticator = new EuidPeerAuthenticator();
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // A failed accept (for example, the client went away) does
                // not stop the bridge.
                continue;
            }

            TrackConnection(BridgeConnectionAsync(client, authenticator, ct));
        }
    }

    private void TrackConnection(Task connection)
    {
        lock (_connectionsGate)
            _connections.Add(connection);
        _ = connection.ContinueWith(
            static (done, state) =>
            {
                var bridge = (SshStdioBridge)state!;
                lock (bridge._connectionsGate)
                    bridge._connections.Remove(done);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task BridgeConnectionAsync(
        Socket client,
        EuidPeerAuthenticator authenticator,
        CancellationToken bridgeStop)
    {
        // Leave the accept loop at once. The authentication is a system call.
        await Task.Yield();
        using (client)
        {
            try
            {
                authenticator.Authenticate(client);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            if (bridgeStop.IsCancellationRequested)
                return;
            using var process = StartChild(_childStart(_request));
            if (process is null)
                return;

            try
            {
                await RunCopiesAsync(client, process, bridgeStop).ConfigureAwait(false);
            }
            finally
            {
                Stop(process);
            }
        }
    }

    /// <summary>
    /// Runs the two copies of one connection.
    /// When the client input ends first, the child stdin closes, and the
    /// child can write its last output in the grace time. Then the child stops.
    /// When the child output ends first, the upload stops.
    /// </summary>
    private async Task RunCopiesAsync(Socket client, Process process, CancellationToken bridgeStop)
    {
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(bridgeStop);
        var stdin = process.StandardInput.BaseStream;
        var stdout = process.StandardOutput.BaseStream;
        // ssh writes diagnostics to stderr. A full stderr pipe would stop ssh.
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

        var upload = UploadThenCloseAsync(client, stdin, connectionStop.Token);
        var download = CopyDownloadAsync(stdout, client, connectionStop.Token);
        var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
        if (first == upload && !bridgeStop.IsCancellationRequested)
        {
            try
            {
                await download.WaitAsync(_downloadGrace, _time, bridgeStop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
            }
        }

        await connectionStop.CancelAsync().ConfigureAwait(false);
        Stop(process);
        try
        {
            client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        await QuietlyAsync(upload).ConfigureAwait(false);
        await QuietlyAsync(download).ConfigureAwait(false);
        await QuietlyAsync(stderr).ConfigureAwait(false);
    }

    private static async Task UploadThenCloseAsync(Socket client, Stream stdin, CancellationToken ct)
    {
        try
        {
            await CopyUploadAsync(client, stdin, ct).ConfigureAwait(false);
        }
        finally
        {
            // The end of the client input is the end of the child input.
            try
            {
                await stdin.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Copies the client bytes to the child stdin. The copy runs on its own
    /// thread, which waits in a blocking receive until bytes arrive. Thus a
    /// keystroke goes on at once, with no poll and no thread-pool hop.
    /// The copy ends when the client ends its input, or on cancel. A cancel
    /// shuts down the receive side of the socket, which releases the receive.
    /// </summary>
    internal static Task CopyUploadAsync(Socket source, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return Task.Factory.StartNew(
            () => CopyUpload(source, destination, ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void CopyUpload(Socket source, Stream destination, CancellationToken ct)
    {
        using var release = ct.Register(
            static state =>
            {
                try
                {
                    ((Socket)state!).Shutdown(SocketShutdown.Receive);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            },
            source);
        var buffer = new byte[CopyBufferBytes];
        try
        {
            source.Blocking = true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = source.Receive(buffer, SocketFlags.None);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            if (read == 0 || ct.IsCancellationRequested)
                return;
            try
            {
                destination.Write(buffer, 0, read);
                destination.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Copies the child output to the client. A send to a full client socket
    /// waits for room. It does not fail and does not drop bytes.
    /// The copy ends when the child output ends, or on cancel.
    /// </summary>
    internal static async Task CopyDownloadAsync(Stream source, Socket destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = new byte[CopyBufferBytes];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
                return;
            var offset = 0;
            while (offset < read)
            {
                int sent;
                try
                {
                    sent = await destination
                        .SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }

                if (sent <= 0)
                    return;
                offset += sent;
            }
        }
    }

    private static ProcessStartInfo SshChildStart(SshStdioBridgeStartRequest request)
    {
        var psi = new ProcessStartInfo { FileName = "ssh" };
        foreach (var arg in OpenSshArgumentBuilder.Build(new OpenSshProcessRequest
        {
            Target = request.Target,
            RemoteCommand = RemoteMuxCommands.RemoteBridgeCommand(request.Session),
            ConfigPath = request.ConfigPath,
            ControlPath = request.ControlPath,
            BatchMode = request.BatchMode,
        }))
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static Process? StartChild(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        try
        {
            return Process.Start(psi);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task QuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or SocketException
            or ObjectDisposedException)
        {
        }
    }
}
