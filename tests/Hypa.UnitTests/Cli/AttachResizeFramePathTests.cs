using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// A host resize runs a chain of round trips to the mux: the chrome refresh
/// and a <c>pane.resize</c> for each visible pane. On a cube that chain takes
/// hundreds of milliseconds. The render loop must not wait for it. A frame
/// whose size agrees with the box of its pane paints when it comes, and a
/// frame of the old size never paints into the new layout.
/// <para>
/// One client is the render reader and the control client, as on a cube.
/// The mux stand-in holds the reply of each <c>pane.resize</c> until the
/// test releases it. Thus each check runs while the resize waits.
/// </para>
/// </summary>
public sealed class AttachResizeFramePathTests
{
    private static readonly TimeSpan PromptPaint = TimeSpan.FromMilliseconds(20);

    // No chrome text contains these runs of letters.
    private const string EchoMarker = "zqxj";
    private const string OldLeftMarker = "vqkw";
    private const string OldRightMarker = "jxqz";
    private const string NewRightMarker = "kwvq";
    private const string WarmMarker = "wqzv";

    // The generation that each pane shows when a test starts.
    private const long Shown = 2;

    [SkippableFact]
    public async Task A_frame_of_the_new_size_paints_at_once_while_a_pane_resize_waits_for_its_reply()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var run = await ResizeRun.StartAsync();

        // The chain waits for its first reply. A scroll event of a pane comes
        // in that time, as with a pane that has output. The host size is new
        // and the chrome is old, so the scroll event builds the chrome again
        // and marks a pane resize.
        run.Mux.HoldSnapshotReplies();
        run.Resize(100, 30);
        await run.Mux.WaitForRequestAsync(ProtocolMethods.SessionSnapshot);
        run.Mux.Send(run.ScrollEventLine("p2", maxOffset: 40));

        // The mux resizes the pane, posts its full frame at the new size,
        // and then the echo of a key, before the reply of the resize.
        var resize = await run.Mux.WaitForRequestAsync(ProtocolMethods.PaneResize, "p1");
        var full = run.Body("p1", resize.Cols, resize.Rows, full: true, generation: Shown + 1, baseGeneration: 0, "$ ");
        var echo = run.Body("p1", resize.Cols, resize.Rows, full: false, generation: Shown + 2, baseGeneration: Shown + 1, "$ " + EchoMarker);
        var sentAt = Stopwatch.GetTimestamp();
        run.Mux.Send(run.RenderEventLine(full) + run.RenderEventLine(echo));

        var paintedAt = await run.Host.WaitForAsync(EchoMarker);
        var waited = Stopwatch.GetElapsedTime(sentAt, paintedAt);
        Assert.True(
            waited < PromptPaint,
            "the echo waited " + waited.TotalMilliseconds.ToString("0.0") + " ms behind the pane resize");
        Assert.False(
            run.Mux.RepliedBefore(resize.Id, paintedAt),
            "the resize reply came before the paint; the test did not hold the resize");

        run.Mux.ReleaseSnapshotReplies();
        run.Mux.ReleaseResizeReplies();
        await run.Lane.Idle.WaitAsync(TestWait.HangGuard);
        await run.WaitForPaneResizesIdleAsync();
    }

    [SkippableFact]
    public async Task An_echo_delta_that_comes_before_the_new_layout_chains_and_paints_at_once()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var run = await ResizeRun.StartAsync();

        // The resize chain starts. Until the layout reply comes, the chrome
        // and the pane are at the old size, so the echo of a key typed now
        // agrees with its box and chains on the shown frame.
        run.Mux.HoldSnapshotReplies();
        run.Resize(100, 30);
        await run.Mux.WaitForRequestAsync(ProtocolMethods.SessionSnapshot);
        var box = run.Box("p1");
        var echo = run.Body("p1", box.Cols, box.Rows, full: false, generation: Shown + 1, baseGeneration: Shown, "$ " + EchoMarker);
        var sentAt = Stopwatch.GetTimestamp();
        run.Mux.Send(run.RenderEventLine(echo));

        var paintedAt = await run.Host.WaitForAsync(EchoMarker);
        var waited = Stopwatch.GetElapsedTime(sentAt, paintedAt);
        Assert.True(
            waited < PromptPaint,
            "the echo waited " + waited.TotalMilliseconds.ToString("0.0") + " ms in the resize chain");
        // The paint writes the host bytes first, then records the applied
        // generation. The snapshot replies are held, so no other frame comes.
        await Poll.UntilAsync(
            () => run.Live.LastAppliedBlit("p1").Generation == Shown + 1,
            "the echo to chain on the shown frame");

        run.Mux.ReleaseSnapshotReplies();
        run.Mux.ReleaseResizeReplies();
        await run.Lane.Idle.WaitAsync(TestWait.HangGuard);
    }

    [SkippableFact]
    public async Task A_frame_of_the_old_size_does_not_paint_into_the_new_layout()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var run = await ResizeRun.StartAsync();
        var oldLeft = run.Box("p1");
        var oldRight = run.Box("p2");

        run.Resize(100, 30);
        var resize = await run.Mux.WaitForRequestAsync(ProtocolMethods.PaneResize, "p1");
        // The resize of each pane goes at once: the right pane does not wait
        // for the reply of the left pane.
        var right = await run.Mux.WaitForRequestAsync(ProtocolMethods.PaneResize, "p2");
        Assert.False(
            run.Mux.RepliedBefore(resize.Id, right.At),
            "the right pane resize waited for the reply of the left pane");

        // The chrome has the new layout. Deltas that the mux sent before it
        // read the resizes still have the old size of each pane.
        // Two in a row: the second one does not chain on a shown frame.
        for (var generation = Shown + 1; generation <= Shown + 2; generation++)
        {
            run.Mux.Send(
                run.RenderEventLine(run.Body("p1", oldLeft.Cols, oldLeft.Rows, full: false, generation, generation - 1, "$ " + OldLeftMarker))
                + run.RenderEventLine(run.Body("p2", oldRight.Cols, oldRight.Rows, full: false, generation, generation - 1, "$ " + OldRightMarker)));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => AttachSession.FrameDropsInRow(run.Live, "p1") >= 2
                    && AttachSession.FrameDropsInRow(run.Live, "p2") >= 2,
                TestWait.HangGuard),
            "the render loop did not drop each frame of the old size for its size");

        // Then the full frame of the left pane at its new size and an echo.
        var full = run.Body("p1", resize.Cols, resize.Rows, full: true, generation: Shown + 3, baseGeneration: 0, "$ ");
        var echo = run.Body("p1", resize.Cols, resize.Rows, full: false, generation: Shown + 4, baseGeneration: Shown + 3, "$ " + EchoMarker);
        var sentAt = Stopwatch.GetTimestamp();
        run.Mux.Send(run.RenderEventLine(full) + run.RenderEventLine(echo));
        var paintedAt = await run.Host.WaitForAsync(EchoMarker);
        var waited = Stopwatch.GetElapsedTime(sentAt, paintedAt);
        Assert.True(
            waited < PromptPaint,
            "the echo of the new size waited " + waited.TotalMilliseconds.ToString("0.0") + " ms");

        run.Mux.ReleaseResizeReplies();
        await run.Lane.Idle.WaitAsync(TestWait.HangGuard);
        run.Mux.Send(run.RenderEventLine(
            run.Body("p2", right.Cols, right.Rows, full: true, generation: Shown + 3, baseGeneration: 0, "$ " + NewRightMarker)));
        await run.Host.WaitForAsync(NewRightMarker);

        // The loop handles lines in order. It dropped the frames of the old
        // size, and then painted later frames of both panes. So no old frame
        // can still paint.
        Assert.False(run.Host.Saw(OldLeftMarker), "a frame of the old size painted into the new layout");
        Assert.False(run.Host.Saw(OldRightMarker), "a frame of the old size painted into the new layout");
        // A delta of the old size does not ask the mux for a full frame: the
        // resize posts one.
        Assert.DoesNotContain(
            run.Mux.Lines(),
            line => line.Method == ProtocolMethods.TerminalObserve);
    }

    /// <summary>
    /// The render loop and the resize lane of one attach, on one client. The
    /// two panes show generation <see cref="Shown"/> at the start.
    /// </summary>
    private sealed class ResizeRun : IAsyncDisposable
    {
        private readonly ControlPlaneClient _client;
        private readonly UnixRawTerminal _tty;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private long _seq;

        private ResizeRun(ResizeFrameMux mux, ControlPlaneClient client, AttachLiveState live, HostCapture host, UnixRawTerminal tty)
        {
            Mux = mux;
            _client = client;
            Live = live;
            Host = host;
            _tty = tty;
            Lane = AttachSession.CreateHostResizeLane(() => _client, _gate, live, tty, _stop.Token);
        }

        public ResizeFrameMux Mux { get; }

        public AttachLiveState Live { get; }

        public HostCapture Host { get; }

        public AttachHostResizeLane Lane { get; }

        public static async Task<ResizeRun> StartAsync()
        {
            var mux = ResizeFrameMux.Listen();
            var client = await mux.ConnectAsync();
            var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
            live.RenderSub = "sub_1";
            live.ChromeSeed = new ChromeComputeSeed(MouseTestGeom.RightSplit(), false, null, "p1", 80, 24);
            var host = new HostCapture();
            var tty = new UnixRawTerminal(host, 80, 24);
            var run = new ResizeRun(mux, client, live, host, tty);
            foreach (var pane in live.Chrome!.Panes)
            {
                live.ObservedPaneIds.Add(pane.PaneId);
                live.LastSentPaneSizes[pane.PaneId] = (pane.Content.Cols, pane.Content.Rows);
                Assert.True(AttachSession.TryWriteCells(
                    tty,
                    live,
                    run.Body(pane.PaneId, pane.Content.Cols, pane.Content.Rows, full: true, generation: 1, baseGeneration: 0, "$ "),
                    reanchorBeforePaint: true));
            }

            run._loop = new AttachSession().ReadRenderAsync(
                client, client, run._gate, new SnapshotAssembler(), tty, live, run._stop, run._stop.Token);

            // One delta of each pane through the loop, before the test
            // measures, so the time of a first paint is not in a measure. It
            // writes another row: the host gets only the cells that change.
            foreach (var pane in live.Chrome!.Panes)
            {
                var marker = WarmMarker + pane.PaneId;
                mux.Send(run.RenderEventLine(
                    run.Body(pane.PaneId, pane.Content.Cols, pane.Content.Rows, full: false, Shown, Shown - 1, marker, row: 3)));
                await host.WaitForAsync(marker);
            }

            return run;
        }

        public CellRect Box(string paneId) =>
            Assert.Single(Live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;

        /// <summary>The host window changes size, and SIGWINCH comes.</summary>
        public void Resize(int cols, int rows)
        {
            _tty.PinSize(cols, rows);
            Lane.Request(cols, rows);
        }

        /// <summary>Waits until no pane resize of the render path runs.</summary>
        public async Task WaitForPaneResizesIdleAsync()
        {
            await Live.PaneChromeResizeGate.WaitAsync(TestWait.HangGuard);
            Live.PaneChromeResizeGate.Release();
        }

        public TerminalRenderCellsPayload Body(
            string paneId,
            int cols,
            int rows,
            bool full,
            long generation,
            long baseGeneration,
            string text,
            int row = 1) =>
            new()
            {
                Kind = TerminalRenderCellsPayload.KindCells,
                PaneId = paneId,
                Full = full,
                Reanchor = false,
                GridCols = cols,
                GridRows = rows,
                Generation = generation,
                BaseGeneration = baseGeneration,
                Rows = [new TerminalRenderCellRow { I = row, T = text, C = 0 }],
                Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = row, Visible = true, Shape = 2 },
            };

        public string RenderEventLine(TerminalRenderCellsPayload payload)
        {
            var body = JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
            return EventLine(ProtocolEventTypes.TerminalRender, JsonNode.Parse(body));
        }

        public string ScrollEventLine(string paneId, int maxOffset) =>
            EventLine(
                ProtocolEventTypes.PaneScrollChanged,
                new JsonObject { ["pane_id"] = paneId, ["offset"] = 0, ["max_offset"] = maxOffset });

        private string EventLine(string type, JsonNode? payload)
        {
            var line = new JsonObject
            {
                ["event"] = "runtime.event",
                ["params"] = new JsonObject
                {
                    ["type"] = type,
                    ["payload"] = payload,
                    ["seq"] = Interlocked.Increment(ref _seq),
                },
            };
            return line.ToJsonString() + "\n";
        }

        public async ValueTask DisposeAsync()
        {
            Mux.ReleaseSnapshotReplies();
            Mux.ReleaseResizeReplies();
            await _stop.CancelAsync();
            try
            {
                await _loop.WaitAsync(TestWait.HangGuard).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                or InvalidOperationException or IOException or TimeoutException)
            {
            }

            try
            {
                await Lane.Idle.WaitAsync(TestWait.HangGuard).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            await _client.DisposeAsync();
            Mux.Dispose();
            _tty.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A host TTY sink that keeps the time at which each marker first came.</summary>
    private sealed class HostCapture : Stream
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns the time at which the host got <paramref name="marker"/>.
        /// The write keeps that time, so the wait adds nothing to it.
        /// </summary>
        public async Task<long> WaitForAsync(string marker)
        {
            long? at = null;
            await Poll.UntilAsync(() => (at = SeenAt(marker)) is not null, "the host to get " + marker);
            return at!.Value;
        }

        /// <summary>True when the host got <paramref name="marker"/>.</summary>
        public bool Saw(string marker) => SeenAt(marker) is not null;

        private long? SeenAt(string marker)
        {
            lock (_gate)
            {
                if (_seen.TryGetValue(marker, out var at))
                    return at;
                if (_text.ToString().Contains(marker, StringComparison.Ordinal))
                    return _seen[marker] = Stopwatch.GetTimestamp();
                return null;
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get
            {
                lock (_gate)
                    return _text.Length;
            }
        }

        public override long Position
        {
            get => Length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var at = Stopwatch.GetTimestamp();
            var text = Encoding.UTF8.GetString(buffer);
            lock (_gate)
            {
                _text.Append(text);
                foreach (var marker in new[] { EchoMarker, OldLeftMarker, OldRightMarker, NewRightMarker })
                {
                    if (!_seen.ContainsKey(marker) && text.Contains(marker, StringComparison.Ordinal))
                        _seen[marker] = at;
                }
            }
        }
    }

    /// <summary>One request that the mux read, in read order.</summary>
    private sealed record MuxLine(int Index, long At, string Method, string? Id, string? PaneId, int Cols, int Rows);

    /// <summary>
    /// A Unix-socket mux stand-in. It answers each request at once with the
    /// chrome replies of <see cref="MouseTestGeom.ApplyPort"/>, except
    /// <c>pane.resize</c>, which it holds until <see cref="ReleaseResizeReplies"/>,
    /// and <c>session.snapshot</c>, which the test can hold. The test writes
    /// event lines with <see cref="Send"/>.
    /// </summary>
    private sealed class ResizeFrameMux : IDisposable
    {
        private readonly Socket _listener;
        private readonly TaskCompletionSource _resizeHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<MuxLine> _lines = [];
        private readonly Dictionary<string, long> _repliedAt = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lineSignal = new(0, int.MaxValue);
        private readonly MouseRecordingPort _replies = MouseTestGeom.ApplyPort();
        private readonly object _writeGate = new();
        private TaskCompletionSource _snapshotHold = CompletedHold();
        private NetworkStream? _stream;

        private ResizeFrameMux(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static ResizeFrameMux Listen()
        {
            var path = "/tmp/hypa-resize-frame-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new ResizeFrameMux(listener, path);
        }

        internal async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new NetworkStream(await accept, ownsSocket: true);
            _ = Task.Run(ReadLoopAsync);
            return client;
        }

        /// <summary>The next <c>session.snapshot</c> replies wait for <see cref="ReleaseSnapshotReplies"/>.</summary>
        internal void HoldSnapshotReplies() =>
            Volatile.Write(ref _snapshotHold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal void ReleaseSnapshotReplies() => Volatile.Read(ref _snapshotHold).TrySetResult();

        /// <summary>Sends the held <c>pane.resize</c> replies. Later ones go at once.</summary>
        internal void ReleaseResizeReplies() => _resizeHold.TrySetResult();

        internal void Send(string lines)
        {
            var bytes = Encoding.UTF8.GetBytes(lines);
            lock (_writeGate)
            {
                _stream!.Write(bytes);
                _stream.Flush();
            }
        }

        internal List<MuxLine> Lines()
        {
            lock (_lines)
                return [.. _lines];
        }

        /// <summary>True when the mux sent the reply of <paramref name="id"/> at or before <paramref name="at"/>.</summary>
        internal bool RepliedBefore(string? id, long at)
        {
            lock (_lines)
                return id is not null && _repliedAt.TryGetValue(id, out var sent) && sent <= at;
        }

        internal async Task<MuxLine> WaitForRequestAsync(string method, string? paneId = null)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(TestWait.HangGuard.TotalSeconds * Stopwatch.Frequency);
            while (true)
            {
                lock (_lines)
                {
                    if (_lines.Find(l => l.Method == method
                            && l.Id is not null
                            && (paneId is null || l.PaneId == paneId)) is { } found)
                    {
                        return found;
                    }
                }

                Assert.True(Stopwatch.GetTimestamp() < deadline, "the mux did not read " + method);
                await _lineSignal.WaitAsync(TimeSpan.FromMilliseconds(5));
            }
        }

        private static TaskCompletionSource CompletedHold()
        {
            var done = new TaskCompletionSource();
            done.SetResult();
            return done;
        }

        private async Task ReadLoopAsync()
        {
            var stream = _stream!;
            using var reader = new StreamReader(stream, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } text)
                {
                    var at = Stopwatch.GetTimestamp();
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    string? id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                    var paneId = ParamString(root, "pane_id");
                    var cols = ParamInt(root, "cols");
                    var rows = ParamInt(root, "rows");
                    lock (_lines)
                        _lines.Add(new MuxLine(_lines.Count, at, method, id, paneId, cols, rows));
                    _lineSignal.Release();
                    if (id is null)
                        continue;

                    var raw = idEl.GetRawText();
                    if (method == ProtocolMethods.PaneResize)
                    {
                        _ = ReplyAfterAsync(
                            _resizeHold.Task,
                            id,
                            raw,
                            "{\"ok\":true,\"pane_id\":\"" + paneId + "\",\"cols\":" + cols + ",\"rows\":" + rows + "}");
                        continue;
                    }

                    var result = _replies.Handler!(method, null).GetRawText();
                    if (method == ProtocolMethods.SessionSnapshot)
                    {
                        _ = ReplyAfterAsync(Volatile.Read(ref _snapshotHold).Task, id, raw, result);
                        continue;
                    }

                    Reply(id, raw, result);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            static int ParamInt(JsonElement root, string name) =>
                root.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : 0;

            static string? ParamString(JsonElement root, string name) =>
                root.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
        }

        private async Task ReplyAfterAsync(Task wait, string id, string raw, string result)
        {
            await wait.ConfigureAwait(false);
            Reply(id, raw, result);
        }

        private void Reply(string id, string raw, string result)
        {
            lock (_lines)
                _repliedAt[id] = Stopwatch.GetTimestamp();
            try
            {
                Send("{\"jsonrpc\":\"2.0\",\"id\":" + raw + ",\"result\":" + result + "}\n");
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _listener.Dispose();
            _lineSignal.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
