using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Latency;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Queued cells deltas apply in order, then the host composes and writes
/// once. Each delta keeps its checks, and its acks commit after the write.
/// </summary>
public sealed class CellsBatchPaintTests
{
    [Fact]
    public void Ten_chained_deltas_paint_with_one_compose_and_one_host_write()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        var paintsBefore = Paints(live);
        var mark = output.Length;

        var batch = new List<TerminalRenderCellsPayload>();
        var text = "$ ";
        for (var g = 2; g <= 11; g++)
        {
            text += (char)('a' + g - 2);
            batch.Add(Delta("p1", box, g - 1, g, text));
        }

        var results = AttachSession.TryWriteCellsBatch(tty, live, batch, reanchorBeforePaint: true);

        Assert.All(results, Assert.True);
        Assert.Equal(paintsBefore + 1, Paints(live));
        Assert.Equal(1, CountWrites(output, mark));
        Assert.Equal(text, RowText(live, box, 0));
        Assert.Equal(11, live.LastAppliedBlit("p1").Generation);
        live.TryGetPaneFrame("p1", out var stored);
        Assert.Equal(11, stored!.Generation);
    }

    [Fact]
    public void Each_delta_in_a_batch_keeps_its_checks_and_commits_after_the_one_write()
    {
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var p1 = Box(live, "p1");
        var p2 = Box(live, "p2");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", p1, 1, "$ ") with { OccupantGeneration = 3 }, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p2", p2, 1, "log") with { OccupantGeneration = 4 }, reanchorBeforePaint: true));
        foreach (var seq in new long[] { 5, 6, 7 })
            live.Latency.NoteSent("p1", seq, [new AttachKeyStamp(live.Latency.Now(), 1)]);
        var mark = output.Length;

        var batch = new List<TerminalRenderCellsPayload>
        {
            Delta("p1", p1, 1, 2, "$ a") with { OccupantGeneration = 3, InputAck = 5 },
            Delta("p1", p1, 2, 3, "$ ab") with { OccupantGeneration = 3, InputAck = 6 },
            // A delta that does not chain is dropped and asks for a reanchor.
            Delta("p1", p1, 99, 100, "$ lost") with { OccupantGeneration = 3 },
            // A delta on the dropped chain is dropped too.
            Delta("p1", p1, 3, 4, "$ abc") with { OccupantGeneration = 3 },
            // A delta of another occupant is dropped.
            Delta("p2", p2, 1, 2, "other") with { OccupantGeneration = 9 },
            // The reanchor body starts a new chain.
            Full("p1", p1, 10, "$ abcd") with { OccupantGeneration = 3, Reanchor = true, InputAck = 7 },
            Delta("p1", p1, 10, 11, "$ abcde") with { OccupantGeneration = 3 },
        };

        var results = AttachSession.TryWriteCellsBatch(tty, live, batch, reanchorBeforePaint: true);

        Assert.Equal([true, true, false, false, false, true, true], results);
        Assert.Equal(1, CountWrites(output, mark));
        Assert.True(live.BlitReanchorPending);
        Assert.Equal("$ abcde", RowText(live, p1, 0));
        Assert.Equal("log", RowText(live, p2, 0));
        Assert.Equal((11L, 3), live.LastAppliedBlit("p1"));
        Assert.Equal(0, live.LastAppliedBlit("p2").Generation);
        Assert.Equal(3, live.Latency.Report().KeysAcked);
    }

    [Fact]
    public void Acks_of_a_batch_wait_for_the_host_write()
    {
        var sink = new FailingStream();
        using var tty = new UnixRawTerminal(sink, 80, 24);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        live.Latency.NoteSent("p1", 5, [new AttachKeyStamp(live.Latency.Now(), 1)]);

        sink.Fail = true;
        Assert.ThrowsAny<IOException>(() => AttachSession.TryWriteCellsBatch(
            tty,
            live,
            [Delta("p1", box, 1, 2, "$ a") with { InputAck = 5 }, Delta("p1", box, 2, 3, "$ ab")],
            reanchorBeforePaint: true));

        Assert.Equal(1, live.LastAppliedBlit("p1").Generation);
        Assert.Equal(0, live.Latency.Report().KeysAcked);
    }

    [SkippableFact]
    public async Task Render_loop_paints_queued_cells_deltas_once()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        using var peer = EventPeer.Listen();
        await using var reader = await peer.ConnectAsync();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var output = new LockedStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var box = Box(live, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        var paintsBefore = Paints(live);
        var mark = output.Length;

        var text = "$ ";
        for (var g = 2; g <= 11; g++)
        {
            text += (char)('a' + g - 2);
            peer.Send(RenderEventLine(Delta("p1", box, g - 1, g, text), seq: g));
        }

        Assert.True(SpinWait.SpinUntil(() => reader.OrderedQueueCount >= 10, TestWait.HangGuard));
        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();
        var loop = new AttachSession().ReadRenderAsync(
            reader, reader, gate, new SnapshotAssembler(), tty, live, stop, stop.Token);
        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => live.LastAppliedBlit("p1").Generation == 11 || loop.IsCompleted, TestWait.HangGuard),
                "the queued deltas were not painted");
            Assert.False(loop.IsCompleted);
            Assert.Equal(text, RowText(live, box, 0));
            Assert.Equal(paintsBefore + 1, Paints(live));
            Assert.Equal(1, CountWrites(output, mark));
        }
        finally
        {
            stop.Cancel();
            try
            {
                await loop.WaitAsync(TestWait.HangGuard);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [SkippableFact]
    public async Task Render_loop_batch_ends_after_a_reanchor_body_and_the_rest_paints_next()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        using var peer = EventPeer.Listen();
        await using var reader = await peer.ConnectAsync();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var output = new LockedStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var box = Box(live, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, "$ "), reanchorBeforePaint: true));
        var paintsBefore = Paints(live);

        // Five deltas, a reanchor body, then five deltas on the new chain.
        var text = "$ ";
        for (var g = 2; g <= 6; g++)
        {
            text += (char)('a' + g - 2);
            peer.Send(RenderEventLine(Delta("p1", box, g - 1, g, text), seq: g));
        }

        text = "$ new";
        peer.Send(RenderEventLine(Full("p1", box, 20, text) with { Reanchor = true }, seq: 7));
        for (var g = 21; g <= 25; g++)
        {
            text += (char)('0' + g - 20);
            peer.Send(RenderEventLine(Delta("p1", box, g - 1, g, text), seq: g));
        }

        Assert.True(SpinWait.SpinUntil(() => reader.OrderedQueueCount >= 11, TestWait.HangGuard));
        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();
        var loop = new AttachSession().ReadRenderAsync(
            reader, reader, gate, new SnapshotAssembler(), tty, live, stop, stop.Token);
        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => live.LastAppliedBlit("p1").Generation == 25 || loop.IsCompleted, TestWait.HangGuard),
                "the queued deltas were not painted");
            Assert.False(loop.IsCompleted);
            Assert.Equal(text, RowText(live, box, 0));
            Assert.Equal(paintsBefore + 2, Paints(live));
        }
        finally
        {
            stop.Cancel();
            try
            {
                await loop.WaitAsync(TestWait.HangGuard);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static long Paints(AttachLiveState live) =>
        live.Latency.Paint.Patches + live.Latency.Paint.FullComposes + live.Latency.Paint.NoOps;

    private static int CountWrites(MemoryStream output, long mark)
    {
        // LockedStream overrides ToArray, so a read during a loop write is safe.
        var bytes = output.ToArray();
        var text = Encoding.UTF8.GetString(bytes, (int)mark, bytes.Length - (int)mark);
        var count = 0;
        for (var i = text.IndexOf(HostBlitEncoder.SyncBegin, StringComparison.Ordinal);
            i >= 0;
            i = text.IndexOf(HostBlitEncoder.SyncBegin, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static CellRect Box(AttachLiveState live, string paneId) =>
        Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;

    private static string RowText(AttachLiveState live, CellRect box, int row)
    {
        var text = new StringBuilder();
        for (var c = 0; c < box.Cols; c++)
            text.Append(live.Host.CellAt(box.Col + c, box.Row + row).Text);
        return text.ToString().TrimEnd();
    }

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

    private static string RenderEventLine(TerminalRenderCellsPayload payload, long seq)
    {
        var body = JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        var line = new JsonObject
        {
            ["event"] = "runtime.event",
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.TerminalRender,
                ["payload"] = JsonNode.Parse(body),
                ["seq"] = seq,
            },
        };
        return line.ToJsonString() + "\n";
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool Fail { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail)
                throw new IOException("host write failed");
            base.Write(buffer, offset, count);
        }
    }

    /// <summary>A host sink that a test thread can read while the loop writes.</summary>
    private sealed class LockedStream : MemoryStream
    {
        private readonly object _gate = new();

        public override long Length
        {
            get
            {
                lock (_gate)
                    return base.Length;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                base.Write(buffer, offset, count);
        }

        public override byte[] ToArray()
        {
            lock (_gate)
                return base.ToArray();
        }
    }

    /// <summary>A Unix-socket mux stand-in that pushes event lines to one client.</summary>
    private sealed class EventPeer : IDisposable
    {
        private readonly Socket _listener;
        private NetworkStream? _stream;

        private EventPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static EventPeer Listen()
        {
            var path = "/tmp/hypa-cells-batch-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new EventPeer(listener, path);
        }

        internal async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new NetworkStream(await accept, ownsSocket: true);
            return client;
        }

        internal void Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream!.Write(bytes);
            _stream.Flush();
        }

        public void Dispose()
        {
            _stream?.Dispose();
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
}
