using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

public sealed class VtCellsEncoderTests
{
    private readonly ITestOutputHelper _output;

    public VtCellsEncoderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Encoder_returns_a_full_reanchor_payload_with_wire_bytes()
    {
        var encoder = new VtCellsEncoder();
        var frame = Frame('x', cols: 4, rows: 2, generation: 3);
        var encoding = encoder.Encode(frame, baseline: null, "p1", generation: 3, baseGeneration: 1, occupantGeneration: 2);
        Assert.NotNull(encoding);
        Assert.True(encoding.Value.Payload.Full);
        Assert.True(encoding.Value.Payload.Reanchor);
        Assert.Equal(encoding.Value.PayloadUtf8.Length, encoding.Value.Payload.WireBytes);
        Assert.True(encoding.Value.Payload.WireBytes > 0);

        var parsed = JsonSerializer.Deserialize(
            encoding.Value.PayloadUtf8,
            ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.NotNull(parsed);
        Assert.Equal("p1", parsed!.PaneId);
        Assert.True(parsed.Full);
        Assert.True(parsed.Reanchor);
        Assert.Equal(3, parsed.Generation);
        Assert.False(
            Encoding.UTF8.GetString(encoding.Value.PayloadUtf8)
                .Contains("\"wire_bytes\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_encode_p99_does_not_exceed_per_observer_encode()
    {
        var encoder = new VtCellsEncoder();
        var frame = Frame('x', cols: 80, rows: 24, generation: 1);
        const int observers = 4;
        const int warmup = 20;
        const int samples = 200;

        for (var i = 0; i < warmup; i++)
        {
            var warm = encoder.Encode(frame, baseline: null, "p1", i + 1, 0, 1);
            Assert.NotNull(warm);
            FormatObservers(warm.Value, observers);
        }

        var sharedTicks = new long[samples];
        var repeatedTicks = new long[samples];
        for (var i = 0; i < samples; i++)
        {
            var generation = i + warmup + 1L;
            var sharedWatch = Stopwatch.StartNew();
            var shared = encoder.Encode(frame, baseline: null, "p1", generation, 0, 1);
            Assert.NotNull(shared);
            FormatObservers(shared.Value, observers);
            sharedWatch.Stop();
            sharedTicks[i] = sharedWatch.ElapsedTicks;

            var repeatedWatch = Stopwatch.StartNew();
            for (var o = 0; o < observers; o++)
            {
                var repeated = encoder.Encode(frame, baseline: null, "p1", generation, 0, 1);
                Assert.NotNull(repeated);
                FormatObservers(repeated.Value, observerCount: 1, idOffset: o);
            }

            repeatedWatch.Stop();
            repeatedTicks[i] = repeatedWatch.ElapsedTicks;
        }

        var sharedP50Us = TicksToMicroseconds(Percentile(sharedTicks, 0.50));
        var sharedP99Us = TicksToMicroseconds(Percentile(sharedTicks, 0.99));
        var priorP50Us = TicksToMicroseconds(Percentile(repeatedTicks, 0.50));
        var priorP99Us = TicksToMicroseconds(Percentile(repeatedTicks, 0.99));
        _output.WriteLine(
            $"shared p50={sharedP50Us:F1} us p99={sharedP99Us:F1} us; prior p50={priorP50Us:F1} us p99={priorP99Us:F1} us");
        Assert.True(
            sharedP99Us <= priorP99Us,
            $"shared p99 {sharedP99Us:F1} us exceeded prior p99 {priorP99Us:F1} us (shared p50 {sharedP50Us:F1} us, prior p50 {priorP50Us:F1} us)");
    }

    [Fact]
    public void Encoder_returns_null_for_a_frame_with_no_packable_row()
    {
        var encoder = new VtCellsEncoder();
        var frame = new VtFrame(
            "p1",
            0,
            0,
            Array.Empty<IReadOnlyList<VtCellView>>(),
            VtFrameCursor.None,
            new VtFrameModes(false, true, false, false, "none", false),
            0,
            1,
            1);
        Assert.Null(encoder.Encode(frame, baseline: null, "p1", 1, 0, 1));
    }

    [Fact]
    public void Delta_encoding_sets_full_false_and_reanchor_false()
    {
        var encoder = new VtCellsEncoder();
        var baseline = Frame('x', cols: 8, rows: 2, generation: 1);
        var current = WithCell(baseline, 0, 3, 'z', generation: 2);
        var encoding = encoder.Encode(current, baseline, "p1", 2, 1, 1);
        Assert.NotNull(encoding);
        Assert.False(encoding.Value.Payload.Full);
        Assert.False(encoding.Value.Payload.Reanchor);
    }

    [Fact]
    public void Full_encoding_keeps_full_and_reanchor_true()
    {
        var encoder = new VtCellsEncoder();
        var frame = Frame('x', cols: 4, rows: 2, generation: 3);
        var encoding = encoder.Encode(frame, baseline: null, "p1", 3, 0, 1);
        Assert.NotNull(encoding);
        Assert.True(encoding.Value.Payload.Full);
        Assert.True(encoding.Value.Payload.Reanchor);
    }

    [Fact]
    public void Delta_payload_carries_the_baseline_generation()
    {
        var encoder = new VtCellsEncoder();
        var baseline = Frame('x', cols: 8, rows: 2, generation: 11);
        var current = WithCell(baseline, 1, 1, 'q', generation: 12);
        var encoding = encoder.Encode(current, baseline, "p1", 12, baseline.Generation, 1);
        Assert.NotNull(encoding);
        Assert.Equal(11, encoding.Value.Payload.BaseGeneration);
        var parsed = JsonSerializer.Deserialize(
            encoding.Value.PayloadUtf8,
            ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.NotNull(parsed);
        Assert.Equal(11, parsed!.BaseGeneration);
        Assert.Contains("\"base_generation\":11", Encoding.UTF8.GetString(encoding.Value.PayloadUtf8), StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_frame_returns_no_encoding()
    {
        var encoder = new VtCellsEncoder();
        var frame = Frame('x', cols: 8, rows: 2, generation: 4);
        Assert.Null(encoder.Encode(frame, frame, "p1", 5, 4, 1));
    }

    [Fact]
    public void Delta_wire_bytes_are_far_below_the_full_frame_bytes()
    {
        var encoder = new VtCellsEncoder();
        var baseline = StyledFrame('x', cols: 80, rows: 24, generation: 1);
        var current = WithCell(baseline, 5, 9, 'z', generation: 2);
        var full = encoder.Encode(current, baseline: null, "p1", 2, 0, 1);
        var delta = encoder.Encode(current, baseline, "p1", 2, 1, 1);
        Assert.NotNull(full);
        Assert.NotNull(delta);
        Assert.False(delta.Value.Payload.Full);
        Assert.False(delta.Value.Payload.Reanchor);
        Assert.Equal(1, delta.Value.Payload.ChangedCells);
        var deltaRow = Assert.Single(delta.Value.Payload.Rows!);
        Assert.Equal(5, deltaRow.I);
        Assert.Equal(9, deltaRow.C);
        Assert.Equal("z", deltaRow.T);
        _output.WriteLine(
            $"one-cell delta {delta.Value.PayloadUtf8.Length} bytes; full {full.Value.PayloadUtf8.Length} bytes");
        Assert.True(
            delta.Value.PayloadUtf8.Length * 10 < full.Value.PayloadUtf8.Length,
            $"delta {delta.Value.PayloadUtf8.Length} bytes was not under one tenth of full {full.Value.PayloadUtf8.Length} bytes");
    }

    private static VtFrame StyledFrame(char ch, int cols, int rows, long generation)
    {
        var cells = new IReadOnlyList<VtCellView>[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = new VtCellView[cols];
            for (var c = 0; c < cols; c++)
            {
                row[c] = new VtCellView(
                    ch.ToString(), 1, false, "#AABBCC", "#112233", false, false, false, false, false, false, false);
            }

            cells[r] = row;
        }

        return new VtFrame(
            "p1",
            cols,
            rows,
            cells,
            new VtFrameCursor(0, 0, true, 0),
            new VtFrameModes(false, true, false, false, "none", false),
            0,
            1,
            generation);
    }

    private static VtFrame WithCell(VtFrame src, int row, int col, char ch, long generation)
    {
        var cells = new IReadOnlyList<VtCellView>[src.Rows];
        for (var r = 0; r < src.Rows; r++)
        {
            var rowCells = new VtCellView[src.Cols];
            for (var c = 0; c < src.Cols; c++)
                rowCells[c] = src.CellAt(r, c);
            cells[r] = rowCells;
        }

        ((VtCellView[])cells[row])[col] = new VtCellView(
            ch.ToString(), 1, false, null, null, false, false, false, false, false, false, false);
        return new VtFrame(
            src.PaneId,
            src.Cols,
            src.Rows,
            cells,
            src.Cursor,
            src.Modes,
            src.ViewportOrigin,
            src.OccupantGeneration,
            generation,
            src.Provider,
            src.ActiveScreen);
    }

    private static VtFrame Frame(char ch, int cols, int rows, long generation)
    {
        var cells = new IReadOnlyList<VtCellView>[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = new VtCellView[cols];
            for (var c = 0; c < cols; c++)
                row[c] = new VtCellView(ch.ToString(), 1, false, null, null, false, false, false, false, false, false, false);
            cells[r] = row;
        }

        return new VtFrame(
            "p1",
            cols,
            rows,
            cells,
            new VtFrameCursor(0, 0, true, 0),
            new VtFrameModes(false, true, false, false, "none", false),
            0,
            1,
            generation);
    }

    private static void FormatObservers(LiveCellsEncoding encoding, int observerCount, int idOffset = 0)
    {
        var record = new RuntimeEventRecord
        {
            Seq = encoding.Payload.Generation,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = "",
            PayloadUtf8 = encoding.PayloadUtf8,
            Lane = WriterLaneNames.Ordered,
        };
        for (var i = 0; i < observerCount; i++)
            _ = EventSubscriptionHub.FormatRuntimeEventUtf8(record, "sub_" + (idOffset + i));
    }

    private static long Percentile(long[] values, double p)
    {
        var sorted = (long[])values.Clone();
        Array.Sort(sorted);
        var index = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double TicksToMicroseconds(long ticks) =>
        ticks * 1_000_000.0 / Stopwatch.Frequency;
}
