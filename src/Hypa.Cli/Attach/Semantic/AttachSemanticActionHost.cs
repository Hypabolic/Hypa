using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace Hypa.Cli.Attach;

/// <summary>
/// One NDJSON action socket for this attach process.
/// Cancel the host token before <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
/// so dispose does not take the control gate.
/// </summary>
internal sealed class AttachSemanticActionHost : IAsyncDisposable
{
    private readonly string _sessionDir;
    private readonly AttachLiveState _live;
    private readonly SemaphoreSlim _controlGate;
    private readonly Func<AttachSemanticRequest, CancellationToken, Task<AttachSemanticReply>> _apply;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<Task, byte> _handles = new();
    private readonly string _socketPath;
    private readonly string _clientId;
    private Socket? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    private AttachSemanticActionHost(
        string sessionDir,
        AttachLiveState live,
        SemaphoreSlim controlGate,
        Func<AttachSemanticRequest, CancellationToken, Task<AttachSemanticReply>> apply,
        CancellationToken ct,
        string socketPath,
        string clientId)
    {
        _sessionDir = sessionDir;
        _live = live;
        _controlGate = controlGate;
        _apply = apply;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socketPath = socketPath;
        _clientId = clientId;
    }

    public static AttachSemanticActionHost Start(
        string sessionDir,
        AttachLiveState live,
        SemaphoreSlim controlGate,
        Func<AttachSemanticRequest, CancellationToken, Task<AttachSemanticReply>> apply,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(apply);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("attach actions use a Unix socket");

        var clientId = AttachSession.EnsureAttachClientId(live);
        Directory.CreateDirectory(sessionDir);
        var socketPath = AttachClientDirectory.SocketPath(sessionDir, Environment.ProcessId);
        AttachSemanticIo.EnsureSocketPathFits(socketPath);
        AttachSemanticIo.ClearStaleSocket(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
            TryRestrictSocket(socketPath);
            var (endpoint, generation) = AttachSemanticIdentity.Read(live);
            var host = new AttachSemanticActionHost(
                sessionDir,
                live,
                controlGate,
                apply,
                ct,
                socketPath,
                clientId);
            host._listener = listener;
            AttachClientDirectory.Register(
                sessionDir,
                new AttachClientRecord
                {
                    AttachClientId = clientId,
                    Socket = socketPath,
                    EndpointId = endpoint,
                    ConnectionGeneration = generation,
                    Pid = Environment.ProcessId,
                });
            host._acceptLoop = host.AcceptLoopAsync();
            return host;
        }
        catch
        {
            listener.Dispose();
            TryDelete(socketPath);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        var pending = _handles.Keys.ToList();
        if (_acceptLoop is not null)
            pending.Add(_acceptLoop);
        if (pending.Count > 0)
        {
            var all = Task.WhenAll(pending);
            _ = all.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            try
            {
                await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        TryDelete(_socketPath);
        try
        {
            AttachClientDirectory.Unregister(_sessionDir, _clientId);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            var handle = HandleConnectionAsync(socket);
            _handles.TryAdd(handle, 0);
            _ = handle.ContinueWith(
                task => _handles.TryRemove(task, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        var ct = _cts.Token;
        try
        {
            var line = await AttachSemanticIo.ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (line is null)
                return;

            AttachSemanticRequest request;
            try
            {
                request = JsonSerializer.Deserialize(line, AttachSemanticJsonContext.Default.AttachSemanticRequest)
                    ?? new AttachSemanticRequest();
            }
            catch (JsonException)
            {
                await WriteAsync(
                        stream,
                        new AttachSemanticReply
                        {
                            Action = "",
                            Source = AttachSemanticSources.Cli,
                            Outcome = AttachSemanticOutcomes.Rejected,
                            Error = "action request is not json",
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var reply = await ApplyUnderGateAsync(request, ct).ConfigureAwait(false);
            if (reply is null)
                return;

            NoteClient(reply);
            await WriteAsync(stream, reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task<AttachSemanticReply?> ApplyUnderGateAsync(
        AttachSemanticRequest request,
        CancellationToken ct)
    {
        var held = false;
        try
        {
            await _controlGate.WaitAsync(ct).ConfigureAwait(false);
            held = true;
            return await _apply(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return new AttachSemanticReply
            {
                RequestId = request.RequestId,
                Action = request.Action,
                Source = AttachSemanticSources.Cli,
                AttachClientId = _live.AttachClientId,
                Outcome = AttachSemanticOutcomes.Rejected,
                Error = ex.Message,
            };
        }
        finally
        {
            if (held)
                _controlGate.Release();
        }
    }

    private void NoteClient(AttachSemanticReply reply)
    {
        if (string.IsNullOrWhiteSpace(_live.AttachClientId))
            return;

        try
        {
            AttachClientDirectory.Register(
                _sessionDir,
                new AttachClientRecord
                {
                    AttachClientId = _live.AttachClientId,
                    Socket = _socketPath,
                    EndpointId = reply.EndpointId,
                    ConnectionGeneration = reply.ConnectionGeneration ?? 0,
                    Pid = Environment.ProcessId,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Task WriteAsync(Stream stream, AttachSemanticReply reply, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(reply, AttachSemanticJsonContext.Default.AttachSemanticReply);
        return AttachSemanticIo.WriteLineAsync(stream, json, ct);
    }

    private static void TryRestrictSocket(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
