using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Envelopes;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane.Unix;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.ControlPlane;

/// <summary>
// / NDJSON over Unix domain socket.
/// ~/.config/hypa/runtime/&lt;session&gt;/hypa.sock (or HYPA_RUNTIME_SOCKET).
/// Per-connection writer gate serializes RPC responses and runtime.event pushes.
/// </summary>
public sealed class UnixSocketServer : IAsyncDisposable
{
    private readonly IControlPlaneService _service;
    private readonly string _socketPath;
    private readonly UnixSocketServerOptions _options;
    private readonly ILogger _logger;
    private readonly IProcessLogSink _processLog;
    private readonly IEventPayloadRedactor _redactor;
    private readonly string? _sessionId;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _disposed;
    private int _connectionCounter;
    private int _liveConnections;
    private int _acceptedSockets;
    private int _peakInFlight;
    private int _peakAcceptedSockets;
    private readonly object _capacityLock = new();
    private TaskCompletionSource? _capacityWaiters;
    private bool _boundPath;
    private UnixSocketFileIdentity? _ownedIdentity;

    public UnixSocketServer(IControlPlaneService service, string socketPath, ILogger? logger = null)
        : this(service, socketPath, UnixSocketServerOptions.Default, logger)
    {
    }

    public UnixSocketServer(
        IControlPlaneService service,
        string socketPath,
        UnixSocketServerOptions options,
        ILogger? logger = null,
        IProcessLogSink? processLog = null,
        string? sessionId = null,
        IEventPayloadRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxLineBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxLineBytes must be at least 1.");
        if (options.MaxConcurrentConnections < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "MaxConcurrentConnections must be at least 1.");
        }

        if (options.ListenBacklog < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ListenBacklog must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(options.PeerAuthenticator);
        ArgumentNullException.ThrowIfNull(options.ModeGuard);

        _service = service;
        _socketPath = socketPath;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _processLog = processLog ?? NullProcessLogSink.Instance;
        _sessionId = sessionId;
        _redactor = redactor ?? new Hypa.AgentIntelligence.DefaultEventPayloadRedactor();
    }

    public string SocketPath => _socketPath;

    internal int LiveConnectionCount => Volatile.Read(ref _liveConnections);

    internal int InFlightCount => _inFlight.Count;

    internal int PeakInFlightCount => Volatile.Read(ref _peakInFlight);

    internal int AcceptedSocketCount => Volatile.Read(ref _acceptedSockets);

    internal int PeakAcceptedSocketCount => Volatile.Read(ref _peakAcceptedSockets);

    internal int ListenBacklog => _options.ListenBacklog;

    internal bool AcceptIsPaused
    {
        get
        {
            lock (_capacityLock)
                return _capacityWaiters is { Task.IsCompleted: false };
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_socketPath);
        if (string.IsNullOrEmpty(dir))
        {
            throw new InvalidOperationException(
                $"Socket path '{_socketPath}' has no parent directory. " +
                "The socket must live in a private directory.");
        }

        _options.ModeGuard.EnsurePrivateDirectory(dir);
        PrepareSocketPath(_socketPath, _logger);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        UnixSocketFileIdentity? identity = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _boundPath = true;
            _options.ModeGuard.HardenSocket(_socketPath);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (!UnixSocketPathIdentity.TryRead(_socketPath, out var captured))
                {
                    throw new InvalidOperationException(
                        $"Could not read socket identity at '{_socketPath}' after bind.");
                }

                identity = captured;
            }

            listener.Listen(_options.ListenBacklog);
        }
        catch
        {
            try { listener.Dispose(); }
            catch
            {
                // ignore dispose of a failed listener
            }

            if (identity is { } owned)
                UnixSocketPathIdentity.RemoveIfOwned(_socketPath, owned);
            throw;
        }

        _ownedIdentity = identity;
        _listener = listener;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Control plane listening on {Socket}", _socketPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove a stale socket only after proving no live listener owns it.
    /// Refuse to delete regular files or directories at the path.
    /// </summary>
    internal static void PrepareSocketPath(string socketPath, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        if (!Path.Exists(socketPath))
            return;

        if (Directory.Exists(socketPath))
        {
            throw new InvalidOperationException(
                $"Socket path '{socketPath}' is a directory. Choose a different path.");
        }

        var name = Path.GetFileName(socketPath);
        var looksLikeSocket = name.EndsWith(".sock", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeSocket)
        {
            throw new InvalidOperationException(
                $"Refusing to replace non-socket file at '{socketPath}'. " +
                "Set HYPA_RUNTIME_SOCKET to a path ending in .sock, or remove the file manually.");
        }

        // Probe for a live listener. Connect success ⇒ another server owns the path.
        // Timeout or unknown error ⇒ fail closed (do not unlink a live peer).
        // ECONNREFUSED / not a socket ⇒ delete the stale inode.
        const int ProbeTimeoutMs = 2000;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var ep = new UnixDomainSocketEndPoint(socketPath);
            var ar = probe.BeginConnect(ep, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(ProbeTimeoutMs)))
            {
                try { probe.Close(); } catch { /* ignore */ }
                throw new InvalidOperationException(
                    $"Socket probe timed out on '{socketPath}'. " +
                    "A live peer may own the path. Remove it manually if the peer is wedged.");
            }

            try
            {
                probe.EndConnect(ar);
                throw new InvalidOperationException(
                    $"Another hypa-runtime is already listening on '{socketPath}'.");
            }
            catch (SocketException ex) when (IsRefusedOrNotSocket(ex))
            {
                // Connection refused / reset — treat as stale socket.
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (SocketException ex) when (IsRefusedOrNotSocket(ex))
        {
            // Not connectable — stale candidate.
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Socket probe failed on '{socketPath}' ({ex.SocketErrorCode}). " +
                "A live peer may own the path. Remove it manually if the peer is wedged.",
                ex);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"Socket path '{socketPath}' is not a usable Unix socket. " +
                "Remove it manually if the inode is wedged.");
        }

        try
        {
            File.Delete(socketPath);
            logger.LogInformation("Removed stale socket at {Socket}", socketPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not remove stale socket at '{socketPath}': {ex.Message}", ex);
        }
    }

    private static bool IsRefusedOrNotSocket(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionRefused
            or SocketError.ConnectionReset;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
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
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    break;
                _logger.LogWarning(ex, "Accept failed");
                continue;
            }

            NoteAcceptedSocket();

            try
            {
                _options.PeerAuthenticator.Authenticate(client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rejected AF_UNIX peer (uid-private socket)");
                CloseAccepted(client);
                continue;
            }

            // Reserve on this thread. Do not Task.Run an unbounded handler set.
            if (!TryReserveConnectionSlot())
            {
                await TryWriteConnectionRejectedAsync(client, ct).ConfigureAwait(false);
                CloseAccepted(client);
                await WaitForConnectionSlotAsync(ct).ConfigureAwait(false);
                continue;
            }

            Task handle;
            try
            {
                handle = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to queue accepted connection");
                ReleaseConnectionSlot();
                CloseAccepted(client);
                continue;
            }

            TrackInFlight(handle);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            var pluginPeer = _service is ControlPlaneService live
                && UnixPeerCredentials.TryGetPeerPid(client, out var peerPid)
                && live.IsLivePluginPeer(peerPid);
            var connectionId = "conn_" + Interlocked.Increment(ref _connectionCounter);
            using var stream = new NetworkStream(client, ownsSocket: false);
            var reader = new BoundedNdjsonLineReader(stream, _options.MaxLineBytes);
            await using var connection = new ClientConnection(
                connectionId, stream, _options.MaxLineBytes);
            if (pluginPeer)
                connection.MarkPluginPeer();
            if (_service is ControlPlaneService controlPlane)
            {
                connection.AllowsUnboundAttachRender = () =>
                    controlPlane.AttachSurfaceInterest.GetSnapshot(connection.ConnectionId) is null;
                connection.CanWriteRenderItem = binding =>
                    binding is null
                        ? connection.AllowsUnboundAttachRender()
                        : controlPlane.RecheckAttachSurfaceEmit(binding);
                connection.RenderItemCanceled = controlPlane.NoteAttachRenderCanceled;
                connection.TryAdmitAttachRender = (surfaceRevision, snapshotRevision) =>
                    controlPlane.TryAdmitAttachSurfaceRender(
                        connection.ConnectionId,
                        surfaceRevision,
                        snapshotRevision,
                        out var binding)
                        ? binding
                        : null;
            }

            connection.WriterDrained = () =>
            {
                try { _service.OnClientWriterDrained(connection); }
                catch { /* retry must not fault the writer */ }
            };
            var writerTask = connection.RunWriterAsync(ct);
            var lanes = new ConnectionRequestLanes(
                (request, queuedAt, laneCt) => RunRequestAsync(connection, request, queuedAt, laneCt),
                (request, queuedAt, laneCt) => RunKeyAsync(connection, request, queuedAt, laneCt),
                ct,
                serialOnly: pluginPeer);
            connection.RequestLanes = lanes;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    BoundedNdjsonReadResult read;
                    try
                    {
                        read = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (read.IsEndOfStream)
                        break;

                    if (read.IsOverflow)
                    {
                        await WriteErrorAsync(
                                connection,
                                id: null,
                                ProtocolErrorCodes.ParseError,
                                "NDJSON line exceeds max size",
                                ct)
                            .ConfigureAwait(false);
                        break;
                    }

                    var line = read.Line;
                    if (line is null || line.Length == 0)
                        continue;

                    RpcRequest? request;
                    try
                    {
                        request = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RpcRequest);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Bad JSON from client");
                        await WriteErrorAsync(connection, id: null, -32700, "Parse error", ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (request is null || string.IsNullOrWhiteSpace(request.Method))
                    {
                        await WriteErrorAsync(connection, request?.Id, -32600, "Invalid request", ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // Render credit comes back here, not on the serial
                    // dispatch queue: a slow request on this connection must
                    // not hold back the next frame. The ack writes no log line.
                    if (string.IsNullOrEmpty(request.Id)
                        && string.Equals(request.Method, ProtocolMethods.RenderAck, StringComparison.Ordinal)
                        && _service is ControlPlaneService credit)
                    {
                        credit.ApplyRenderAck(connection, request.Params);
                        continue;
                    }

                    await lanes.PostAsync(request, System.Diagnostics.Stopwatch.GetTimestamp(), ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                lanes.Complete();
                try { await lanes.DispatchCompletion.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (IOException) { /* peer gone */ }
                try { await lanes.InputCompletion.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (IOException) { /* peer gone */ }
                connection.CompleteWriter();
                try { await writerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (IOException) { /* peer gone */ }
                var loss = ct.IsCancellationRequested
                    ? AttachClientLoss.Disconnect
                    : AttachClientLoss.Stall;
                _service.OnClientDisconnected(connection, loss);
                _logger.LogInformation(
                    "Client {ConnectionId} closed. read_buffer_bytes={ReadBuffer} reliable_high_water_bytes={HighWater} reliable_limit_bytes={ReliableLimit}",
                    connection.ConnectionId,
                    reader.BufferCapacity,
                    connection.Lanes.ReliableBytesHighWater,
                    connection.Lanes.MaxReliableBytes);
            }
        }
        finally
        {
            ReleaseConnectionSlot();
            CloseAccepted(client);
        }
    }

    private Task<JsonElement> DispatchGrantedAsync(
        RpcRequest request,
        IClientConnection connection,
        CancellationToken ct)
    {
        if (_service is ControlPlaneService live)
        {
            var pluginConnection = false;
            if (connection is ClientConnection client)
            {
                client.NotePluginGrantToken(request.GrantToken);
                pluginConnection = client.IsPluginAttributed;
            }

            return live.DispatchAsync(
                request.Method,
                request.Params,
                connection,
                request.GrantToken,
                pluginConnection,
                ct);
        }

        return _service.DispatchAsync(request.Method, request.Params, connection, ct);
    }

    /// <summary>
    /// Runs one request on the serial dispatch lane of a connection. Returns
    /// false when the lane must stop: the client closed the connection before
    /// a reply, or the server is shutting down.
    /// </summary>
    private async Task<bool> RunRequestAsync(
        ClientConnection connection,
        RpcRequest request,
        long queuedAt,
        CancellationToken ct)
    {
        // A key on the dispatch lane (with an id or a grant token) keeps the
        // queue stage of the input lane.
        if (string.Equals(request.Method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal))
            RecordDispatchQueue(queuedAt);

        connection.ActiveRequestId = request.Id;
        connection.LastEmittedSeq = null;
        ProcessLogApi.WriteStart(
            _processLog,
            request.Id,
            request.Method,
            connection.ConnectionId,
            _sessionId);
        try
        {
            JsonElement result;
            try
            {
                result = await DispatchGrantedAsync(request, connection, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                // The change of the handler is applied. A key that waits for
                // this request does not also wait for the reply write, which
                // waits behind render lines on the writer.
                if (!CompletesAfterReply(request.Method))
                    connection.RequestLanes?.MarkActiveRequestApplied();
            }

            // JSON-RPC 2.0: a notification omits id and must not receive a reply.
            if (string.IsNullOrEmpty(request.Id))
            {
                WriteRequestComplete(connection, request, ProcessLogEvents.OutcomeOk, connection.LastEmittedSeq);
                return true;
            }

            if (string.Equals(
                    request.Method, ProtocolMethods.EventsSubscribe, StringComparison.Ordinal) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("subscription_id", out var subEl) &&
                subEl.GetString() is { } subId)
            {
                var prepared = await _service.PrepareEventsSubscribeAsync(subId, ct)
                    .ConfigureAwait(false);
                if (!prepared.IsOk)
                {
                    WriteRequestFail(connection, request, prepared.Error.Message);
                    _logger.LogWarning(
                        "Subscribe replay failed for {SubId}: {Error}",
                        subId,
                        prepared.Error.Message);
                    // The floor can rise between the subscribe check and this replay.
                    // Keep cursor_expired so the client resubscribes at the live edge.
                    if (prepared.Error.Code == RuntimePersistenceError.CursorExpiredCode)
                    {
                        await WriteErrorAsync(
                                connection,
                                request.Id,
                                ProtocolErrorCodes.InvalidState,
                                prepared.Error.Message,
                                ct,
                                ProtocolErrors.CursorExpired,
                                prepared.Error.FloorSeq)
                            .ConfigureAwait(false);
                        return true;
                    }

                    await WriteErrorAsync(
                            connection,
                            request.Id,
                            ProtocolErrorCodes.PersistenceUnavailable,
                            ProtocolErrors.MeaningOf(
                                ProtocolErrorCodes.PersistenceUnavailable),
                            ct)
                        .ConfigureAwait(false);
                    return true;
                }

                if (!await TryWriteReplyAsync(connection, request, result, queuedAt, ct)
                        .ConfigureAwait(false))
                {
                    return false;
                }

                WriteRequestComplete(connection, request, ProcessLogEvents.OutcomeOk, connection.LastEmittedSeq);

                var replayRecords = prepared.Value;
                var replayCt = ct;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _service.CompleteEventsSubscribeAsync(
                                subId, replayRecords, connection, replayCt)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (replayCt.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex, "Subscribe replay/live failed for {SubId}", subId);
                    }
                }, CancellationToken.None);

                return true;
            }

            if (!await TryWriteReplyAsync(connection, request, result, queuedAt, ct)
                    .ConfigureAwait(false))
            {
                return false;
            }

            WriteRequestComplete(connection, request, ProcessLogEvents.OutcomeOk, connection.LastEmittedSeq);

            if (string.Equals(
                    request.Method, ProtocolMethods.ServerStop, StringComparison.Ordinal))
            {
                await _service.CompleteServerStopAsync(ct).ConfigureAwait(false);
            }

            if (string.Equals(
                    request.Method, ProtocolMethods.ServerLiveHandoff, StringComparison.Ordinal))
            {
                await _service.CompleteLiveHandoffAsync(ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (ControlPlaneException cpe)
        {
            WriteRequestFail(connection, request, cpe.Message);
            if (string.IsNullOrEmpty(request.Id))
            {
                await TryEmitNotificationFaultAsync(
                        request, cpe.Code, cpe.Message, connection, ct)
                    .ConfigureAwait(false);
                return true;
            }

            await WriteErrorAsync(
                    connection, request.Id, cpe.Code, cpe.Message, ct, cpe.ErrorCode, cpe.FloorSeq)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            WriteRequestFault(connection, request, ex);
            _logger.LogError(ex, "Handler failed for {Method}", request.Method);
            if (!string.IsNullOrEmpty(request.Id))
            {
                await WriteErrorAsync(
                        connection,
                        request.Id,
                        ProtocolErrorCodes.InternalError,
                        ProtocolErrors.MeaningOf(ProtocolErrorCodes.InternalError),
                        ct)
                    .ConfigureAwait(false);
                return true;
            }

            await TryEmitNotificationFaultAsync(
                    request,
                    ProtocolErrorCodes.InternalError,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.InternalError),
                    connection,
                    ct)
                .ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Runs one <c>pane.send_keys</c> notification on the input lane of a
    /// connection. A key gets no reply. A rejected key gets
    /// <c>pane.input_rejected</c>. This lane does not touch the request state
    /// of the dispatch lane (<c>ActiveRequestId</c>, <c>LastEmittedSeq</c>).
    /// </summary>
    private async Task RunKeyAsync(
        ClientConnection connection,
        RpcRequest request,
        long queuedAt,
        CancellationToken ct)
    {
        RecordDispatchQueue(queuedAt);
        ProcessLogApi.WriteStart(
            _processLog,
            request.Id,
            request.Method,
            connection.ConnectionId,
            _sessionId);
        try
        {
            await DispatchGrantedAsync(request, connection, ct).ConfigureAwait(false);
            WriteRequestComplete(connection, request, ProcessLogEvents.OutcomeOk, emittedSeq: null);
        }
        catch (ControlPlaneException cpe)
        {
            WriteRequestFail(connection, request, cpe.Message);
            await TryEmitNotificationFaultAsync(request, cpe.Code, cpe.Message, connection, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteRequestFault(connection, request, ex);
            _logger.LogError(ex, "Handler failed for {Method}", request.Method);
            await TryEmitNotificationFaultAsync(
                    request,
                    ProtocolErrorCodes.InternalError,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.InternalError),
                    connection,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The time from the read of a key to the start of its handler, with each
    /// fence wait. This is the <c>dispatch-queue</c> stage of the input path.
    /// </summary>
    private static void RecordDispatchQueue(long queuedAt) =>
        AttachPathTrace.RecordHop(
            AttachPathTrace.DomainInput,
            AttachPathTrace.StageDispatchQueue,
            paneId: null,
            System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt);

    /// <summary>
    /// A request whose change completes only after its reply: the stop and
    /// the live handoff. A key that it fences waits for the full lane step.
    /// </summary>
    private static bool CompletesAfterReply(string method) =>
        string.Equals(method, ProtocolMethods.ServerStop, StringComparison.Ordinal)
        || string.Equals(method, ProtocolMethods.ServerLiveHandoff, StringComparison.Ordinal);

    /// <summary>
    /// Write the reply of a handler that succeeded. Returns false when the client
    /// closed the connection first. That is not a handler fault: log it as peer gone,
    /// with the time from when the server read the request. The connection cannot
    /// carry a later reply, so the caller stops the dispatch loop.
    /// A fault that is not a closed peer propagates as before.
    /// </summary>
    private async Task<bool> TryWriteReplyAsync(
        ClientConnection connection,
        RpcRequest request,
        JsonElement result,
        long queuedAt,
        CancellationToken ct)
    {
        try
        {
            await WriteResultLineAsync(connection, request.Id, request.Method, result, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsPeerGoneWriteFault(ex, ct))
        {
            var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            ProcessLogApi.WritePeerGone(
                _processLog,
                request.Id,
                request.Method,
                elapsedMs,
                connection.ConnectionId,
                _sessionId);
            _logger.LogWarning(
                "Peer gone before the reply for {Method} after {ElapsedMs} ms on {ConnectionId}",
                request.Method,
                elapsedMs,
                connection.ConnectionId);
            return false;
        }
    }

    /// <summary>
    /// A reply write fault that means the client closed the connection: a broken pipe,
    /// a closed writer lane, a disposed stream, or a write that the closed connection
    /// cancelled while the server token is still live.
    /// </summary>
    private static bool IsPeerGoneWriteFault(Exception ex, CancellationToken ct) =>
        ex is IOException or SocketException or ObjectDisposedException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private void WriteRequestComplete(
        ClientConnection connection,
        RpcRequest request,
        string outcome,
        long? emittedSeq)
    {
        ProcessLogApi.WriteComplete(
            _processLog,
            request.Id,
            request.Method,
            outcome,
            connection.ConnectionId,
            _sessionId,
            emittedSeq);
    }

    private void WriteRequestFail(ClientConnection connection, RpcRequest request, string err)
    {
        ProcessLogApi.WriteFail(
            _processLog,
            request.Id,
            request.Method,
            ProcessLogApi.RedactFailErr(_redactor, err),
            connection.ConnectionId,
            _sessionId);
    }

    /// <summary>
    /// An unexpected handler exception. The log record gets the exception type
    /// and the redacted message. The reply to the client stays the generic
    /// internal error.
    /// </summary>
    private void WriteRequestFault(ClientConnection connection, RpcRequest request, Exception ex)
    {
        ProcessLogApi.WriteFail(
            _processLog,
            request.Id,
            request.Method,
            ProcessLogApi.HandlerFaultErr(_redactor, ex),
            connection.ConnectionId,
            _sessionId);
    }

    private async Task TryEmitNotificationFaultAsync(
        RpcRequest request,
        int code,
        string message,
        IClientConnection connection,
        CancellationToken ct)
    {
        if (!string.Equals(request.Method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Notification {Method} failed with {Code}",
                request.Method,
                code);
            return;
        }

        try
        {
            await _service.EmitNotificationFaultAsync(
                    request.Method, request.Params, code, message, connection, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit pane.input_rejected");
        }
    }

    private bool TryReserveConnectionSlot()
    {
        var max = _options.MaxConcurrentConnections;
        while (true)
        {
            var current = Volatile.Read(ref _liveConnections);
            if (current >= max)
                return false;
            if (Interlocked.CompareExchange(ref _liveConnections, current + 1, current) == current)
                return true;
        }
    }

    private void ReleaseConnectionSlot()
    {
        Interlocked.Decrement(ref _liveConnections);
        SignalConnectionSlotAvailable();
    }

    private void TrackInFlight(Task handle)
    {
        _inFlight.TryAdd(handle, 0);
        RaisePeak(ref _peakInFlight, _inFlight.Count);
        _ = handle.ContinueWith(
            t => _inFlight.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void NoteAcceptedSocket()
    {
        var n = Interlocked.Increment(ref _acceptedSockets);
        RaisePeak(ref _peakAcceptedSockets, n);
    }

    private void CloseAccepted(Socket client)
    {
        Interlocked.Decrement(ref _acceptedSockets);
        try { client.Dispose(); }
        catch
        {
            // ignore client dispose
        }
    }

    private async Task WaitForConnectionSlotAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _liveConnections) >= _options.MaxConcurrentConnections)
        {
            Task wait;
            lock (_capacityLock)
            {
                if (Volatile.Read(ref _liveConnections) < _options.MaxConcurrentConnections)
                    return;
                _capacityWaiters ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _capacityWaiters.Task;
            }

            try
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SignalConnectionSlotAvailable()
    {
        TaskCompletionSource? waiters;
        lock (_capacityLock)
        {
            if (Volatile.Read(ref _liveConnections) >= _options.MaxConcurrentConnections)
                return;
            waiters = _capacityWaiters;
            _capacityWaiters = null;
        }

        waiters?.TrySetResult();
    }

    private static void RaisePeak(ref int peak, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (value <= current)
                return;
            if (Interlocked.CompareExchange(ref peak, value, current) == current)
                return;
        }
    }

    private static async Task TryWriteConnectionRejectedAsync(Socket client, CancellationToken ct)
    {
        try
        {
            try { client.SendTimeout = 250; }
            catch (SocketException)
            {
                // best-effort write deadline
            }

            using var rejectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rejectCts.CancelAfter(TimeSpan.FromMilliseconds(250));
            using var stream = new NetworkStream(client, ownsSocket: false);
            await using var writer = new StreamWriter(
                stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            var response = new RpcResponse
            {
                Id = null,
                Error = new RpcError
                {
                    Code = ProtocolErrorCodes.InvalidRequest,
                    Message = "invalid_request",
                },
            };
            var json = JsonSerializer.Serialize(response, ProtocolJsonContext.Default.RpcResponse);
            await writer.WriteLineAsync(json.AsMemory(), rejectCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort reject line; the socket is closed by the caller.
        }
    }

    private async Task WriteResultLineAsync(
        IClientConnection connection,
        string? id,
        string method,
        JsonElement result,
        CancellationToken ct)
    {
        var response = new RpcResponse
        {
            Id = id,
            Result = result.ValueKind == JsonValueKind.Undefined ? null : result,
        };
        var json = JsonSerializer.Serialize(response, ProtocolJsonContext.Default.RpcResponse);
        if (NdjsonRpcBudget.Utf8LineBytes(json) > _options.MaxLineBytes)
        {
            if (string.Equals(method, ProtocolMethods.PaneRead, StringComparison.Ordinal)
                && result.ValueKind == JsonValueKind.Object
                && NdjsonRpcBudget.TryFitPaneRead(id, result, _options.MaxLineBytes, out var fitted))
            {
                json = fitted;
            }
            else
            {
                await WriteErrorAsync(
                        connection,
                        id,
                        ProtocolErrorCodes.InvalidParams,
                        "NDJSON line exceeds max size",
                        ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        await connection.WriteLineAsync(json, ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        IClientConnection connection,
        string? id,
        int code,
        string message,
        CancellationToken ct,
        string? errorCode = null,
        long? floorSeq = null)
    {
        var response = new RpcResponse
        {
            Id = id,
            Error = new RpcError
            {
                Code = code,
                Message = message,
                Data = new RpcErrorData
                {
                    RequestId = id,
                    Retryable = ProtocolErrors.IsRetryable(code),
                    ErrorCode = errorCode,
                    FloorSeq = floorSeq,
                },
            },
        };
        var json = JsonSerializer.Serialize(response, ProtocolJsonContext.Default.RpcResponse);
        await connection.WriteLineAsync(json, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var cts = _cts;
        _cts = null;
        try
        {
            if (cts is not null)
                await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        try { _listener?.Dispose(); }
        catch { /* ignore */ }
        _listener = null;

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        if (!_inFlight.IsEmpty)
        {
            try { await Task.WhenAll(_inFlight.Keys).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        try { cts?.Dispose(); }
        catch { /* ignore */ }

        var bound = _boundPath;
        var owned = _ownedIdentity;
        _boundPath = false;
        _ownedIdentity = null;
        if (!bound)
            return;

        if (owned is { } identity)
        {
            UnixSocketPathIdentity.RemoveIfOwned(_socketPath, identity);
            return;
        }

        try
        {
            if (File.Exists(_socketPath))
                File.Delete(_socketPath);
        }
        catch { /* ignore */ }
    }

    public static string ResolveSocketPath(string sessionName, bool honorEnvironment = true)
    {
        if (honorEnvironment)
        {
            var env = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
        }

        if (!SessionId.IsValidName(sessionName))
        {
            throw new ArgumentException(
                "Session name must be 1–64 characters of [A-Za-z0-9._-] only.",
                nameof(sessionName));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = "/tmp";

        return Path.Combine(home, ".config", "hypa", "runtime", sessionName, "hypa.sock");
    }
}

internal sealed class ClientConnection : IClientConnection, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ClientWriterQueue _lanes;
    private readonly int _maxLineBytes;
    private readonly SemaphoreSlim _attachWriteGate = new(1, 1);
    private int _disposed;

    public ClientConnection(
        string connectionId,
        Stream stream,
        int maxLineBytes = UnixSocketServerOptions.DefaultMaxLineBytes)
    {
        ConnectionId = connectionId;
        _stream = stream;
        _maxLineBytes = maxLineBytes > 0 ? maxLineBytes : UnixSocketServerOptions.DefaultMaxLineBytes;
        _lanes = new ClientWriterQueue(maxLineBytes: _maxLineBytes);
    }

    public string ConnectionId { get; }

    public string? ActiveRequestId { get; set; }

    public long? LastEmittedSeq { get; set; }

    /// <summary>The request lanes of this connection. The server sets them before the first read.</summary>
    internal ConnectionRequestLanes? RequestLanes { get; set; }

    /// <summary>
    /// The active request on the dispatch lane has applied its change. Keys
    /// that it fences can go before its reply. Call this only from the
    /// handler of that request.
    /// </summary>
    internal void MarkRequestApplied() => RequestLanes?.MarkActiveRequestApplied();

    public bool IsPluginAttributed { get; private set; }

    internal void NotePluginGrantToken(string? grantToken)
    {
        if (!string.IsNullOrWhiteSpace(grantToken))
            IsPluginAttributed = true;
    }

    /// <summary>Peer pid matched a live plugin command from ProcessPluginLauncher.</summary>
    internal void MarkPluginPeer() => IsPluginAttributed = true;

    public int MaxLineBytes => _maxLineBytes;

    public bool UsesWriterLanes => true;

    internal ClientWriterQueue Lanes => _lanes;

    internal Func<AttachSurfaceEmitBinding?, bool>? CanWriteRenderItem { get; set; }

    internal Func<bool>? AllowsUnboundAttachRender { get; set; }

    internal Func<ulong, ulong, AttachSurfaceEmitBinding?>? TryAdmitAttachRender { get; set; }

    public Action? WriterDrained { get; set; }

    /// <summary>
    /// Runs when the writer cancels a bound render item because its binding
    /// failed the recheck. The client never gets that line.
    /// </summary>
    internal Action<AttachSurfaceEmitBinding>? RenderItemCanceled { get; set; }

    public bool RequiresAttachRenderBinding => CanWriteRenderItem is not null;

    private void NoteRenderItemCanceled(AttachSurfaceEmitBinding binding)
    {
        try { RenderItemCanceled?.Invoke(binding); }
        catch { /* a reanchor mark must not fault the writer */ }
    }

    public bool TryResolveAttachRenderBinding(
        RuntimeEventRecord record,
        out AttachSurfaceEmitBinding? emitBinding)
    {
        emitBinding = null;
        if (CanWriteRenderItem is null)
            return true;
        if (record.AttachEmitSurfaceRevision is not { } surfaceRevision
            || record.AttachEmitSnapshotRevision is not { } snapshotRevision)
            return AllowsUnboundAttachRender?.Invoke() == true;
        if (TryAdmitAttachRender?.Invoke(surfaceRevision, snapshotRevision) is { } binding)
        {
            if (!CanWriteRenderItem(binding))
                return false;
            emitBinding = binding;
            return true;
        }

        return AllowsUnboundAttachRender?.Invoke() == true;
    }

    public void DiscardPendingRender()
    {
        _attachWriteGate.Wait();
        try
        {
            _lanes.DiscardPendingRender();
        }
        finally
        {
            _attachWriteGate.Release();
        }
    }

    public async Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
    {
        var admitted = _lanes.EnqueueResponseWait(jsonLine, out var written);
        if (!admitted.IsOk)
            throw new IOException("Reliable writer lane closed.");
        await written.WaitAsync(ct).ConfigureAwait(false);
    }

    internal async Task WriteAttachLiveLineAsync(
        string jsonLine,
        AttachSurfaceEmitBinding binding,
        CancellationToken ct)
    {
        await _attachWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (CanWriteRenderItem is not null && !CanWriteRenderItem(binding))
                return;
        }
        finally
        {
            _attachWriteGate.Release();
        }

        var admitted = _lanes.EnqueueResponseWait(jsonLine, binding, out var written);
        if (!admitted.IsOk)
            throw new IOException("Reliable writer lane closed.");
        await written.WaitAsync(ct).ConfigureAwait(false);
    }

    public WriterLaneResult EnqueueResponse(string jsonLine) =>
        _lanes.EnqueueResponse(jsonLine);

    public WriterLaneResult EnqueueResponse(byte[] utf8Json) =>
        _lanes.EnqueueResponse(utf8Json);

    public WriterLaneResult EnqueueReliableEvent(string jsonLine) =>
        _lanes.EnqueueReliableEvent(jsonLine);

    public WriterLaneResult EnqueueReliableEvent(byte[] utf8Json) =>
        _lanes.EnqueueReliableEvent(utf8Json);

    public WriterLaneResult EnqueueReliableEvent(ReadOnlyMemory<byte> ndjsonLine) =>
        _lanes.EnqueueReliableEvent(ndjsonLine);

    public WriterLaneResult TryEnqueueRender(string jsonLine) =>
        _lanes.TryEnqueueRender(jsonLine);

    public WriterLaneResult TryEnqueueRender(byte[] utf8Json) =>
        _lanes.TryEnqueueRender(utf8Json);

    public WriterLaneResult TryEnqueueRender(ReadOnlyMemory<byte> ndjsonLine) =>
        _lanes.TryEnqueueRender(ndjsonLine);

    public WriterLaneResult TryEnqueueRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        _lanes.TryEnqueueRender(ndjsonLine, emitBinding);

    public WriterLaneResult EnqueueOrderedRender(string jsonLine) =>
        _lanes.EnqueueOrderedRender(jsonLine);

    public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) =>
        _lanes.EnqueueOrderedRender(utf8Json);

    public WriterLaneResult EnqueueOrderedRender(ReadOnlyMemory<byte> ndjsonLine) =>
        _lanes.EnqueueOrderedRender(ndjsonLine);

    public WriterLaneResult EnqueueOrderedRender(
        ReadOnlyMemory<byte> ndjsonLine,
        AttachSurfaceEmitBinding? emitBinding) =>
        _lanes.EnqueueOrderedRender(ndjsonLine, emitBinding);

    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<string> jsonLines) =>
        _lanes.EnqueueOrderedRenderBatch(jsonLines);

    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<byte[]> utf8JsonLines) =>
        _lanes.EnqueueOrderedRenderBatch(utf8JsonLines);

    public WriterLaneResult EnqueueOrderedRenderBatch(IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines) =>
        _lanes.EnqueueOrderedRenderBatch(ndjsonLines);

    public WriterLaneResult EnqueueOrderedRenderBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines,
        AttachSurfaceEmitBinding? emitBinding) =>
        _lanes.EnqueueOrderedRenderBatch(ndjsonLines, emitBinding);

    public WriterLaneResult ReplaceWithCleanup(string jsonLine) =>
        _lanes.ReplaceWithCleanup(jsonLine);

    public WriterLaneResult ReplaceWithCleanup(byte[] utf8Json) =>
        _lanes.ReplaceWithCleanup(utf8Json);

    public void CompleteWriter() => _lanes.Complete();

    internal async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = await _lanes.DequeueAsync(ct, CanWriteRenderItem, NoteRenderItemCanceled).ConfigureAwait(false);
                if (item is null)
                    return;
                if (Volatile.Read(ref _disposed) != 0)
                {
                    item.Completion?.TrySetCanceled(ct);
                    _lanes.NotifyWriteCompleted();
                    return;
                }

                var needsAttachGate = CanWriteRenderItem is not null
                    && (item.EmitBinding is not null || !item.Reliable);
                if (needsAttachGate)
                    await _attachWriteGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (CanWriteRenderItem is not null
                        && !item.Reliable
                        && item.EmitBinding is null
                        && !CanWriteRenderItem(null))
                    {
                        item.Completion?.TrySetCanceled();
                        _lanes.NotifyWriteCompleted();
                        // A canceled item frees the lane like a written one,
                        // so waiting frames and retries run.
                        try { WriterDrained?.Invoke(); }
                        catch { /* retry must not fault the writer */ }
                        continue;
                    }

                    if (item.EmitBinding is { } binding
                        && CanWriteRenderItem is not null
                        && !CanWriteRenderItem(binding))
                    {
                        item.Completion?.TrySetCanceled();
                        NoteRenderItemCanceled(binding);
                        _lanes.NotifyWriteCompleted();
                        // A canceled item frees the lane like a written one,
                        // so waiting frames and retries run.
                        try { WriterDrained?.Invoke(); }
                        catch { /* retry must not fault the writer */ }
                        continue;
                    }

                    await _stream.WriteAsync(item.Utf8, ct).ConfigureAwait(false);
                    item.Completion?.TrySetResult();
                    _lanes.NotifyWriteCompleted();
                    try { WriterDrained?.Invoke(); }
                    catch { /* retry must not fault the writer */ }
                }
                catch (Exception ex)
                {
                    item.Completion?.TrySetException(ex);
                    _lanes.Complete();
                    throw;
                }
                finally
                {
                    if (needsAttachGate)
                        _attachWriteGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        _lanes.Complete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One control-plane RPC and the JSON-RPC id assigned for that call.
/// <see cref="Error"/> is set when the RPC failed after the id was assigned.
/// </summary>
public readonly record struct ControlPlaneCallResult(
    JsonElement Result,
    string? RequestId,
    Exception? Error);

/// <summary>Thin NDJSON client for CLI / agents with event demux.</summary>
public sealed class ControlPlaneClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    private readonly string _socketPath;
    private readonly Stream? _connectedStream;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _callTimeout;
    private readonly bool _validatePrivatePath;
    private Socket? _socket;
    private Stream? _stream;
    private BoundedNdjsonLineReader? _lines;
    private StreamWriter? _writer;
    private int _nextId;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private EndpointOutboundWriter? _outbound;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingCalls =
        new(StringComparer.Ordinal);
    private Func<JsonElement, bool>? _shellChunkAdmit;
    private Action? _lineReceived;
    private Action? _eventAdmitted;
    public const int DefaultMaxOrderedBytes = ClientWriterQueue.DefaultMaxReliableBytes;

    private readonly ClientEventQueue _events;

    /// <summary>The pool of the line buffers that the read loop rents. Each queued event holds one.</summary>
    private readonly ArrayPool<byte> _linePool;
    private int _replacedRender;
    private int _reanchorRequired;
    private long _linesReceived;
    private readonly SemaphoreSlim _eventSignal = new(0, int.MaxValue);
    private CancellationTokenSource? _readerCts;
    private Task? _reader;
    private int _notifyWrites;
    private readonly TaskCompletionSource _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControlPlaneClient(
        string socketPath,
        TimeSpan? connectTimeout = null,
        TimeSpan? callTimeout = null,
        bool validatePrivatePath = false)
        : this(socketPath, connectTimeout, callTimeout, DefaultMaxOrderedBytes, validatePrivatePath)
    {
    }

    internal ControlPlaneClient(
        string socketPath,
        TimeSpan? connectTimeout,
        TimeSpan? callTimeout,
        int maxOrderedBytes,
        bool validatePrivatePath = false,
        ArrayPool<byte>? linePool = null)
    {
        _socketPath = socketPath;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _events = new ClientEventQueue(maxOrderedBytes > 0 ? maxOrderedBytes : DefaultMaxOrderedBytes);
        _validatePrivatePath = validatePrivatePath;
        _linePool = linePool ?? ArrayPool<byte>.Shared;
    }

    /// <summary>
    /// NDJSON client over an already-open stream. Used by Connectivity attach.
    /// Does not dial a Unix socket.
    /// </summary>
    public static ControlPlaneClient FromConnectedStream(
        Stream stream,
        TimeSpan? callTimeout = null,
        int maxOrderedBytes = DefaultMaxOrderedBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("stream must be readable and writable.", nameof(stream));

        return new ControlPlaneClient(stream, callTimeout, maxOrderedBytes, ArrayPool<byte>.Shared);
    }

    /// <summary>
    /// <see cref="FromConnectedStream(Stream, TimeSpan?, int)"/> with the
    /// pool of the line buffers. Tests use it to count the buffers.
    /// </summary>
    internal static ControlPlaneClient FromConnectedStream(
        Stream stream,
        ArrayPool<byte> linePool,
        TimeSpan? callTimeout = null,
        int maxOrderedBytes = DefaultMaxOrderedBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(linePool);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("stream must be readable and writable.", nameof(stream));

        return new ControlPlaneClient(stream, callTimeout, maxOrderedBytes, linePool);
    }

    private ControlPlaneClient(Stream stream, TimeSpan? callTimeout, int maxOrderedBytes, ArrayPool<byte> linePool)
    {
        _socketPath = "connectivity";
        _connectedStream = stream;
        _connectTimeout = DefaultConnectTimeout;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _events = new ClientEventQueue(maxOrderedBytes > 0 ? maxOrderedBytes : DefaultMaxOrderedBytes);
        _linePool = linePool;
    }

    public bool UsesUnixSocket => _connectedStream is null;

    public TimeSpan ConnectTimeout => _connectTimeout;

    public TimeSpan CallTimeout => _callTimeout;

    /// <summary>Test/diagnostic view of in-flight RPC entries.</summary>
    internal int PendingCallCount => _pendingCalls.Count;

    /// <summary>Successful one-way writes. Does not include request/response calls.</summary>
    internal int NotifyWriteCount => Volatile.Read(ref _notifyWrites);

    /// <summary>True when a pending render was replaced before the consumer read it.</summary>
    public bool ReanchorRequired => Volatile.Read(ref _reanchorRequired) != 0;

    /// <summary>Ordered FIFO depth. Diagnostic for lossless Full delivery.</summary>
    public int OrderedQueueCount => _events.OrderedCount;

    /// <summary>Cached UTF-8 bytes charged to the ordered lane (not <c>GetRawText</c>).</summary>
    internal long OrderedQueuedBytes => _events.OrderedBytes;

    /// <summary>Queued reliable events. Diagnostic.</summary>
    internal int ReliableQueueCount => _events.ReliableCount;

    /// <summary>True when at least one reliable event waits in the queue.</summary>
    internal bool HasPendingReliableEvents => _events.ReliableCount > 0;

    /// <summary>True when the latest-wins render slot holds an event. Diagnostic.</summary>
    internal bool HasLatestRenderEvent => _events.HasPendingRender;

    /// <summary>
    /// Complete lines that the read loop received on this connection: events,
    /// RPC replies and render lines. Each one proves that the link is alive.
    /// </summary>
    internal long LinesReceived => Interlocked.Read(ref _linesReceived);

    /// <summary>
    /// Gives this client the receive rules of an attach. See
    /// <see cref="ClientEventQueue"/>. Null restores the first rules.
    /// </summary>
    internal void SetAttachReceivePolicy(AttachReceivePolicy? policy) => _events.SetPolicy(policy);

    internal AttachReceivePolicy? AttachReceivePolicy => _events.Policy;

    /// <summary>Queued chained cells delta bytes of one pane. Diagnostic.</summary>
    internal long PaneQueuedDeltaBytes(string paneId) => _events.PaneDeltaBytes(paneId);

    /// <summary>Cells deltas that the receive policy dropped. Diagnostic.</summary>
    internal long DroppedPaneDeltas => _events.DroppedDeltas;

    /// <summary>Scroll events that a newer scroll event of the same pane replaced. Diagnostic.</summary>
    internal long CoalescedScrollEvents => _events.CoalescedScrolls;

    internal bool HasPaneReanchorRequests => _events.HasPaneReanchorRequests;

    /// <summary>
    /// The panes whose queued deltas the receive policy dropped. The caller
    /// asks the mux for one reanchor of each pane.
    /// </summary>
    internal IReadOnlyList<string> TakePaneReanchorRequests() => _events.TakePaneReanchorRequests();

    /// <summary>The reanchor request of <paramref name="paneId"/> failed. Stop the drop of its deltas.</summary>
    internal void CancelPaneDrop(string paneId) => _events.CancelPaneDrop(paneId);

    public bool ConsumeReanchorRequest() =>
        Interlocked.Exchange(ref _reanchorRequired, 0) != 0;

    /// <summary>Test seam: drain one queued event without a live socket.</summary>
    internal bool TryTakeQueuedEvent(out JsonElement ev) => TryTakeEvent(out ev);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_reader is not null)
            throw new InvalidOperationException("Already connected");

        if (_connectedStream is not null)
        {
            StartReader(_connectedStream);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_connectTimeout);
        try
        {
            if (_validatePrivatePath)
                UnixPrivatePathGuard.ValidateBridgeConnectPath(_socketPath);

            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cts.Token)
                .ConfigureAwait(false);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                new EuidPeerAuthenticator().Authenticate(_socket);
            StartReader(new NetworkStream(_socket, ownsSocket: true));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlPlaneClientTimeoutException(
                $"Connect to '{_socketPath}' timed out after {_connectTimeout.TotalMilliseconds:0} ms.");
        }
    }

    private void StartReader(Stream stream)
    {
        _stream = stream;
        _lines = new BoundedNdjsonLineReader(_stream, UnixSocketServerOptions.DefaultMaxLineBytes);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _readerCts = new CancellationTokenSource();
        _reader = ReadLoopAsync(_readerCts.Token);
    }

    private string? _lastRequestId;

    /// <summary>
    /// JSON-RPC id of the last <see cref="CallAsync"/> that assigned an id.
    /// Concurrent callers must use <see cref="CallWithRequestIdAsync"/>; this
    /// property is last-writer-wins and must not stamp <c>tab.requested</c>.
    /// </summary>
    public string? LastRequestId => _lastRequestId;

    /// <summary>Id the next <see cref="CallAsync"/> will assign. Sequential callers only.</summary>
    public string PeekNextRequestId() =>
        (Volatile.Read(ref _nextId) + 1).ToString();

    /// <summary>
    /// Next JSON-RPC id. Shared with <see cref="CallWithRequestIdAsync"/> so a
    /// shell-lane write does not collide with an ordinary pending call.
    /// The lane on the attach loop owns the wait. This client does not.
    /// </summary>
    internal string AllocateRpcId()
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        _lastRequestId = id;
        return id;
    }

    /// <summary>
    /// The line is the existing shell-action RPC. This does not register a pending call.
    /// A failed write is not <c>Sent</c>.
    /// </summary>
    internal bool TryWriteRpcRequest(string id, string method, JsonObject? parameters)
    {
        if (_writer is null || _lines is null || _reader is null)
            return false;

        var held = false;
        try
        {
            if (!_ioGate.Wait(TimeSpan.FromSeconds(5)))
                return false;
            held = true;
            var writer = _writer;
            if (writer is null)
                return false;
            writer.WriteLine(FormatRpcRequest(id, method, parameters));
            _lastRequestId = id;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (held)
                _ioGate.Release();
        }
    }

    public async Task<JsonElement> CallAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var outcome = await CallWithRequestIdAsync(method, parameters, ct, timeout)
            .ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            ExceptionDispatchInfo.Capture(outcome.Error).Throw();
            return default;
        }

        return outcome.Result;
    }

    /// <summary>
    /// Same RPC as <see cref="CallAsync"/>, with this call's id in the result.
    /// AsyncLocal mutations do not flow back to an awaiting caller, so attach
    /// must not read <see cref="LastRequestId"/> after await.
    /// </summary>
    public async Task<ControlPlaneCallResult> CallWithRequestIdAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (_writer is null || _lines is null || _reader is null)
            throw new InvalidOperationException("Not connected");

        var budget = timeout ?? _callTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        var id = Interlocked.Increment(ref _nextId).ToString();
        _lastRequestId = id;
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCalls.TryAdd(id, pending))
            throw new InvalidOperationException("Duplicate RPC id");

        // The entry must not outlive this call for any exit path. A caller
        // cancel or a write failure that left the entry in place would keep it
        // until disconnect and grow the map for the life of the connection.
        try
        {
            var result = await CallCoreAsync(id, method, parameters, pending, ct, budget)
                .ConfigureAwait(false);
            return new ControlPlaneCallResult(result, id, null);
        }
        catch (Exception ex)
        {
            return new ControlPlaneCallResult(default, id, ex);
        }
        finally
        {
            _pendingCalls.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// One-way JSON-RPC notification. Omits <c>id</c> and does not wait for a reply.
    // / and continues.
    /// returns after write/flush. No application acknowledgement.
    /// </summary>
    public async Task NotifyAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken ct = default)
    {
        if (_writer is null || _lines is null || _reader is null)
            throw new InvalidOperationException("Not connected");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_callTimeout);

        var writer = _writer ?? throw new InvalidOperationException("Not connected");
        var held = false;
        try
        {
            await _ioGate.WaitAsync(cts.Token).ConfigureAwait(false);
            held = true;
            var req = new JsonObject
            {
                ["method"] = method,
            };
            if (parameters is not null)
                req["params"] = parameters;
            var grant = Environment.GetEnvironmentVariable(PluginEnv.GrantToken);
            if (!string.IsNullOrWhiteSpace(grant))
                req["grant_token"] = grant;

            await writer.WriteLineAsync(req.ToJsonString().AsMemory(), cts.Token)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _notifyWrites);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlPlaneClientTimeoutException(
                $"Notify '{method}' timed out after {_callTimeout.TotalMilliseconds:0} ms.");
        }
        finally
        {
            if (held)
                _ioGate.Release();
        }
    }

    private async Task<JsonElement> CallCoreAsync(
        string id,
        string method,
        JsonObject? parameters,
        TaskCompletionSource<string> pending,
        CancellationToken ct,
        TimeSpan budget)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        var writer = _writer ?? throw new InvalidOperationException("Not connected");
        var held = false;
        try
        {
            await _ioGate.WaitAsync(cts.Token).ConfigureAwait(false);
            held = true;
            var json = FormatRpcRequest(id, method, parameters);
            await writer.WriteLineAsync(json.AsMemory(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlPlaneClientTimeoutException(
                $"RPC '{method}' timed out after {budget.TotalMilliseconds:0} ms.");
        }
        finally
        {
            if (held)
                _ioGate.Release();
        }

        string line;
        try
        {
            line = await pending.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlPlaneClientTimeoutException(
                $"RPC '{method}' timed out after {budget.TotalMilliseconds:0} ms.");
        }

        var response = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RpcResponse)
            ?? throw new InvalidOperationException("Invalid response");
        if (response.Error is not null)
            throw new ControlPlaneException(
                response.Error.Code,
                response.Error.Message,
                response.Error.Data?.ErrorCode,
                response.Error.Data?.FloorSeq);
        return response.Result ?? default;
    }

    /// <summary>
    /// Source-generated RPC request line. Params that were built from
    /// <see cref="HostThemeSetParams"/> keep that contract on the wire.
    /// </summary>
    internal static string FormatRpcRequest(string id, string method, JsonObject? parameters)
    {
        JsonElement? paramsElement = null;
        if (parameters is not null)
        {
            paramsElement = parameters.Deserialize(ProtocolJsonContext.Default.JsonElement);
        }

        var grant = Environment.GetEnvironmentVariable(PluginEnv.GrantToken);
        var req = new RpcRequest
        {
            Id = id,
            Method = method,
            Params = paramsElement,
            GrantToken = string.IsNullOrWhiteSpace(grant) ? null : grant,
        };
        return JsonSerializer.Serialize(req, ProtocolJsonContext.Default.RpcRequest);
    }

    /// <summary>
    /// Kept for callers that used to yield the poll loop. The dedicated reader
    /// wakes <see cref="ReadEventAsync"/> when a line arrives.
    /// </summary>
    public void WakeRead()
    {
    }

    /// <summary>
    /// Read the next server-push event. RPC replies complete <c>_pendingCalls</c>
    /// directly and never wait for render consumer capacity.
    /// Drain order is reliable, ordered FIFO, then latest-wins render.
    /// </summary>
    public async Task<JsonElement> ReadEventAsync(CancellationToken ct = default)
    {
        if (_lines is null || _reader is null)
            throw new InvalidOperationException("Not connected");

        while (!ct.IsCancellationRequested)
        {
            if (TryTakeEvent(out var ev))
                return ev;
            if (_disconnected.Task.IsCompleted && !TryTakeEvent(out ev))
                throw new IOException("Connection closed");

            try
            {
                await _eventSignal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                throw new IOException("Connection closed", ex);
            }
        }

        throw new OperationCanceledException(ct);
    }

    private bool TryTakeEvent(out JsonElement ev)
    {
        if (_events.TryTake(out var taken))
        {
            Volatile.Write(ref _lastTakenEventReceivedAt, taken.ReceivedAt);
            ev = taken.Materialize();
            return true;
        }

        ev = default;
        return false;
    }

    /// <summary>
    // The socket reader admits a shell
    /// chunk while the render loop is blocked on the attach control gate.
    /// The callback must not take that gate. Return true when this line is the
    /// chunk the lane accepted, so it is not queued for a second admit.
    /// </summary>
    internal void SetShellChunkAdmit(Func<JsonElement, bool> admit)
    {
        ArgumentNullException.ThrowIfNull(admit);
        Volatile.Write(ref _shellChunkAdmit, admit);
    }

    internal void ClearShellChunkAdmit() => Volatile.Write(ref _shellChunkAdmit, null);

    /// <summary>
    /// before the admission filter. The callback must not take the attach
    /// control gate or the event queue lock.
    /// </summary>
    internal void SetLineReceived(Action onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        Volatile.Write(ref _lineReceived, onLine);
    }

    internal void ClearLineReceived() => Volatile.Write(ref _lineReceived, null);

    /// <summary>
    /// Runs after an event line is in the event queue, so a drain in the
    /// callback sees it. The callback must not take the attach control gate
    /// or the event queue lock.
    /// </summary>
    internal void SetEventAdmitted(Action onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        Volatile.Write(ref _eventAdmitted, onEvent);
    }

    internal void ClearEventAdmitted() => Volatile.Write(ref _eventAdmitted, null);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var lines = _lines ?? throw new InvalidOperationException("Not connected");
                var result = await lines.ReadLineUtf8Async(_linePool, ct).ConfigureAwait(false);
                if (result.IsEndOfStream)
                    throw new IOException("Connection closed");
                if (result.IsOverflow)
                {
                    FailClosed(new IOException("NDJSON line exceeded maximum length."));
                    return;
                }

                var line = result.Line ?? throw new IOException("Connection closed");
                // The line goes back to the pool at the end of this pass,
                // unless a queue takes it.
                var owned = line;
                try
                {
                    var receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    // An RPC reply, an event,
                    // and a render line each count. Do not take the queue lock here.
                    Interlocked.Increment(ref _linesReceived);
                    Volatile.Read(ref _lineReceived)?.Invoke();
                    // One pass over the bytes reads the fields that select the
                    // queue. A line that is not JSON throws, and the connection
                    // fails closed.
                    var fields = ReceiveLineFields.Read(line.Span);
                    if (!fields.IsObject)
                        throw new JsonException("NDJSON line is not a JSON object.");
                    if (fields.HasEvent)
                    {
                        // Keep the NDJSON UTF-8 in the lane. The render loop
                        // parses it one time when it takes the event.
                        owned = null;
                        if (!AdmitClientEvent(ClientEvent.FromLine(line, fields, line.Length, receivedAt)))
                            return;
                        Volatile.Read(ref _eventAdmitted)?.Invoke();
                        continue;
                    }

                    var id = fields.Id;
                    if (id is not null && _pendingCalls.TryRemove(id, out var pending))
                    {
                        pending.TrySetResult(Encoding.UTF8.GetString(line.Span));
                        continue;
                    }

                    // ClientShellEndpointResponseChunk before other client work.
                    // This path does not take the attach control gate. A chunk it
                    // accepts is not queued, so the render loop does not admit it again.
                    // Only this path needs a parsed element.
                    var admitShell = Volatile.Read(ref _shellChunkAdmit);
                    if (id is not null && admitShell is not null)
                    {
                        bool admitted;
                        using (var doc = JsonDocument.Parse(line.Memory))
                            admitted = admitShell(doc.RootElement);
                        if (admitted)
                            continue;
                    }

                    // A reply that is not an ordinary pending call and not an accepted
                    // shell chunk stays on the event queue for the render loop.
                    if (id is not null)
                    {
                        owned = null;
                        if (!AdmitClientEvent(ClientEvent.FromLine(line, fields, line.Length, receivedAt)))
                            return;
                    }
                }
                finally
                {
                    owned?.Return();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FailClosed(ex);
            return;
        }
        finally
        {
            _events.CloseReliable();
            _disconnected.TrySetResult();
            FailPendingCalls(new IOException("Connection closed"));
            PulseEvents();
        }
    }

    /// <summary>
    /// payload.len(); it does not re-encode to measure. The NDJSON reader
    /// passes the line UTF-8 length. Cache it on the queue entry. Do not
    /// call GetRawText to count bytes.
    /// </summary>
    internal bool AdmitEvent(JsonElement payload, int encodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(encodedBytes);
        return AdmitClientEvent(
            ClientEvent.FromElement(payload, encodedBytes, System.Diagnostics.Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Store NDJSON UTF-8 in the lane, as the read loop does. The fields that
    /// select the queue come from the same bytes. No pool owns
    /// <paramref name="utf8"/>.
    /// <paramref name="receivedAt"/> is the monotonic time at which the read
    /// loop had the line. Zero means now.
    /// </summary>
    internal bool AdmitEventUtf8(byte[] utf8, int encodedBytes, long receivedAt = 0)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentOutOfRangeException.ThrowIfNegative(encodedBytes);
        if (receivedAt == 0)
            receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var line = RentedUtf8Line.Unpooled(utf8, Math.Min(encodedBytes, utf8.Length));
        return AdmitClientEvent(ClientEvent.FromLine(line, ReceiveLineFields.Read(line.Span), encodedBytes, receivedAt));
    }

    /// <summary>
    /// The monotonic time at which the read loop had the event that the last
    /// <see cref="ReadEventAsync"/> returned. The attach measures its
    /// receive hop from this value. Zero before the first event.
    /// </summary>
    internal long LastTakenEventReceivedAt => Volatile.Read(ref _lastTakenEventReceivedAt);

    private long _lastTakenEventReceivedAt;

    /// <summary>
    /// The capabilities of the endpoint welcome on this connection. The attach
    /// writes them after its hello. Empty before a hello.
    /// </summary>
    internal void NotePeerCapabilities(IReadOnlyList<string>? capabilities) =>
        Volatile.Write(ref _peerCapabilities, capabilities is null ? [] : [.. capabilities]);

    /// <summary>True when the endpoint welcome on this connection listed <paramref name="capability"/>.</summary>
    internal bool HasPeerCapability(string capability) =>
        Array.IndexOf(Volatile.Read(ref _peerCapabilities), capability) >= 0;

    private string[] _peerCapabilities = [];

    private RenderAckSender? _renderAcks;

    /// <summary>
    /// True when this client offered render credit in its hello and the
    /// welcome listed it. Only then does the attach send <c>render.ack</c>.
    /// </summary>
    internal bool RenderCreditEnabled => Volatile.Read(ref _renderAcks) is not null;

    /// <summary><c>render.ack</c> notifications that this client wrote. Diagnostic.</summary>
    internal long RenderAcksSent => Volatile.Read(ref _renderAcks)?.Sent ?? _renderAcksSentBefore;

    private long _renderAcksSentBefore;

    /// <summary>
    /// Turns the <c>render.ack</c> notifications of this connection on or off.
    /// The attach calls it after each hello on the connection.
    /// </summary>
    internal void SetRenderCredit(bool enabled)
    {
        var next = enabled ? new RenderAckSender(WriteAcceptedLineAsync) : null;
        var prior = Interlocked.Exchange(ref _renderAcks, next);
        if (prior is not null)
        {
            Interlocked.Add(ref _renderAcksSentBefore, prior.Sent);
            prior.Stop();
        }
    }

    /// <summary>
    /// Acks the render frames of one host paint: for each pane, the highest
    /// generation that the paint showed or dropped. The write runs on another
    /// task. Without render credit this does nothing.
    /// </summary>
    internal void NoteRenderPainted(IReadOnlyList<RenderAck> painted) =>
        Volatile.Read(ref _renderAcks)?.Note(painted);

    private bool AdmitClientEvent(ClientEvent item)
    {
        switch (_events.Admit(item))
        {
            case ClientEventAdmit.OverBudget:
                FailClosed(new IOException("Ordered event lane exceeded the byte budget."));
                return false;
            case ClientEventAdmit.Closed:
                FailClosed(new IOException("Reliable event lane closed."));
                return false;
            case ClientEventAdmit.ReplacedRender:
                Interlocked.Increment(ref _replacedRender);
                Volatile.Write(ref _reanchorRequired, 1);
                break;
            case ClientEventAdmit.Dropped:
                return true;
            case ClientEventAdmit.DroppedForReanchor:
                _events.Policy?.ReanchorRequested?.Invoke();
                return true;
        }

        PulseEvents();
        return true;
    }

    private void PulseEvents()
    {
        try { _eventSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
        catch (ObjectDisposedException) { /* disposing */ }
    }

    private void FailClosed(Exception ex)
    {
        FailPendingCalls(ex);
        _events.CloseReliable();
        Volatile.Write(ref _reanchorRequired, 1);
        _disconnected.TrySetResult();
        PulseEvents();
    }

    private void FailPendingCalls(Exception ex)
    {
        foreach (var id in _pendingCalls.Keys)
        {
            if (_pendingCalls.TryRemove(id, out var pending))
                pending.TrySetException(ex);
        }
    }

    private async Task<string> ReadLineCoreAsync(CancellationToken ct)
    {
        var lines = _lines ?? throw new InvalidOperationException("Not connected");
        var result = await lines.ReadLineAsync(ct).ConfigureAwait(false);
        if (result.IsEndOfStream)
            throw new IOException("Connection closed");
        if (result.IsOverflow)
            throw new IOException("NDJSON line exceeded maximum length.");
        return result.Line ?? throw new IOException("Connection closed");
    }

    /// <summary>Drain reliable events currently buffered (non-blocking). Does not take render.</summary>
    public IReadOnlyList<JsonElement> DrainPendingEvents()
    {
        var drained = _events.DrainReliable();
        var list = new List<JsonElement>(drained.Count);
        foreach (var ev in drained)
            list.Add(ev.Materialize());
        return list;
    }

    /// <summary>
    /// Drain buffered reliable, ordered, and latest-wins render events.
    /// Does not wait for a later line.
    /// </summary>
    public IReadOnlyList<JsonElement> DrainPendingAllEvents()
    {
        var list = new List<JsonElement>();
        while (TryTakeEvent(out var ev))
            list.Add(ev);
        return list;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool IsTransportClosed => IsDisposed || _disconnected.Task.IsCompleted;

    public bool HasOpenConnection => _stream is not null || _connectedStream is not null;

    /// <summary>
    /// Wait until the dedicated reader sees end-of-stream or a socket fault.
    /// Does not peek the socket, so it cannot steal an RPC reply.
    /// </summary>
    public async Task WatchDisconnectAsync(CancellationToken ct = default)
    {
        if (_reader is null)
            throw new InvalidOperationException("Not connected");

        await _disconnected.Task.WaitAsync(ct).ConfigureAwait(false);
        throw new IOException("Connection closed");
    }

    /// <summary>
    /// When this connection dies, dispose <paramref name="peer"/> so a C drop
    /// tears R without a third socket.
    /// </summary>
    public async Task DisposePeerOnDisconnectAsync(ControlPlaneClient peer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            await WatchDisconnectAsync(ct).ConfigureAwait(false);
            throw new IOException("Connection closed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                try { await peer.DisposeAsync().ConfigureAwait(false); }
                catch { /* peer already dead */ }
            }
        }
    }

    /// <summary>
    /// A second caller of the same client uses this queue.
    /// </summary>
    internal EndpointOutboundWriter Outbound
    {
        get
        {
            var existing = Volatile.Read(ref _outbound);
            if (existing is not null)
                return existing;
            var created = new EndpointOutboundWriter(WriteAcceptedLineAsync);
            var prior = Interlocked.CompareExchange(ref _outbound, created, null);
            return prior ?? created;
        }
    }

    /// <summary>
    // Registers the reply, encodes one frame, and
    /// <c>try_send</c>s it. The returned task is the reply. Acceptance does not
    /// wait for that reply.
    /// </summary>
    internal bool TryAcceptFrame(
        string method,
        JsonObject? parameters,
        TimeSpan replyTimeout,
        out Task<ControlPlaneCallResult> reply,
        out string rejection)
    {
        reply = Task.FromResult(new ControlPlaneCallResult(
            default,
            null,
            new InvalidOperationException("Not connected")));
        if (IsDisposed || _writer is null)
        {
            rejection = "Not connected";
            return false;
        }

        var outbound = Outbound;
        if (outbound.IsStopped)
        {
            rejection = "endpoint writer stopped";
            return false;
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCalls.TryAdd(id, pending))
        {
            rejection = "Duplicate RPC id";
            return false;
        }

        var line = FormatRpcRequest(id, method, parameters);
        if (!outbound.TrySend(line, pending, out var error))
        {
            _pendingCalls.TryRemove(id, out _);
            rejection = error;
            return false;
        }

        reply = WaitForAcceptedCallAsync(id, method, pending, replyTimeout);
        rejection = string.Empty;
        return true;
    }

    private async Task WriteAcceptedLineAsync(string line, CancellationToken ct)
    {
        var writer = _writer ?? throw new IOException("endpoint writer stopped");
        var held = false;
        try
        {
            await _ioGate.WaitAsync(ct).ConfigureAwait(false);
            held = true;
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            if (held)
                _ioGate.Release();
        }
    }

    private async Task<ControlPlaneCallResult> WaitForAcceptedCallAsync(
        string id,
        string method,
        TaskCompletionSource<string> pending,
        TimeSpan budget)
    {
        try
        {
            using var cts = new CancellationTokenSource(budget);
            string line;
            try
            {
                line = await pending.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new ControlPlaneClientTimeoutException(
                    $"RPC '{method}' timed out after {budget.TotalMilliseconds:0} ms.");
            }

            var response = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RpcResponse)
                ?? throw new InvalidOperationException("Invalid response");
            if (response.Error is not null)
            {
                throw new ControlPlaneException(
                    response.Error.Code,
                    response.Error.Message,
                    response.Error.Data?.ErrorCode);
            }

            return new ControlPlaneCallResult(response.Result ?? default, id, null);
        }
        catch (Exception ex)
        {
            return new ControlPlaneCallResult(default, id, ex);
        }
        finally
        {
            _pendingCalls.TryRemove(id, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        Volatile.Read(ref _outbound)?.Stop();
        Volatile.Read(ref _renderAcks)?.Stop();
        try { _readerCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _lines = null;
        _writer?.Dispose();
        _stream?.Dispose();
        _socket?.Dispose();
        FailPendingCalls(new ObjectDisposedException(nameof(ControlPlaneClient)));
        _events.Release();
        _disconnected.TrySetResult();
        PulseEvents();
        return ValueTask.CompletedTask;
    }

    private int _disposed;
}
