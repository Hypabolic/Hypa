using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// One host writer owns fd 1. A paint gives its encoded frame to that writer
/// and releases the paint lock. The key feed takes only the key engine lock.
/// Thus a stalled host terminal does not delay the keys.
/// <para>
/// A stalled host write stays held until the test releases it. So a step
/// that waited for the host terminal would not end, and each check runs
/// while the write is held.
/// </para>
/// </summary>
public sealed class HostWriterKeyFeedTests
{
    [Fact]
    public async Task Key_feed_does_not_wait_for_a_blocked_host_write()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        var box = Box(live, "p1");
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();

        // Warm the feed and the send path once, so the first-call cost does
        // not count.
        await AttachSession.FeedKeysAfterControlGateAsync(live, [0x20], gate, CancellationToken.None);
        await AttachSession.DispatchKeysUnderGateAsync(tty, live, [0x20], port, gate, linked, CancellationToken.None);
        port.Calls.Clear();

        host.StallNextWrite();
        var paint = Task.Run(() => AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        Assert.True(host.Stalled.Wait(TimeSpan.FromSeconds(5)), "the host write did not start");

        // The paint gave its frame to the host writer and returned. Only the
        // writer thread waits for the host terminal.
        await Poll.GuardAsync(paint, "the paint to return while the host write is held");
        Assert.True(await paint);
        Assert.True(host.InStall);

        var waitsBefore = live.PaintWaitsForTests;
        var watch = Stopwatch.StartNew();
        var (events, _) = await AttachSession.FeedKeysAfterControlGateAsync(live, [0x20], gate, CancellationToken.None);
        watch.Stop();

        Assert.True(host.InStall, "the feed ended after the host write");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(5), $"the feed took {watch.Elapsed.TotalMilliseconds} ms");
        var send = Assert.Single(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.Equal([0x20], send.Bytes!);
        Assert.Equal(waitsBefore, live.PaintWaitsForTests);

        // The keys reach the pane send during the stall too.
        await AttachSession.DispatchKeysUnderGateAsync(tty, live, [0x20], port, gate, linked, CancellationToken.None);
        Assert.True(host.InStall, "the send ended after the host write");
        var call = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
        Assert.Equal(Convert.ToBase64String([0x20]), call.Params?["data"]?.GetValue<string>());

        // After the stall the host gets the frame.
        host.EndStall();
        await tty.WhenHostOutputWrittenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(HostBlitEncoder.SyncBegin, host.Text, StringComparison.Ordinal);
        Assert.Equal(1, live.LastAppliedBlit("p1").Generation);
    }

    [Fact]
    public async Task Key_feed_does_not_wait_for_the_paint_lock()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        using var gate = new SemaphoreSlim(1, 1);
        await AttachSession.FeedKeysAfterControlGateAsync(live, [0x20], gate, CancellationToken.None);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // A paint that composes holds the paint lock and not the key engine lock.
        var painter = Task.Run(() => live.WithFramePaint(() =>
        {
            held.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        var waitsBefore = live.PaintWaitsForTests;
        var watch = Stopwatch.StartNew();
        var (events, _) = await AttachSession.FeedKeysAfterControlGateAsync(live, [(byte)'a'], gate, CancellationToken.None);
        watch.Stop();
        var stillHeld = !release.IsSet;
        release.Set();
        await painter;

        Assert.True(stillHeld);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(5), $"the feed took {watch.Elapsed.TotalMilliseconds} ms");
        var send = Assert.Single(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.Equal([(byte)'a'], send.Bytes!);
        Assert.Equal(waitsBefore, live.PaintWaitsForTests);
    }

    [Fact]
    public async Task Feed_uses_the_input_modes_of_the_newest_focused_frame()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");
        using var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        using var gate = new SemaphoreSlim(1, 1);
        var up = Encoding.ASCII.GetBytes("\u001b[A");

        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ ") with { ApplicationCursor = true }, reanchorBeforePaint: true));
        var (on, _) = await AttachSession.FeedKeysAfterControlGateAsync(live, [.. up], gate, CancellationToken.None);
        Assert.Equal(Encoding.ASCII.GetBytes("\u001bOA"), Assert.Single(on, e => e.Kind == KeyEngineEventKind.SendPaneBytes).Bytes);

        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 2, "$ ") with { ApplicationCursor = false }, reanchorBeforePaint: true));
        var (off, _) = await AttachSession.FeedKeysAfterControlGateAsync(live, [.. up], gate, CancellationToken.None);
        Assert.Equal(up, Assert.Single(off, e => e.Kind == KeyEngineEventKind.SendPaneBytes).Bytes);
    }

    [Fact]
    public async Task A_write_during_a_stalled_paint_goes_to_the_host_after_the_frame()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");

        host.StallNextWrite();
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ frame"), reanchorBeforePaint: true));
        Assert.True(host.Stalled.Wait(TimeSpan.FromSeconds(5)));

        // A second writer does not write next to the paint. Its bytes go
        // into the queue of the one host writer, after the frame. The frame
        // write is held, so a write that waited for it would not return.
        // The write runs on its own task, so that fault fails at the hang
        // guard and the test still ends the stall.
        await Task.Run(() => tty.WriteBytes("<mark>")).WaitAsync(TestWait.HangGuard);
        Assert.True(host.InStall, "the write waited for the host terminal");

        host.EndStall();
        await tty.WhenHostOutputWrittenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var text = host.Text;
        var frameEnd = text.IndexOf(HostBlitEncoder.SyncEnd, StringComparison.Ordinal);
        Assert.True(frameEnd > 0);
        Assert.Contains("frame", text[..frameEnd], StringComparison.Ordinal);
        Assert.True(text.IndexOf("<mark>", StringComparison.Ordinal) > frameEnd);
        Assert.Equal(0, tty.HostBytesPending);
    }

    [Fact]
    public async Task Restore_writes_the_queued_frame_before_the_restore_bytes()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        tty.EnterRaw();
        tty.EnterClientOverlay();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");

        host.StallNextWrite();
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ frame"), reanchorBeforePaint: true));
        Assert.True(host.Stalled.Wait(TimeSpan.FromSeconds(5)));

        // The restore waits for the held frame write.
        Thread? restorer = null;
        var restoring = Task.Run(() =>
        {
            Volatile.Write(ref restorer, Thread.CurrentThread);
            tty.Restore();
        });
        TestWait.UntilBlockedOrDone(() => Volatile.Read(ref restorer), restoring);
        Assert.False(restoring.IsCompleted, "the restore did not wait for the queued frame");
        host.EndStall();
        await restoring.WaitAsync(TestWait.HangGuard);

        Assert.False(host.InStall);
        Assert.Equal(0, tty.HostBytesPending);
        var text = host.Text;
        var frameEnd = text.IndexOf(HostBlitEncoder.SyncEnd, StringComparison.Ordinal);
        var restore = text.LastIndexOf(SnapshotPainter.RestoreSequence, StringComparison.Ordinal);
        Assert.True(frameEnd > 0);
        Assert.True(restore > frameEnd);
    }

    [Fact]
    public async Task A_failed_host_write_ends_the_attach_and_fails_the_next_paint()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        await tty.WhenHostOutputWrittenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // The host terminal is gone (EIO). The paint queued its bytes before
        // the writer thread found that.
        host.FailWrites = true;
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, "$ a"), reanchorBeforePaint: true));

        await Assert.ThrowsAsync<TtyDisconnectException>(
            () => tty.HostWriteFaulted.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<TtyDisconnectException>(
            () => tty.WhenHostOutputWrittenAsync(CancellationToken.None));
        Assert.Throws<TtyDisconnectException>(
            () => AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 3, "$ ab"), reanchorBeforePaint: true));
        Assert.Throws<TtyDisconnectException>(() => tty.WriteBytes("x"));
    }

    [SkippableFact]
    public async Task Keys_reach_the_input_sender_during_a_blocked_host_write()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        using var mux = InputPeer.Listen();
        await using var client = await mux.ConnectAsync();
        await using var sender = new AttachInputSender(client, () => ("p1", "lease-in"));
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");
        using var gate = new SemaphoreSlim(1, 1);

        host.StallNextWrite();
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        Assert.True(host.Stalled.Wait(TimeSpan.FromSeconds(5)));

        var (events, _) = await AttachSession.FeedKeysAfterControlGateAsync(live, [0x20], gate, CancellationToken.None);
        var send = Assert.Single(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.True(await sender.EnqueueAsync(send.Bytes!, CancellationToken.None));

        // The mux gets the key while the host write still waits.
        var line = await mux.ReadLineAsync().WaitAsync(TestWait.HangGuard);
        Assert.True(host.InStall, "the key reached the mux after the host write");
        Assert.Contains(ProtocolMethods.PaneSendKeys, line, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String([0x20]), line, StringComparison.Ordinal);
        host.EndStall();
    }

    [Fact]
    public async Task Host_write_wait_ends_after_the_earlier_bytes_while_later_writes_continue()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        host.StallNextWrite();
        tty.WriteBytes("first");
        Assert.True(host.Stalled.Wait(TimeSpan.FromSeconds(5)));

        var wait = tty.WhenHostOutputWrittenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        // Another writer adds bytes before the host takes each write, so
        // the queue is never empty.
        // The bytes go from another thread, so a write that waited for the
        // host cannot hold the writer thread on itself.
        var hooks = 0;
        host.BeforeWrite = () =>
        {
            Interlocked.Increment(ref hooks);
            try
            {
                if (stop.IsCancellationRequested)
                    return;
                var more = Task.Run(() => tty.WriteBytes("more"));
                Assert.True(more.Wait(TestWait.HangGuard), "a write waited for the host writer");
            }
            finally
            {
                Interlocked.Decrement(ref hooks);
            }
        };
        tty.WriteBytes("more");
        host.EndStall();
        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("first", host.Text, StringComparison.Ordinal);
        }
        finally
        {
            // The restore at test exit holds the terminal write lock while it
            // waits for the writer thread. A hook that adds bytes needs that
            // lock, so wait until no hook runs.
            stop.Cancel();
            await Poll.UntilAsync(() => Volatile.Read(ref hooks) == 0, "the write hook to end");
        }
    }

    [Fact]
    public async Task Writer_thread_records_the_host_write_with_the_route_of_the_caller()
    {
        using var host = new StalledHost();
        using var tty = new UnixRawTerminal(host.Write);
        using var endStall = host.EndStallAtExit();
        using var capture = AttachPathTrace.BeginCapture();

        using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode, paneId: "p1"))
            tty.WriteBytes("abc");
        await tty.WhenHostOutputWrittenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var record = Assert.Single(
            capture.Records,
            r => r.Stage == AttachPathTrace.StageHostWrite);
        Assert.Equal(AttachPathTrace.RouteHostMode, record.Route);
        Assert.Equal("p1", record.PaneId);
        Assert.Equal(3, record.ByteCount);
    }

    private static CellRect Box(AttachLiveState live, string paneId) =>
        Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;

    private static TerminalRenderCellsPayload Full(string paneId, CellRect box, long generation, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = paneId,
        Full = true,
        GridCols = box.Cols,
        GridRows = box.Rows,
        Generation = generation,
        Rows = [new TerminalRenderCellRow { I = 0, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true },
    };

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        string text) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = false,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Rows = [new TerminalRenderCellRow { I = 0, T = text }],
            Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true },
        };

    /// <summary>A mux end of a control socket that reads the lines of the client.</summary>
    private sealed class InputPeer : IDisposable
    {
        private readonly Socket _listener;
        private StreamReader? _reader;

        private InputPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static InputPeer Listen()
        {
            var path = "/tmp/hypa-host-writer-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new InputPeer(listener, path);
        }

        internal async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _reader = new StreamReader(new NetworkStream(await accept, ownsSocket: true), Encoding.UTF8);
            return client;
        }

        internal async Task<string> ReadLineAsync() =>
            await _reader!.ReadLineAsync() ?? "";

        public void Dispose()
        {
            _reader?.Dispose();
            _listener.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A host terminal behind the native writer seam. It records each
    /// write. It can stall one write until the test ends the stall, as a slow
    /// terminal does, or fail every write with EIO.
    /// </summary>
    private sealed class StalledHost : IDisposable
    {
        private readonly object _gate = new();
        private readonly MemoryStream _bytes = new();
        private readonly ManualResetEventSlim _endStall = new();
        private bool _stallNext;
        private volatile bool _inStall;

        public ManualResetEventSlim Stalled { get; } = new();

        public bool InStall => _inStall;

        public volatile bool FailWrites;

        /// <summary>Runs on the writer thread before each write.</summary>
        public Action? BeforeWrite { get; set; }

        public string Text
        {
            get
            {
                lock (_gate)
                    return Encoding.UTF8.GetString(_bytes.ToArray());
            }
        }

        /// <summary>The next write waits until <see cref="EndStall"/>.</summary>
        public void StallNextWrite()
        {
            lock (_gate)
                _stallNext = true;
        }

        public void EndStall() => _endStall.Set();

        /// <summary>
        /// Ends the stall when the test exits. Declare it after the terminal,
        /// so it runs first: the terminal restore writes to this host.
        /// </summary>
        public IDisposable EndStallAtExit() => new StallEnd(this);

        private sealed class StallEnd(StalledHost host) : IDisposable
        {
            public void Dispose() => host.EndStall();
        }

        public (nint n, int errno) Write(ReadOnlySpan<byte> bytes)
        {
            if (FailWrites)
                return (-1, 5);
            bool stall;
            lock (_gate)
            {
                stall = _stallNext;
                _stallNext = false;
            }

            BeforeWrite?.Invoke();
            if (stall)
            {
                _inStall = true;
                Stalled.Set();
                Assert.True(_endStall.Wait(TestWait.HangGuard), "the test did not end the host stall");
                _inStall = false;
            }

            lock (_gate)
                _bytes.Write(bytes);
            return (bytes.Length, 0);
        }

        public void Dispose()
        {
            _endStall.Set();
            Stalled.Dispose();
            _bytes.Dispose();
        }
    }
}
