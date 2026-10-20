using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The per-event attach trace is Debug for frequent events. At the default
/// level a following pane writes no attach event line, and a stalled log
/// writer does not delay the event apply, the paint of a cells delta, or the
/// terminal restore at the end of the attach.
/// </summary>
public sealed class AttachEventTraceLevelTests(ITestOutputHelper output)
{
    private const int Events = 100;
    private const string PaintMarker = "zqxj";
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Scroll_events_write_no_attach_event_line_at_the_default_level()
    {
        var sink = new CapturingProcessLogSink(ProcessLogLevel.Information);
        var live = Live(sink);

        await ApplyScrollEventsAsync(live, Events);

        Assert.DoesNotContain(sink.Records, record =>
            record.Event is ProcessLogEvents.AttachEventReceived or ProcessLogEvents.AttachEventOutcome);
        Assert.True(live.TryGetPaneScrollMetrics("p2", out var offset, out var maxOffset, out _));
        Assert.Equal(0, offset);
        Assert.Equal(1000 + Events - 1, maxOffset);
    }

    [Fact]
    public async Task Scroll_events_write_both_attach_event_lines_at_debug()
    {
        var sink = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var live = Live(sink);

        await ApplyScrollEventsAsync(live, Events);

        var received = sink.Records.Where(record =>
            record.Event == ProcessLogEvents.AttachEventReceived
            && record.EventType == ProtocolEventTypes.PaneScrollChanged).ToArray();
        var applied = sink.Records.Where(record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PaneScrollChanged
            && record.Outcome == ProcessLogEvents.OutcomeApplied).ToArray();
        Assert.Equal(Events, received.Length);
        Assert.Equal(Events, applied.Length);
        Assert.All(received, record => Assert.Equal(ProcessLogLevel.Debug, record.Level));
        Assert.All(received, record => Assert.Equal("p2", record.PaneId));
    }

    [Theory]
    [InlineData(ProtocolEventTypes.PaneScrollChanged, ProcessLogLevel.Debug)]
    [InlineData(ProtocolEventTypes.PaneOutputMatched, ProcessLogLevel.Debug)]
    [InlineData(ProtocolEventTypes.PaneMetadataUpdated, ProcessLogLevel.Debug)]
    [InlineData(ProtocolEventTypes.ResourceChanged, ProcessLogLevel.Debug)]
    [InlineData("not.a.known.type", ProcessLogLevel.Debug)]
    [InlineData(ProtocolEventTypes.PaneLifecycle, ProcessLogLevel.Information)]
    [InlineData(ProtocolEventTypes.LayoutUpdated, ProcessLogLevel.Information)]
    [InlineData(ProtocolEventTypes.PaneAgentStatusChanged, ProcessLogLevel.Information)]
    [InlineData(ProtocolEventTypes.LeaseChanged, ProcessLogLevel.Information)]
    [InlineData(ProtocolEventTypes.ClientWindowTitleChanged, ProcessLogLevel.Information)]
    [InlineData(ProtocolEventTypes.PopupLifecycle, ProcessLogLevel.Information)]
    public void Each_event_type_has_one_trace_level(string eventType, ProcessLogLevel expected) =>
        Assert.Equal(expected, AttachProcessLog.EventTraceLevel(eventType));

    [Fact]
    public async Task A_stalled_log_writer_does_not_delay_the_scroll_event_apply()
    {
        var inner = new GatedSink();
        using var sink = new QueuedProcessLogSink(inner);
        var live = Live(sink);
        try
        {
            await ApplyScrollEventsAsync(live, 1);
            Assert.True(inner.Entered.Wait(HangGuard), "the log writer did not start");

            var apply = Stopwatch.StartNew();
            await ApplyScrollEventsAsync(live, Events, first: 1);
            var applyTime = apply.Elapsed;

            // Each event was applied while the writer was still blocked.
            Assert.False(inner.Gate.IsSet);
            Assert.Empty(inner.Records);
            Assert.True(live.TryGetPaneScrollMetrics("p2", out _, out var maxOffset, out _));
            Assert.Equal(1000 + Events, maxOffset);
            output.WriteLine($"{Events} scroll events applied in {applyTime.TotalMilliseconds:F2} ms during a stalled log writer");
        }
        finally
        {
            inner.Gate.Set();
        }

        Assert.True(sink.Flush(HangGuard));
        Assert.Equal(2 * (Events + 1), inner.Records.Count(record =>
            record.EventType == ProtocolEventTypes.PaneScrollChanged));
    }

    /// <summary>
    /// The log writer is blocked in the inner sink on the attach's own
    /// "admitted" line of the baseline frame. While it is blocked, the render
    /// loop traces events of the other pane and then applies a cells delta of
    /// the focused pane. The delta is on the host before the test opens the
    /// gate, so the paint did not wait for the log writer. The time is only
    /// printed: the causal check is the proof, and a wall-clock bound would
    /// fail on a loaded test host.
    /// </summary>
    [Fact]
    public async Task A_stalled_log_writer_does_not_delay_the_paint_of_a_focused_pane_cells_delta()
    {
        const int otherPaneEvents = 20;

        // Warm up the paint path, so that the measured time has no JIT cost.
        {
            var (warmLive, warmPort, warmTty, warmCapture) = PaintLive();
            using (warmTty)
            {
                var warmBox = FocusedBox(warmLive);
                await RenderAsync(warmLive, warmPort, warmTty, BaselineCells(warmBox));
                await RenderAsync(warmLive, warmPort, warmTty, EchoDelta(warmBox));
                Assert.Contains(PaintMarker, Encoding.UTF8.GetString(warmCapture.Snapshot(0)), StringComparison.Ordinal);
            }
        }

        var inner = new GatedSink();
        using var sink = new QueuedProcessLogSink(inner, drainTimeout: HangGuard);
        var (live, port, tty, capture) = PaintLive();
        live.ProcessLog = sink;
        using (tty)
        {
            var box = FocusedBox(live);
            TimeSpan paintTime;
            try
            {
                // The baseline frame writes the "admitted" line. The writer takes it and blocks.
                await RenderAsync(live, port, tty, BaselineCells(box));
                Assert.True(inner.Entered.Wait(HangGuard), "the log writer did not start");

                for (var index = 0; index < otherPaneEvents; index++)
                {
                    using var ev = ScrollEvent(2000 + index);
                    await AttachSession.HandleRenderEventAsync(
                        ev.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
                }

                var before = capture.Length;
                Assert.DoesNotContain(PaintMarker, Encoding.UTF8.GetString(capture.Snapshot(0)), StringComparison.Ordinal);
                var paint = Stopwatch.StartNew();
                await RenderAsync(live, port, tty, EchoDelta(box));
                paintTime = paint.Elapsed;

                // The delta is on the host, and the log writer is still blocked.
                Assert.Contains(PaintMarker, Encoding.UTF8.GetString(capture.Snapshot(before)), StringComparison.Ordinal);
                Assert.Equal(2L, live.LastAppliedBlit("p1").Generation);
                Assert.False(inner.Gate.IsSet);
                Assert.Empty(inner.Records);
            }
            finally
            {
                inner.Gate.Set();
            }

            output.WriteLine(
                $"cells delta applied and painted in {paintTime.TotalMilliseconds:F3} ms during a log writer stall (target under 5 ms)");
        }

        // The render loop queued its lines during the stall. The writer writes them after it.
        Assert.True(sink.Flush(HangGuard));
        var records = inner.Records;
        Assert.Equal(ProcessLogEvents.AttachObserve, records[0].Event);
        Assert.Equal("p1", records[0].PaneId);
        Assert.Equal(2 * otherPaneEvents, records.Count(record =>
            record.EventType == ProtocolEventTypes.PaneScrollChanged && record.PaneId == "p2"));
    }

    /// <summary>
    /// At the end of the attach, the terminal restore comes before the log
    /// drain. With the log writer blocked, the restore bytes reach the host
    /// while the drain still waits.
    /// </summary>
    [Fact]
    public async Task The_attach_end_restores_the_terminal_before_it_drains_a_stalled_log()
    {
        var inner = new GatedSink();
        var sink = new QueuedProcessLogSink(inner, drainTimeout: HangGuard);
        var capture = new LockedCapture();
        var tty = new UnixRawTerminal(capture, 80, 24);
        tty.EnterRaw();
        Assert.True(tty.IsRaw);
        var raw = capture.Length;

        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.AttachObserve,
            Subsystem = ProcessLogEvents.SubsystemAttach,
            Outcome = ProcessLogEvents.OutcomeOk,
            Ts = DateTimeOffset.UnixEpoch,
            Pid = 1,
            Level = ProcessLogLevel.Information,
        });
        Assert.True(inner.Entered.Wait(HangGuard), "the log writer did not start");

        var end = Task.Run(() => AttachSession.RestoreTerminalThenDrainLog(null, tty, sink));
        try
        {
            var restored = SpinWait.SpinUntil(() => capture.Length > raw || end.IsCompleted, HangGuard);
            Assert.True(restored, "the terminal restore did not write");
            Assert.True(capture.Length > raw);
            Assert.Contains(SnapshotPainter.EnableLineWrap, Encoding.UTF8.GetString(capture.Snapshot(raw)), StringComparison.Ordinal);

            // The restore is on the host, and the drain still waits for the writer.
            Assert.False(inner.Gate.IsSet);
            Assert.False(end.IsCompleted);
            Assert.Empty(inner.Records);
        }
        finally
        {
            inner.Gate.Set();
        }

        await end.WaitAsync(HangGuard);
        Assert.Single(inner.Records);
        Assert.False(sink.IsEnabled(ProcessLogLevel.Warning));
    }

    private static async Task ApplyScrollEventsAsync(AttachLiveState live, int count, int first = 0)
    {
        var port = new SilentPort();
        var assembler = new SnapshotAssembler();
        for (var index = first; index < first + count; index++)
        {
            using var ev = ScrollEvent(1000 + index);
            await AttachSession.HandleRenderEventAsync(
                ev.RootElement, port, assembler, null, live, CancellationToken.None);
        }
    }

    private static JsonDocument ScrollEvent(int maxOffset) =>
        JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneScrollChanged,
                ["payload"] = new JsonObject
                {
                    ["pane_id"] = "p2",
                    ["offset"] = 0,
                    ["max_offset"] = maxOffset,
                },
            },
        }.ToJsonString());

    private static AttachLiveState Live(IProcessLogSink sink)
    {
        var port = new SilentPort();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", ""),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            AttachClientId = "cli_trace",
            SessionName = "trace",
            ProcessLog = sink,
        };
    }

    private static (AttachLiveState Live, MouseRecordingPort Port, UnixRawTerminal Tty, LockedCapture Capture) PaintLive()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.AttachClientId = "cli_paint";
        live.SessionName = "paint";
        var capture = new LockedCapture();
        return (live, port, new UnixRawTerminal(capture, 80, 24), capture);
    }

    private static CellRect FocusedBox(AttachLiveState live) =>
        Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == "p1").Content;

    private static TerminalRenderCellsPayload BaselineCells(CellRect box) =>
        new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            Full = true,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ " }],
            Cursor = new TerminalRenderCursorPayload { Col = 2, Row = 0, Visible = true, Shape = 2 },
        };

    private static TerminalRenderCellsPayload EchoDelta(CellRect box) =>
        BaselineCells(box) with
        {
            Full = false,
            BaseGeneration = 1,
            Generation = 2,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ " + PaintMarker }],
            Cursor = new TerminalRenderCursorPayload { Col = 2 + PaintMarker.Length, Row = 0, Visible = true, Shape = 2 },
        };

    private static async Task RenderAsync(
        AttachLiveState live,
        IAttachCommandPort port,
        UnixRawTerminal tty,
        TerminalRenderCellsPayload cells)
    {
        var body = JsonSerializer.Serialize(cells, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        using var ev = JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.TerminalRender,
                ["payload"] = JsonNode.Parse(body),
            },
        }.ToJsonString());
        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
    }

    /// <summary>A host output capture that a second thread can read while the attach writes.</summary>
    private sealed class LockedCapture : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _inner = new();

        public override long Length
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

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
                _inner.Write(buffer);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // Keep the bytes readable after the terminal is disposed.
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class GatedSink : IProcessLogSink
    {
        private readonly ConcurrentQueue<ProcessLogRecord> _records = new();

        public ManualResetEventSlim Gate { get; } = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);

        public IReadOnlyList<ProcessLogRecord> Records => _records.ToArray();

        public bool Disabled => false;

        public string? Path => null;

        public bool IsEnabled(ProcessLogLevel level) => true;

        public void Write(ProcessLogRecord record)
        {
            Entered.Set();
            Gate.Wait(TimeSpan.FromMinutes(2));
            _records.Enqueue(record);
        }
    }
}
