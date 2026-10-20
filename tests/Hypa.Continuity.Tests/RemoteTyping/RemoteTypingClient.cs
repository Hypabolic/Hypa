using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Tests.Support;
using Hypa.ControlPlane;

namespace Hypa.Continuity.Tests.RemoteTyping;

/// <summary>
/// The attach client side of the remote typing bench. It uses the attach
/// pieces of the real client over one joined control stream: the subscribe,
/// the visible set and the observe calls, the input lease, <c>pane.send_keys</c>
/// with a <c>seq</c>, <see cref="AttachSession.TryWriteCells"/> for each cells
/// body, and one <see cref="AttachLatencyMeter"/>. A chrome-off live state for
/// each visible pane composes that pane at the host origin.
/// </summary>
internal sealed class RemoteTypingClient : IAsyncDisposable
{
    private readonly FramedControlStream _stream;
    private readonly ControlPlaneClient _client;
    private readonly Dictionary<string, PaneView> _views = new(StringComparer.Ordinal);
    private readonly List<PaneView> _retired = [];
    private readonly object _viewGate = new();
    private readonly object _closedGate = new();
    private readonly Dictionary<long, AttachKeyClosed> _closed = [];
    private readonly object _changeGate = new();
    private TaskCompletionSource _changed = NewChange();
    private AttachInputSender? _sender;
    private string _targetPane = "";
    private string _targetLease = "";
    private readonly CancellationTokenSource _stop = new();
    private string _subscriptionId = "";

    private Task? _events;
    private long _reanchors;
    private long _rejectedFrames;
    private long _cellsBodies;
    private string? _loopExit;
    private volatile string _loopState = "start";
    private readonly Dictionary<string, int> _bodiesByPane = new(StringComparer.Ordinal);

    private RemoteTypingClient(FramedControlStream stream, ControlPlaneClient client)
    {
        _stream = stream;
        _client = client;
        Meter = new AttachLatencyMeter(keyClosed: OnKeyClosed);
    }

    /// <summary>The one meter of this attach.</summary>
    public AttachLatencyMeter Meter { get; }

    /// <summary>Render lane replacements and generation breaks that needed a new observe.</summary>
    public long Reanchors => Interlocked.Read(ref _reanchors);

    /// <summary>Cells bodies that <see cref="AttachSession.TryWriteCells"/> did not apply.</summary>
    public long RejectedFrames => Interlocked.Read(ref _rejectedFrames);

    public long CellsBodies => Interlocked.Read(ref _cellsBodies);

    /// <summary>The fault that stopped the event loop, or null.</summary>
    public string? LoopFault => _loopExit is { } exit && exit.StartsWith("fault", StringComparison.Ordinal) ? exit : null;

    /// <summary>Cells bodies by pane and kind, for a failure message.</summary>
    public string Describe()
    {
        lock (_viewGate)
        {
            return $"loop_state={_loopState} loop_exit={_loopExit ?? "none"} disposed={_client.IsDisposed} bodies={CellsBodies} rejected={RejectedFrames} reanchors={Reanchors} views=[{string.Join(",", _views.Keys)}] "
                + string.Join(" ", _bodiesByPane.Select(kv => kv.Key + "=" + kv.Value));
        }
    }

    public static async Task<RemoteTypingClient> ConnectAsync(IFramedSession session)
    {
        var stream = new FramedControlStream(session, ownsSession: false);
        var client = ControlPlaneClient.FromConnectedStream(stream, callTimeout: TimeSpan.FromSeconds(30));
        await client.ConnectAsync();
        var typing = new RemoteTypingClient(stream, client);
        // The attach of a remote link says hello and turns its surface on.
        // The hello offers render credit; the welcome sets the optional
        // features of the connection (input_ack and render credit).
        // HYPA_REMOTE_TYPING_RENDER_CREDIT=0 sends a hello without it, for an A/B.
        await ActivateSurfaceAsync(client, renderCredit: RemoteTypingLatencyGate.RenderCreditOffered);
        var subscribed = await AttachSession.SubscribeAsync(
            client,
            ["control", "lifecycle", "render"],
            CancellationToken.None);
        typing._subscriptionId = subscribed.SubscriptionId;
        typing._sender = new AttachInputSender(
            client,
            () => (Volatile.Read(ref typing._targetPane), Volatile.Read(ref typing._targetLease)),
            latency: typing.Meter);
        typing._events = Task.Run(() => typing.EventLoopAsync(typing._stop.Token));
        return typing;
    }

    /// <summary>True when the hello of this client offered render credit and the mux took it.</summary>
    public bool RenderCredit => _client.RenderCreditEnabled;

    /// <summary><c>render.ack</c> notifications that this client wrote.</summary>
    public long RenderAcksSent => _client.RenderAcksSent;

    private static async Task ActivateSurfaceAsync(ControlPlaneClient client, bool renderCredit)
    {
        const string clientId = "remote-typing-bench";
        var geometry = new AttachGeometry
        {
            Columns = 200,
            Rows = 50,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };
        var rpc = new AttachEndpointRpcClient(client);
        var (welcome, _) = await rpc.HelloAsync(
            AttachEndpointPreflight.BuildHello(clientId, geometry, surfaceActive: true, renderCredit: renderCredit));
        if (welcome.Error is not null)
            throw new InvalidOperationException("attach hello failed: " + welcome.Error.Message);
        var interest = await rpc.SurfaceInterestAsync(new AttachSurfaceInterestRequest
        {
            RequestId = "bench-surface-on",
            ClientId = clientId,
            Active = true,
            GeometryRevision = geometry.GeometryRevision,
        });
        if (!interest.Active)
            throw new InvalidOperationException("attach surface_interest did not activate");
    }

    /// <summary>Round trips of <c>ping</c> on the control stream, in milliseconds.</summary>
    public async Task<List<double>> PingAsync(int count)
    {
        var times = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await _client.CallAsync(ProtocolMethods.Ping);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return times;
    }

    /// <summary>
    /// Shows the panes: the visible set, an observe of each pane, the input
    /// lease, and a resize to <paramref name="cols"/> x <paramref name="rows"/>.
    /// Returns the input lease of the first pane.
    /// </summary>
    public async Task<string> ShowAsync(IReadOnlyList<string> paneIds, int cols, int rows)
    {
        lock (_viewGate)
        {
            // The event loop can still hold an old view. Retire it, and
            // dispose it only when the client stops.
            _retired.AddRange(_views.Values);
            _views.Clear();
            foreach (var paneId in paneIds)
                _views[paneId] = new PaneView(paneId, cols, rows, Meter, capture: paneId == paneIds[0]);
        }

        var nodes = new JsonNode?[paneIds.Count];
        for (var i = 0; i < paneIds.Count; i++)
            nodes[i] = JsonValue.Create(paneIds[i]);
        await _client.CallAsync(
            ProtocolMethods.TerminalVisibleSet,
            new JsonObject { ["subscription_id"] = _subscriptionId, ["pane_ids"] = new JsonArray(nodes) });
        string? firstLease = null;
        for (var i = 0; i < paneIds.Count; i++)
        {
            var lease = await ClaimInputLeaseAsync(paneIds[i]);
            firstLease ??= lease;
            await _client.CallAsync(
                ProtocolMethods.PaneResize,
                new JsonObject { ["pane_id"] = paneIds[i], ["cols"] = cols, ["rows"] = rows, ["lease_id"] = lease });
            await ObserveAsync(paneIds[i], replace: i == 0);
        }

        Volatile.Write(ref _targetLease, firstLease!);
        Volatile.Write(ref _targetPane, paneIds[0]);
        return firstLease!;
    }

    /// <summary>
    /// Sends the bytes of one key the way the attach input loop does: the
    /// read opens the key on the meter, and the bytes go to the production
    /// <see cref="AttachInputSender"/> with the stamp of that read. The sender
    /// puts a seq on the batch and sends <c>pane.send_keys</c> to the pane of
    /// the last <see cref="ShowAsync"/>. Returns the read time, which names
    /// the key.
    /// </summary>
    public async Task<long> SendKeyAsync(byte[] bytes)
    {
        var readAt = Meter.Now();
        Meter.NoteStdinRead(bytes, readAt);
        if (!await _sender!.EnqueueAsync(bytes, _stop.Token, Meter.StampKeys(bytes)))
            throw new InvalidOperationException("the attach input sender refused a key: " + _sender.Fault);
        return readAt;
    }

    /// <summary>
    /// A task that completes at the next change that the bench reads: an
    /// event that the loop handled, a closed key, or the end of the loop.
    /// Take it before the check of a state, so no change is lost.
    /// </summary>
    public Task NextChange
    {
        get
        {
            lock (_changeGate)
                return _changed.Task;
        }
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> is true, or until
    /// <paramref name="limit"/> ends. The limit is a bench window: the state
    /// at its end is the result. Each change of the client checks the
    /// condition again.
    /// </summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var change = NextChange;
            if (condition())
                return true;
            var left = limit - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
                return false;
            try
            {
                await change.WaitAsync(left).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return condition();
            }
        }
    }

    /// <summary>The closed keys of the read at <paramref name="readAt"/>, or null while they are open.</summary>
    public AttachKeyClosed? Closed(long readAt)
    {
        lock (_closedGate)
            return _closed.TryGetValue(readAt, out var closed) ? closed : null;
    }

    /// <summary>True when the pane view has a frame.</summary>
    public bool HasFrame(string paneId)
    {
        PaneView? view;
        lock (_viewGate)
            _views.TryGetValue(paneId, out view);
        return view is not null && view.Live.TryGetPaneFrame(paneId, out var frame) && frame is not null;
    }

    /// <summary>The bytes that the client wrote to the host for <paramref name="paneId"/>.</summary>
    public byte[] HostBytes(string paneId)
    {
        PaneView view;
        lock (_viewGate)
            view = _views[paneId];
        return view.Live.WithPaint(() => view.Captured());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_sender is not null)
            await _sender.DisposeAsync();
        if (_events is not null)
            await Poll.EndAsync(_events, "the client event loop to stop");
        await _client.DisposeAsync();
        await _stream.DisposeAsync();
        lock (_viewGate)
        {
            foreach (var view in _views.Values.Concat(_retired))
                view.Dispose();
            _views.Clear();
            _retired.Clear();
        }

        _stop.Dispose();
    }

    private void OnKeyClosed(AttachKeyClosed closed)
    {
        lock (_closedGate)
            _closed[closed.ReadTimestamp] = closed;
        PulseChange();
    }

    private void PulseChange()
    {
        TaskCompletionSource previous;
        lock (_changeGate)
        {
            previous = _changed;
            _changed = NewChange();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource NewChange() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<string> ClaimInputLeaseAsync(string paneId)
    {
        var claimed = await _client.CallAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["scope"] = "input",
                ["ttl_ms"] = 600_000,
                ["takeover"] = true,
            });
        return claimed.GetProperty("lease_id").GetString()!;
    }

    private Task<JsonElement> ObserveAsync(string paneId, bool replace) =>
        _client.CallAsync(
            ProtocolMethods.TerminalObserve,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["subscription_id"] = _subscriptionId,
                ["replace"] = replace,
            });

    private async Task EventLoopAsync(CancellationToken ct)
    {
        try
        {
            await HandleEventsAsync(ct);
        }
        finally
        {
            // No change comes after the loop ends. Wake each wait now.
            PulseChange();
        }
    }

    private async Task HandleEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonElement ev;
            try
            {
                _loopState = "read";
                ev = await _client.ReadEventAsync(ct);
                _loopState = "handle";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                _loopExit = "read: " + ex.Message;
                return;
            }

            try
            {
                if (_client.ConsumeReanchorRequest())
                {
                    // A render body was replaced in the latest-wins slot. The
                    // chain of deltas is broken: observe each pane again, as
                    // the attach does.
                    Interlocked.Increment(ref _reanchors);
                    foreach (var view in Views())
                    {
                        view.Live.ResetAppliedBlit();
                        _loopState = "observe-lane " + view.PaneId;
                        await ObserveAsync(view.PaneId, replace: false);
                    }
                }

                if (!TryReadCells(ev, out var cells))
                {
                    var type = ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("params", out var p)
                        && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var t)
                        ? t.GetString() ?? "?"
                        : ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("method", out var m) ? "method:" + m.GetString() : "other";
                    lock (_viewGate)
                        _bodiesByPane["event:" + type] = _bodiesByPane.GetValueOrDefault("event:" + type) + 1;
                    continue;
                }
                Interlocked.Increment(ref _cellsBodies);
                PaneView? target;
                lock (_viewGate)
                {
                    _views.TryGetValue(cells.PaneId ?? "", out target);
                    var key = (cells.PaneId ?? "?") + (cells.Full ? ":full" : cells.Reanchor ? ":reanchor" : ":delta");
                    _bodiesByPane[key] = _bodiesByPane.GetValueOrDefault(key) + 1;
                }

                if (target is null)
                {
                    AckFrame(cells);
                    continue;
                }

                var applied = AttachSession.TryWriteCells(target.Tty, target.Live, cells, reanchorBeforePaint: true);
                // After the host write, as the attach render loop does.
                AckFrame(cells);
                if (applied)
                    continue;
                Interlocked.Increment(ref _rejectedFrames);
                if (target.Live.BlitReanchorPending)
                {
                    target.Live.BlitReanchorPending = false;
                    Interlocked.Increment(ref _reanchors);
                    _loopState = "observe-blit " + target.PaneId;
                    await ObserveAsync(target.PaneId, replace: false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                _loopExit = "handle: " + ex.Message;
                return;
            }
            catch (ControlPlaneClientTimeoutException)
            {
                // An observe did not answer in time. The next break asks again.
            }
            catch (ControlPlaneException)
            {
                // An observe raced a pane that the bench closed. The loop
                // keeps reading.
            }
            catch (Exception ex)
            {
                _loopExit = "fault: " + ex;
                return;
            }
            finally
            {
                // A handled event can add a frame or host bytes.
                PulseChange();
            }
        }
    }

    private void AckFrame(TerminalRenderCellsPayload cells)
    {
        if (cells.PaneId is { Length: > 0 } paneId && cells.Generation > 0)
            _client.NoteRenderPainted([new RenderAck(paneId, cells.Generation, RenderRows.CellsRowEnd(cells))]);
    }

    private List<PaneView> Views()
    {
        lock (_viewGate)
            return [.. _views.Values];
    }

    private static bool TryReadCells(JsonElement ev, out TerminalRenderCellsPayload cells)
    {
        cells = null!;
        if (ev.ValueKind != JsonValueKind.Object
            || !ev.TryGetProperty("params", out var parms)
            || !parms.TryGetProperty("type", out var type)
            || type.GetString() != ProtocolEventTypes.TerminalRender
            || !parms.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("kind", out var kind)
            || kind.GetString() != TerminalRenderCellsPayload.KindCells)
        {
            return false;
        }

        var parsed = payload.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        if (parsed is null)
            return false;
        cells = parsed;
        return true;
    }

    /// <summary>A chrome-off live state that composes one pane over an in-memory host terminal.</summary>
    private sealed class PaneView : IDisposable
    {
        private readonly Stream _host;

        public PaneView(string paneId, int cols, int rows, AttachLatencyMeter meter, bool capture)
        {
            PaneId = paneId;
            _host = capture ? new MemoryStream() : new DiscardStream();
            Tty = new UnixRawTerminal(_host, cols, rows);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
                Latency = meter,
            };
        }

        public string PaneId { get; }

        public UnixRawTerminal Tty { get; }

        public AttachLiveState Live { get; }

        public byte[] Captured() => _host is MemoryStream memory ? memory.ToArray() : [];

        public void Dispose() => Tty.Dispose();
    }

    /// <summary>A host terminal that drops its bytes: the bulk pane output is large.</summary>
    private sealed class DiscardStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
        }
    }
}
