using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// A host resize holds the control gate while it waits for the mux: the
/// chrome refresh and one <c>pane.resize</c> for each visible pane. On a cube
/// with a long round trip that takes hundreds of milliseconds. A key that the
/// user types during the resize must not wait for it. It must go to the mux
/// at once, and on the wire it must come after the resize request that the
/// client sent before it.
/// <para>
/// The mux stand-in holds the reply of each <c>pane.resize</c> until the
/// test releases it. Thus each check runs while the resize waits.
/// </para>
/// </summary>
public sealed class AttachResizeKeyPathTests
{
    private static readonly TimeSpan PromptSend = TimeSpan.FromMilliseconds(20);

    [SkippableFact]
    public async Task Key_typed_during_a_slow_resize_is_sent_at_once_after_the_resize_request()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        using var mux = SlowResizeMux.Listen();
        await using var client = await mux.ConnectAsync();
        var fixture = Fixture.Start(client);
        try
        {
            // The first key warms the send path, so the timed key below
            // measures the wait and not the first-call cost.
            fixture.Stdin.Push("w"u8.ToArray());
            await mux.WaitForKeysAsync("w");

            var lane = AttachSession.CreateHostResizeLane(
                () => client, fixture.Gate, fixture.Live, fixture.Tty, fixture.Attempt.Token);
            lane.Request(80, 24);
            var resize = await mux.WaitForRequestAsync(ProtocolMethods.PaneResize);
            Assert.Equal(0, fixture.Gate.CurrentCount);

            // A host can send a focus report with a resize. The focus redraw
            // after it needs the control gate that the resize holds.
            fixture.Stdin.Push("\u001b[I"u8.ToArray());
            var typedAt = Stopwatch.GetTimestamp();
            fixture.Stdin.Push("a"u8.ToArray());

            var key = await mux.WaitForKeysAsync("a");
            var waited = Stopwatch.GetElapsedTime(typedAt, key.At);
            Assert.True(
                waited < PromptSend,
                "the key waited " + waited.TotalMilliseconds.ToString("0.0") + " ms behind the resize");
            Assert.True(key.Index > resize.Index, "the key went out before the resize request that came first");
            Assert.False(
                mux.RepliedBefore(resize.Id, key.At),
                "the resize reply came before the key; the test did not hold the resize");

            mux.ReleaseResizeReplies();
            await lane.Idle.WaitAsync(TestWait.HangGuard);
            await fixture.Live.FocusRedraw.WaitAsync(TestWait.HangGuard);
            // The focus redraw still runs. It only does not hold the keys.
            var redraw = await mux.WaitForRequestAsync(ProtocolMethods.TerminalControl);
            Assert.True(redraw.Index > key.Index);
            Assert.Equal(1, fixture.Gate.CurrentCount);
        }
        finally
        {
            await fixture.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Focus_report_and_key_keep_their_order_and_do_not_wait_for_a_slow_resize()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        using var mux = SlowResizeMux.Listen();
        await using var client = await mux.ConnectAsync();
        var fixture = Fixture.Start(client, focusReporting: true);
        try
        {
            fixture.Stdin.Push("w"u8.ToArray());
            await mux.WaitForKeysAsync("w");

            var lane = AttachSession.CreateHostResizeLane(
                () => client, fixture.Gate, fixture.Live, fixture.Tty, fixture.Attempt.Token);
            lane.Request(80, 24);
            var resize = await mux.WaitForRequestAsync(ProtocolMethods.PaneResize);

            // The pane asked for focus reports. The report and the key that
            // follows it reach the pane in the order of the host.
            var typedAt = Stopwatch.GetTimestamp();
            fixture.Stdin.Push("\u001b[Ia"u8.ToArray());

            var keys = await mux.WaitForKeysAsync("\u001b[Ia");
            var waited = Stopwatch.GetElapsedTime(typedAt, keys.At);
            Assert.True(
                waited < PromptSend,
                "the focus report and the key waited " + waited.TotalMilliseconds.ToString("0.0") + " ms behind the resize");
            Assert.True(keys.Index > resize.Index);
            Assert.False(mux.RepliedBefore(resize.Id, keys.At));
            // The report went as a notification on the key path, not as a
            // call that waits for a reply.
            Assert.DoesNotContain(
                mux.Lines(),
                line => line.Method == ProtocolMethods.PaneSendKeys && line.Id is not null);

            mux.ReleaseResizeReplies();
            await lane.Idle.WaitAsync(TestWait.HangGuard);
            await fixture.Live.FocusRedraw.WaitAsync(TestWait.HangGuard);
        }
        finally
        {
            await fixture.StopAsync();
        }
    }

    [Fact]
    public async Task A_burst_of_host_resizes_applies_the_running_size_and_then_only_the_newest()
    {
        using var stop = new CancellationTokenSource();
        var applied = new List<(int Cols, int Rows)>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new AttachHostResizeLane(
            async (cols, rows, _) =>
            {
                lock (applied)
                    applied.Add((cols, rows));
                if (firstStarted.TrySetResult())
                    await releaseFirst.Task.ConfigureAwait(false);
            },
            stop.Token);

        lane.Request(100, 30);
        await firstStarted.Task.WaitAsync(TestWait.HangGuard);
        lane.Request(101, 30);
        lane.Request(102, 31);
        lane.Request(103, 32);
        releaseFirst.SetResult();
        await lane.Idle.WaitAsync(TestWait.HangGuard);

        lock (applied)
            Assert.Equal(new[] { (100, 30), (103, 32) }, applied);
    }

    [Fact]
    public async Task A_failed_resize_does_not_stop_the_next_one()
    {
        using var stop = new CancellationTokenSource();
        var applied = new List<int>();
        var faults = new List<Exception>();
        var lane = new AttachHostResizeLane(
            (cols, _, _) =>
            {
                lock (applied)
                    applied.Add(cols);
                return cols == 90
                    ? Task.FromException(new InvalidOperationException("resize failed"))
                    : Task.CompletedTask;
            },
            stop.Token,
            ex =>
            {
                lock (faults)
                    faults.Add(ex);
            });

        lane.Request(90, 30);
        await lane.Idle.WaitAsync(TestWait.HangGuard);
        lane.Request(91, 30);
        await lane.Idle.WaitAsync(TestWait.HangGuard);

        lock (applied)
            Assert.Equal(new[] { 90, 91 }, applied);
        lock (faults)
            Assert.IsType<InvalidOperationException>(Assert.Single(faults));
    }

    [Fact]
    public async Task A_stopped_lane_applies_no_new_size()
    {
        using var stop = new CancellationTokenSource();
        var applied = 0;
        var lane = new AttachHostResizeLane(
            (_, _, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            },
            stop.Token);

        stop.Cancel();
        lane.Request(80, 24);
        await lane.Idle.WaitAsync(TestWait.HangGuard);

        Assert.Equal(0, Volatile.Read(ref applied));
    }

    /// <summary>The attach input loop of one test, with its live state and input sender.</summary>
    private sealed class Fixture
    {
        private Fixture(
            AttachLiveState live,
            UnixRawTerminal tty,
            SemaphoreSlim gate,
            CancellationTokenSource linked,
            CancellationTokenSource attempt,
            ChannelAttachStdin stdin,
            AttachInputSender sender,
            Task input)
        {
            Live = live;
            Tty = tty;
            Gate = gate;
            Linked = linked;
            Attempt = attempt;
            Stdin = stdin;
            Sender = sender;
            Input = input;
        }

        internal AttachLiveState Live { get; }

        internal UnixRawTerminal Tty { get; }

        internal SemaphoreSlim Gate { get; }

        internal CancellationTokenSource Linked { get; }

        internal CancellationTokenSource Attempt { get; }

        internal ChannelAttachStdin Stdin { get; }

        private AttachInputSender Sender { get; }

        private Task Input { get; }

        internal static Fixture Start(ControlPlaneClient client, bool focusReporting = false)
        {
            var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
            if (focusReporting)
                live.SetPaneFrame(FocusReportingFrame(live, "p1"));
            var tty = new UnixRawTerminal(Stream.Synchronized(new MemoryStream()), 80, 24);
            var gate = new SemaphoreSlim(1, 1);
            var linked = new CancellationTokenSource();
            var attempt = new CancellationTokenSource();
            var stdin = new ChannelAttachStdin();
            var sender = new AttachInputSender(client, () => (live.PaneId, live.InputLease));
            live.InputSender = sender;
            var input = new AttachSession().ReadInputForTests(
                tty, live, client, gate, linked, stdin, attempt.Token);
            return new Fixture(live, tty, gate, linked, attempt, stdin, sender, input);
        }

        internal async Task StopAsync()
        {
            await Attempt.CancelAsync();
            try
            {
                await Input.WaitAsync(TestWait.HangGuard).ConfigureAwait(false);
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

            await Sender.DisposeAsync();
            await Stdin.DisposeAsync();
            Tty.Dispose();
            Gate.Dispose();
            Linked.Dispose();
            Attempt.Dispose();
        }

        private static AssembledSnapshot FocusReportingFrame(AttachLiveState live, string paneId)
        {
            var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;
            var rows = new List<IReadOnlyList<AssembledCell>>(box.Rows);
            for (var r = 0; r < box.Rows; r++)
            {
                var row = new List<AssembledCell>(box.Cols);
                for (var c = 0; c < box.Cols; c++)
                    row.Add(new AssembledCell(" ", 1, false, AssembledStyle.Default));
                rows.Add(row);
            }

            using var doc = JsonDocument.Parse("""{"modes":{"focus_reporting":true}}""");
            return new AssembledSnapshot(
                paneId, box.Cols, box.Rows, "ghostty", "main", rows, doc.RootElement.Clone(), AssembledCursor.Default);
        }
    }

    /// <summary>A stdin source that returns pushed chunks and times out when idle.</summary>
    private sealed class ChannelAttachStdin : IAttachStdinSource
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public bool ThreadAlive => false;

        public void Push(byte[] chunk) => Assert.True(_chunks.Writer.TryWrite(chunk));

        public async ValueTask<AttachStdinRead> ReadAsync(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0)
                timeout.CancelAfter(timeoutMs);
            try
            {
                var chunk = await _chunks.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                chunk.CopyTo(buffer, 0);
                return AttachStdinRead.Bytes(chunk.Length, Stopwatch.GetTimestamp());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return AttachStdinRead.Timeout;
            }
        }

        public ValueTask DisposeAsync()
        {
            _chunks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One line that the mux read, in read order.</summary>
    private sealed record MuxLine(int Index, long At, string Method, string? Id, string? Keys);

    /// <summary>
    /// A Unix-socket mux stand-in. It answers each request at once with the
    /// chrome replies of <see cref="MouseTestGeom.ApplyPort"/>, except
    /// <c>pane.resize</c>, which it holds until <see cref="ReleaseResizeReplies"/>.
    /// It keeps each line with its read time.
    /// </summary>
    private sealed class SlowResizeMux : IDisposable
    {
        private readonly Socket _listener;
        private readonly TaskCompletionSource _resizeHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<MuxLine> _lines = [];
        private readonly Dictionary<string, long> _repliedAt = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lineSignal = new(0, int.MaxValue);
        private readonly MouseRecordingPort _replies = MouseTestGeom.ApplyPort();
        private NetworkStream? _stream;

        private SlowResizeMux(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static SlowResizeMux Listen()
        {
            var path = "/tmp/hypa-resize-key-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new SlowResizeMux(listener, path);
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

        /// <summary>Sends the held <c>pane.resize</c> replies. Later ones go at once.</summary>
        internal void ReleaseResizeReplies() => _resizeHold.TrySetResult();

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

        internal Task<MuxLine> WaitForRequestAsync(string method) =>
            WaitForAsync(line => line.Method == method && line.Id is not null);

        /// <summary>
        /// Waits for the <c>pane.send_keys</c> lines whose bytes, joined in
        /// read order, end with <paramref name="text"/>. Returns the line
        /// that completed the text.
        /// </summary>
        internal Task<MuxLine> WaitForKeysAsync(string text) =>
            WaitForAsync(_ => KeysSoFar().EndsWith(text, StringComparison.Ordinal), last: true);

        private string KeysSoFar()
        {
            var sb = new StringBuilder();
            lock (_lines)
            {
                foreach (var line in _lines)
                {
                    if (line.Method == ProtocolMethods.PaneSendKeys && line.Keys is not null)
                        sb.Append(line.Keys);
                }
            }

            return sb.ToString();
        }

        private async Task<MuxLine> WaitForAsync(Func<MuxLine, bool> match, bool last = false)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(TestWait.HangGuard.TotalSeconds * Stopwatch.Frequency);
            while (true)
            {
                lock (_lines)
                {
                    if (last)
                    {
                        if (_lines.Count > 0 && match(_lines[^1]))
                            return _lines.FindLast(l => l.Method == ProtocolMethods.PaneSendKeys)!;
                    }
                    else if (_lines.Find(l => match(l)) is { } found)
                    {
                        return found;
                    }
                }

                var left = deadline - Stopwatch.GetTimestamp();
                Assert.True(left > 0, "the mux did not read the expected line");
                await _lineSignal.WaitAsync(TimeSpan.FromMilliseconds(50));
            }
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
                    string? keys = null;
                    if (method == ProtocolMethods.PaneSendKeys
                        && root.TryGetProperty("params", out var p)
                        && p.TryGetProperty("data", out var data))
                    {
                        keys = Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!));
                    }

                    lock (_lines)
                        _lines.Add(new MuxLine(_lines.Count, at, method, id, keys));
                    _lineSignal.Release();
                    if (id is null)
                        continue;

                    var raw = idEl.GetRawText();
                    if (method == ProtocolMethods.PaneResize)
                    {
                        var cols = ParamInt(root, "cols");
                        var rows = ParamInt(root, "rows");
                        _ = ReplyLaterAsync(id, raw, "{\"cols\":" + cols + ",\"rows\":" + rows + "}");
                        continue;
                    }

                    var result = _replies.Handler!(method, null).GetRawText();
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
                root.TryGetProperty("params", out var p) && p.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
        }

        private async Task ReplyLaterAsync(string id, string raw, string result)
        {
            await _resizeHold.Task.ConfigureAwait(false);
            Reply(id, raw, result);
        }

        private void Reply(string id, string raw, string result)
        {
            var stream = _stream!;
            lock (_lines)
                _repliedAt[id] = Stopwatch.GetTimestamp();
            try
            {
                lock (stream)
                {
                    stream.Write(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + raw + ",\"result\":" + result + "}\n"));
                    stream.Flush();
                }
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
            _resizeHold.TrySetResult();
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
