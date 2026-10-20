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
/// The attach client side of the row shift and of the full frame that the
/// mux sends only because it is smaller than the delta.
/// </summary>
public sealed class RowShiftClientTests
{
    // No chrome text contains these runs of letters.
    private const string FirstMarker = "zqxj";
    private const string SecondMarker = "vqkw";

    [Fact]
    public void Each_hello_offers_row_shift()
    {
        var geometry = new AttachGeometry { Columns = 80, Rows = 24, CellWidthPx = 8, CellHeightPx = 16, GeometryRevision = 1 };
        Assert.Contains(
            AttachEndpointProtocol.RowShiftCapability,
            AttachEndpointPreflight.BuildHello("c1", geometry).OptionalCapabilities!);
        Assert.Contains(
            AttachEndpointProtocol.RowShiftCapability,
            AttachEndpointPreflight.BuildHello("c1", geometry, renderCredit: true).OptionalCapabilities!);
        Assert.DoesNotContain(AttachEndpointProtocol.RowShiftCapability, AttachEndpointProtocol.RequiredCapabilities);
    }

    [Fact]
    public void A_shift_moves_the_kept_rows_and_blanks_the_emptied_rows()
    {
        var full = Body(full: true, generation: 1, rows: ["a", "b", "c", "d"]);
        var frame = VtCellUnpacker.Apply(full, previous: null);
        var up = Body(full: false, generation: 2, rows: [], shift: 1) with
        {
            BaseGeneration = 1,
            Rows = [new TerminalRenderCellRow { I = 3, T = "e", C = 0 }],
        };
        frame = VtCellUnpacker.Apply(up, frame);
        Assert.Equal(["b", "c", "d", "e"], Text(frame));

        var down = Body(full: false, generation: 3, rows: [], shift: -2) with { BaseGeneration = 2 };
        frame = VtCellUnpacker.Apply(down, frame);
        Assert.Equal(["", "", "b", "c"], Text(frame));
    }

    [Fact]
    public void A_shift_without_a_kept_grid_of_the_same_size_is_a_chain_break()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == "p1").Content;
        var full = PaneBody(box, full: true, generation: 1, text: "$ ");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        // The shift is the full height: no kept row is left to move.
        var bad = full with
        {
            Full = false,
            Reanchor = false,
            BaseGeneration = 1,
            Generation = 2,
            Shift = box.Rows,
            Rows = [],
        };
        Assert.False(AttachSession.TryWriteCells(tty, live, bad, reanchorBeforePaint: true));
        Assert.Equal(0, live.LastAppliedBlit("p1").Generation);
        Assert.True(live.ChainReanchors.IsOpen("p1"));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MinValue + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(-1_000_000)]
    public void A_shift_outside_the_grid_is_a_chain_break_with_no_exception(int shift)
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == "p1").Content;
        var full = PaneBody(box, full: true, generation: 1, text: "$ ");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        var bad = full with
        {
            Full = false,
            Reanchor = false,
            BaseGeneration = 1,
            Generation = 2,
            Shift = shift,
            Rows = [],
        };
        Assert.False(AttachSession.TryWriteCells(tty, live, bad, reanchorBeforePaint: true));
        Assert.Equal(0, live.LastAppliedBlit("p1").Generation);
        Assert.True(live.ChainReanchors.IsOpen("p1"));
    }

    [Theory]
    [InlineData(int.MinValue, new[] { "", "", "", "" })]
    [InlineData(int.MaxValue, new[] { "", "", "", "" })]
    [InlineData(-4, new[] { "", "", "", "" })]
    [InlineData(-3, new[] { "", "", "", "a" })]
    public void A_shift_of_the_grid_height_or_more_blanks_each_row(int shift, string[] expected)
    {
        var frame = VtCellUnpacker.Apply(Body(full: true, generation: 1, rows: ["a", "b", "c", "d"]), previous: null);
        frame = VtCellUnpacker.Apply(Body(full: false, generation: 2, rows: [], shift: shift) with { BaseGeneration = 1 }, frame);
        Assert.Equal(expected, Text(frame));
    }

    [SkippableFact]
    public async Task A_full_frame_that_is_not_a_reanchor_starts_no_control_round_trip()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var run = await RenderRun.StartAsync();
        var smaller = run.Full with
        {
            Reanchor = false,
            Generation = 2,
            BaseGeneration = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ " + FirstMarker }],
        };
        run.Peer.Send(RenderEventLine(smaller, seq: 1));
        Assert.True(run.WaitPainted(FirstMarker), "the full frame did not paint");

        // Observe work came while the frame painted. The loop does not take
        // the held control gate for it after the frame: it reads the next line.
        run.Peer.Send(RenderEventLine(run.Delta(generation: 3, baseGeneration: 2, SecondMarker), seq: 2));
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref run.Live.RenderLoopReads) >= 2, TestWait.HangGuard),
            "the loop did not read the next line while the control gate was held");
        Assert.Equal("p2", run.Live.PendingObservePaneId);
    }

    [SkippableFact]
    public async Task A_reanchor_frame_still_starts_the_control_round_trip()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var run = await RenderRun.StartAsync();
        var reanchor = run.Full with
        {
            Reanchor = true,
            Generation = 2,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ " + FirstMarker }],
        };
        run.Peer.Send(RenderEventLine(reanchor, seq: 1));
        Assert.True(run.WaitPainted(FirstMarker), "the reanchor frame did not paint");

        // After the reanchor frame, the loop waits for the control gate to
        // run the observe work. It does not read the next line.
        run.Peer.Send(RenderEventLine(run.Delta(generation: 3, baseGeneration: 2, SecondMarker), seq: 2));
        Assert.Equal(1, Volatile.Read(ref run.Live.RenderLoopReads));
        Assert.False(run.Painted(SecondMarker));

        // Release the gate. The loop then sends the observe call, and the
        // peer does not answer it. So the loop stays in that call, and the
        // read count is the count from before the gate. A loop that read the
        // next line first would show two reads here.
        run.ReleaseGate();
        await Poll.UntilAsync(
            () => run.Peer.Methods().Contains(ProtocolMethods.TerminalObserve),
            "the observe call after the gate");
        Assert.Equal(1, Volatile.Read(ref run.Live.RenderLoopReads));
        Assert.False(run.Painted(SecondMarker));
    }

    private static string[] Text(AssembledSnapshot frame) =>
        Enumerable.Range(0, frame.Rows).Select(row => VtCellUnpacker.RowText(frame, row).TrimEnd()).ToArray();

    private static TerminalRenderCellsPayload Body(bool full, long generation, string[] rows, int shift = 0) =>
        new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            Full = full,
            Reanchor = full,
            GridCols = 4,
            GridRows = 4,
            Generation = generation,
            Shift = shift,
            Rows = rows.Select((text, i) => new TerminalRenderCellRow { I = i, T = text }).ToArray(),
            Cursor = new TerminalRenderCursorPayload { Col = 0, Row = 0, Visible = true },
        };

    private static TerminalRenderCellsPayload PaneBody(CellRect box, bool full, long generation, string text) =>
        new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            Full = full,
            Reanchor = full,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = generation,
            Rows = [new TerminalRenderCellRow { I = 0, T = text }],
            Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true, Shape = 2 },
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

    /// <summary>
    /// A running render loop with a painted first frame and the control gate
    /// held by the test. Painting the first marker queues observe work.
    /// </summary>
    private sealed class RenderRun : IAsyncDisposable
    {
        private readonly CountingStream _output;
        private readonly UnixRawTerminal _tty;
        private readonly ControlPlaneClient _reader;
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private long _mark;
        private bool _gateHeld;

        private RenderRun(EventPeer peer, ControlPlaneClient reader, AttachLiveState live, CountingStream output, UnixRawTerminal tty, TerminalRenderCellsPayload full)
        {
            Peer = peer;
            _reader = reader;
            Live = live;
            _output = output;
            _tty = tty;
            Full = full;
        }

        public EventPeer Peer { get; }

        public AttachLiveState Live { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The first full frame, generation 1.</summary>
        public TerminalRenderCellsPayload Full { get; }

        public static async Task<RenderRun> StartAsync()
        {
            var peer = EventPeer.Listen();
            var reader = await peer.ConnectAsync();
            var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
            var output = new CountingStream();
            var tty = new UnixRawTerminal(output, 80, 24);
            var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == "p1").Content;
            var full = PaneBody(box, full: true, generation: 1, text: "$ ");
            Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
            live.RenderSub = "sub_1";
            Assert.False(AttachSession.HasPendingObserveWork(live));
            // Observe work comes from another thread while the first marker
            // paints: after the loop checked for work at the read, and before
            // it ends the line.
            output.OnWrite = text =>
            {
                if (text.Contains(FirstMarker, StringComparison.Ordinal))
                    live.PendingObservePaneId ??= "p2";
            };
            var run = new RenderRun(peer, reader, live, output, tty, full);
            await run.Gate.WaitAsync();
            run._gateHeld = true;
            run._mark = output.BytesWritten;
            run._loop = new AttachSession().ReadRenderAsync(
                reader, reader, run.Gate, new SnapshotAssembler(), tty, live, run._stop, run._stop.Token);
            return run;
        }

        public TerminalRenderCellsPayload Delta(long generation, long baseGeneration, string marker) =>
            Full with
            {
                Full = false,
                Reanchor = false,
                Generation = generation,
                BaseGeneration = baseGeneration,
                Rows = [new TerminalRenderCellRow { I = 1, T = marker, C = 0 }],
            };

        /// <summary>The test gives up the control gate. The loop can take it.</summary>
        public void ReleaseGate()
        {
            _gateHeld = false;
            Gate.Release();
        }

        public bool Painted(string marker) =>
            Encoding.UTF8.GetString(_output.Snapshot(_mark)).Contains(marker, StringComparison.Ordinal);

        public bool WaitPainted(string marker) =>
            SpinWait.SpinUntil(() => Painted(marker) || _loop.IsCompleted, TestWait.HangGuard) && Painted(marker);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_gateHeld)
                ReleaseGate();
            try
            {
                await _loop.WaitAsync(TestWait.HangGuard).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                or InvalidOperationException or IOException or TimeoutException)
            {
            }

            await _reader.DisposeAsync();
            Peer.Dispose();
            _tty.Dispose();
            Gate.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A host TTY sink that other threads can read while the loop writes.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _inner = new();

        public Action<string>? OnWrite { get; set; }

        public long BytesWritten
        {
            get
            {
                lock (_gate)
                    return _inner.Length;
            }
        }

        public byte[] Snapshot(long from)
        {
            lock (_gate)
                return _inner.ToArray()[(int)from..];
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
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
            lock (_gate)
                _inner.Write(buffer);
            OnWrite?.Invoke(Encoding.UTF8.GetString(buffer));
        }
    }

    /// <summary>
    /// A Unix-socket mux stand-in that pushes event lines to one client. It
    /// keeps the method of each request line and answers none.
    /// </summary>
    private sealed class EventPeer : IDisposable
    {
        private readonly Socket _listener;
        private readonly List<string> _methods = [];
        private NetworkStream? _stream;

        private EventPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static EventPeer Listen()
        {
            var path = "/tmp/hypa-row-shift-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
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
            _ = Task.Run(ReadRequestsAsync);
            return client;
        }

        internal string[] Methods()
        {
            lock (_methods)
                return [.. _methods];
        }

        internal void Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream!.Write(bytes);
            _stream.Flush();
        }

        private async Task ReadRequestsAsync()
        {
            try
            {
                using var reader = new StreamReader(_stream!, leaveOpen: true);
                while (await reader.ReadLineAsync() is { } text)
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("method", out var method)
                        && method.GetString() is { } name)
                    {
                        lock (_methods)
                            _methods.Add(name);
                    }
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
