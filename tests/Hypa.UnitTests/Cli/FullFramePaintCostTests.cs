using System.Diagnostics;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The paint cost of a 200x50 host with two panes side by side. The right
/// pane gets bulk output: full bodies with no reanchor (the mux sends them
/// when a body changes all rows) and row shifts with new rows at the bottom.
/// The left pane gets a typing delta after each bulk paint. The test writes
/// the time of each paint step, the allocations and the garbage collections
/// to the test output. It asserts only the paint counters, not the times.
/// </summary>
public sealed class FullFramePaintCostTests(ITestOutputHelper output)
{
    private const int HostCols = 200;
    private const int HostRows = 50;
    private const int WarmUp = 100;
    private const int Iterations = 300;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    [Fact]
    public void Bulk_full_bodies_in_one_pane_paint_through_the_patch_path()
    {
        var (chrome, typing, bulk) = Layout();

        // A first run of each attach warms the JIT. The second run counts.
        Run(chrome, typing, bulk, patchPath: false, shiftEvery: 0);
        Run(chrome, typing, bulk, patchPath: true, shiftEvery: 0);
        var full = Run(chrome, typing, bulk, patchPath: false, shiftEvery: 0);
        var patch = Run(chrome, typing, bulk, patchPath: true, shiftEvery: 0);

        Write("full bodies only", bulk, patch, full);

        // Only the first bodies of each pane have no baseline. Each later
        // bulk body is a full body of the same size, so it takes the patch
        // path, as the typing deltas do.
        Assert.Equal(0, patch.FullFrameFallbacks);
        Assert.Equal(2 * Iterations, patch.Patches);
        Assert.Equal(0, patch.FullComposes);
        Assert.Equal(0, full.Patches);
        Assert.Equal(2 * Iterations, full.FullComposes);
    }

    [Fact]
    public void Bulk_full_bodies_and_row_shifts_in_one_pane_paint_through_the_patch_path()
    {
        var (chrome, typing, bulk) = Layout();

        Run(chrome, typing, bulk, patchPath: false, shiftEvery: 2);
        Run(chrome, typing, bulk, patchPath: true, shiftEvery: 2);
        var full = Run(chrome, typing, bulk, patchPath: false, shiftEvery: 2);
        var patch = Run(chrome, typing, bulk, patchPath: true, shiftEvery: 2);

        Write("full bodies and row shifts", bulk, patch, full);

        Assert.Equal(0, patch.FullFrameFallbacks);
        Assert.Equal(2 * Iterations, patch.Patches);
        Assert.Equal(0, patch.FullComposes);
        Assert.Equal(0, full.Patches);
        Assert.Equal(2 * Iterations, full.FullComposes);
    }

    private static (LayoutChromeGeometry Chrome, CellRect Typing, CellRect Bulk) Layout()
    {
        var chrome = LayoutChromeGeometry.Compute(
            HostCols,
            HostRows,
            MouseTestGeom.RightSplit(),
            false,
            null,
            "p1",
            LayoutChromeGeometry.BareInsets,
            1,
            AttachClientMode.Terminal,
            [("t1", "one", true)]);
        var typing = Assert.Single(chrome.Panes, pane => pane.PaneId == "p1").Content;
        var bulk = Assert.Single(chrome.Panes, pane => pane.PaneId == "p2").Content;
        return (chrome, typing, bulk);
    }

    private void Write(string scenario, CellRect bulk, Measured patch, Measured full)
    {
        output.WriteLine($"{scenario}: host {HostCols}x{HostRows}, bulk pane {bulk.Cols}x{bulk.Rows}, {Iterations} bulk paints and {Iterations} typing deltas");
        output.WriteLine("attach | apply p50/p99 | compose p50/p99 | encode p50/p99 | host_write p50/p99 | bulk paint p50/p99/max | typing paint p50/p99/max | alloc per bulk and typing pair (KiB) | gc 0/1/2 | patch | full_compose | full_frame");
        output.WriteLine(patch.Line("patch path on"));
        output.WriteLine(full.Line("patch path off"));
    }

    private static Measured Run(LayoutChromeGeometry chrome, CellRect typing, CellRect bulk, bool patchPath, int shiftEvery)
    {
        using var tty = new UnixRawTerminal(Stream.Null, HostCols, HostRows);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PaneId = "p1",
            SessionName = "cost",
            Chrome = chrome,
            PatchPaintEnabled = patchPath,
        };
        var random = new Random(7);
        Assert.True(AttachSession.TryWriteCells(tty, live, Typing(typing, 0, 1, "$ "), reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, Bulk(bulk, 0, 1, random), reanchorBeforePaint: true));

        var typed = 1L;
        var bulkGeneration = 1L;
        var bulkTimes = new List<long>(Iterations);
        var typingTimes = new List<long>(Iterations);
        long allocated = 0;
        int gc0 = 0, gc1 = 0, gc2 = 0;
        for (var i = 0; i < WarmUp + Iterations; i++)
        {
            if (i == WarmUp)
            {
                live.Latency.Reset();
                bulkTimes.Clear();
                typingTimes.Clear();
                allocated = GC.GetTotalAllocatedBytes(precise: true);
                gc0 = GC.CollectionCount(0);
                gc1 = GC.CollectionCount(1);
                gc2 = GC.CollectionCount(2);
            }

            var body = shiftEvery > 0 && i % shiftEvery != 0
                ? Shift(bulk, bulkGeneration, bulkGeneration + 1, random, 3 + (i % 5))
                : Bulk(bulk, bulkGeneration, bulkGeneration + 1, random);
            bulkGeneration++;
            var start = Stopwatch.GetTimestamp();
            Assert.True(AttachSession.TryWriteCells(tty, live, body, reanchorBeforePaint: true));
            bulkTimes.Add((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            var text = "$ " + new string('x', 1 + (i % 40));
            start = Stopwatch.GetTimestamp();
            Assert.True(AttachSession.TryWriteCells(tty, live, Typing(typing, typed, ++typed, text), reanchorBeforePaint: true));
            typingTimes.Add((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var report = live.Latency.Report();
        bulkTimes.Sort();
        typingTimes.Sort();
        return new Measured(
            report.Hops[AttachLatencyMeter.HopName(AttachLatencyHop.Apply)],
            report.Hops[AttachLatencyMeter.HopName(AttachLatencyHop.Compose)],
            report.Hops[AttachLatencyMeter.HopName(AttachLatencyHop.Encode)],
            report.Hops[AttachLatencyMeter.HopName(AttachLatencyHop.HostWrite)],
            new Wall(Percentile(bulkTimes, 0.50), Percentile(bulkTimes, 0.99), bulkTimes[^1]),
            new Wall(Percentile(typingTimes, 0.50), Percentile(typingTimes, 0.99), typingTimes[^1]),
            allocated / 1024 / Iterations,
            (GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2),
            live.Latency.Paint.Patches,
            live.Latency.Paint.FullComposes,
            live.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame));
    }

    private static long Percentile(List<long> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    private static string Line(Random random, int length)
    {
        var text = new StringBuilder(length);
        for (var c = 0; c < length; c++)
            text.Append(Alphabet[random.Next(Alphabet.Length)]);
        return text.ToString();
    }

    /// <summary>
    /// A full body with no reanchor, as the mux sends it when a full body is
    /// smaller than the delta: every row has new text. The first body is a
    /// reanchor.
    /// </summary>
    private static TerminalRenderCellsPayload Bulk(CellRect box, long baseGeneration, long generation, Random random)
    {
        var rows = new List<TerminalRenderCellRow>(box.Rows);
        for (var r = 0; r < box.Rows - 1; r++)
            rows.Add(new TerminalRenderCellRow { I = r, T = Line(random, Math.Min(box.Cols, 76)) });

        rows.Add(new TerminalRenderCellRow { I = box.Rows - 1, T = "$ " });
        return new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            Full = true,
            Reanchor = baseGeneration == 0,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = 2, Row = box.Rows - 1, Visible = true },
        };
    }

    /// <summary>
    /// A row shift of <paramref name="shift"/> rows up: the kept rows move,
    /// and the body has the new rows at the bottom and the prompt row.
    /// </summary>
    private static TerminalRenderCellsPayload Shift(CellRect box, long baseGeneration, long generation, Random random, int shift)
    {
        var rows = new List<TerminalRenderCellRow>(shift + 1);
        for (var r = box.Rows - 1 - shift; r < box.Rows - 1; r++)
            rows.Add(new TerminalRenderCellRow { I = r, T = Line(random, Math.Min(box.Cols, 76)) });

        rows.Add(new TerminalRenderCellRow { I = box.Rows - 1, T = "$ " });
        return new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            Shift = shift,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = 2, Row = box.Rows - 1, Visible = true },
        };
    }

    private static TerminalRenderCellsPayload Typing(CellRect box, long baseGeneration, long generation, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p1",
        Full = baseGeneration == 0,
        Reanchor = baseGeneration == 0,
        GridCols = box.Cols,
        GridRows = box.Rows,
        BaseGeneration = baseGeneration,
        Generation = generation,
        Rows = [new TerminalRenderCellRow { I = 0, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = 0, Visible = true },
    };

    private readonly record struct Wall(long P50Us, long P99Us, long MaxUs)
    {
        public override string ToString() => $"{P50Us} / {P99Us} / {MaxUs}";
    }

    private sealed record Measured(
        LatencyHistogramReport Apply,
        LatencyHistogramReport Compose,
        LatencyHistogramReport Encode,
        LatencyHistogramReport HostWrite,
        Wall BulkPaint,
        Wall TypingPaint,
        long AllocatedKiBPerPair,
        (int Gen0, int Gen1, int Gen2) Collections,
        long Patches,
        long FullComposes,
        long FullFrameFallbacks)
    {
        public string Line(string name) =>
            $"{name} | {Apply.P50Us} / {Apply.P99Us} | {Compose.P50Us} / {Compose.P99Us} | {Encode.P50Us} / {Encode.P99Us} | "
            + $"{HostWrite.P50Us} / {HostWrite.P99Us} | {BulkPaint} | {TypingPaint} | {AllocatedKiBPerPair} | "
            + $"{Collections.Gen0}/{Collections.Gen1}/{Collections.Gen2} | {Patches} | {FullComposes} | {FullFrameFallbacks}";
    }
}
