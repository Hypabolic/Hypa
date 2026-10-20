using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Latency;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachLatencyMeterTests
{
    [Fact]
    public void A_key_closes_when_a_painted_frame_acks_its_seq()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        var readAt = meter.Now();
        meter.NoteStdinRead("a"u8, readAt);
        var seq = meter.NextSeq();
        meter.NoteSent("p1", seq, [meter.StampKeys("a"u8)]);

        clock.AdvanceMicroseconds(1_500);
        meter.NoteInputAck("p1", seq);

        var report = meter.Report();
        Assert.Equal(1, report.KeysRead);
        Assert.Equal(1, report.KeysSent);
        Assert.Equal(1, report.KeysAcked);
        Assert.Equal(0, report.OpenKeys);
        Assert.Equal(1, report.KeyToPaint.Count);
        Assert.InRange(report.KeyToPaint.P50Us, 1_400, 1_500);
        Assert.Equal(1_500, report.KeyToPaint.MaxUs);
        Assert.False(report.AckUnavailable);
    }

    [Fact]
    public void A_closed_key_writes_a_trace_record_with_its_pane_and_time()
    {
        using var capture = AttachPathTrace.BeginCapture();
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("a"u8, meter.Now());
        var seq = meter.NextSeq();
        meter.NoteSent("p1", seq, [meter.StampKeys("a"u8)]);

        clock.AdvanceMicroseconds(1_500);
        meter.NoteInputAck("p1", seq);

        var record = Assert.Single(capture.Input, r => r.Stage == AttachPathTrace.StageKeyToPaint);
        Assert.Equal("p1", record.PaneId);
        Assert.InRange(record.ElapsedUs, 1_400, 1_600);
    }

    [Fact]
    public void The_report_has_the_gc_totals_of_the_process()
    {
        var meter = new AttachLatencyMeter(new TickClock());
        var before = meter.Report().Gc!;
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        var after = meter.Report().Gc!;
        Assert.True(after.Gen0Collections > before.Gen0Collections);
        Assert.True(after.AllocatedBytes >= before.AllocatedBytes);

        var json = JsonSerializer.Serialize(meter.Report(), AttachSemanticJsonContext.Default.AttachLatencyReport);
        Assert.Contains("gen0_collections", json, StringComparison.Ordinal);
        Assert.Contains("last_gc_index", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ack_below_the_seq_or_for_another_pane_leaves_the_key_open()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("ab"u8, meter.Now());
        var first = meter.NextSeq();
        meter.NoteSent("p1", first, [meter.StampKeys("a"u8)]);
        var second = meter.NextSeq();
        meter.NoteSent("p1", second, [meter.StampKeys("b"u8)]);

        meter.NoteInputAck("p2", second);
        Assert.Equal(2, meter.Report().OpenKeys);

        meter.NoteInputAck("p1", first);
        var report = meter.Report();
        Assert.Equal(1, report.OpenKeys);
        Assert.Equal(1, report.KeysAcked);

        meter.NoteInputAck("p1", second);
        Assert.Equal(0, meter.Report().OpenKeys);
        Assert.Equal(2, meter.Report().KeyToPaint.Count);
    }

    [Fact]
    public void A_key_closed_observer_gets_the_exact_time_of_each_closed_batch()
    {
        var clock = new TickClock();
        var closed = new List<AttachKeyClosed>();
        var meter = new AttachLatencyMeter(clock, keyClosed: closed.Add);
        var firstRead = meter.Now();
        meter.NoteStdinRead("a"u8, firstRead);
        var first = meter.NextSeq();
        meter.NoteSent("p1", first, [meter.StampKeys("a"u8)]);
        clock.AdvanceMicroseconds(700);
        var secondRead = meter.Now();
        meter.NoteStdinRead("\u001b[D"u8, secondRead);
        var second = meter.NextSeq();
        meter.NoteSent("p1", second, [meter.StampKeys("\u001b[D"u8)]);
        clock.AdvanceMicroseconds(1_234);

        // An ack for another pane or below the seq closes nothing.
        meter.NoteInputAck("p2", second);
        Assert.Empty(closed);

        meter.NoteInputAck("p1", second);
        Assert.Equal(
            [
                new AttachKeyClosed("p1", first, firstRead, 1, 1_934),
                new AttachKeyClosed("p1", second, secondRead, 1, 1_234),
            ],
            closed);
        Assert.Equal(1_934, meter.Report().KeyToPaint.MaxUs);
    }

    [Fact]
    public void Seq_increases_for_the_life_of_the_meter()
    {
        var meter = new AttachLatencyMeter(new TickClock());
        Assert.Equal(1, meter.NextSeq());
        Assert.Equal(2, meter.NextSeq());
        meter.Reset();
        Assert.Equal(3, meter.NextSeq());
    }

    [Fact]
    public void A_key_that_an_ack_closes_after_250_ms_still_counts_as_stale()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("a"u8, meter.Now());
        meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("a"u8)]);

        clock.AdvanceMicroseconds(300_000);
        meter.NoteInputAck("p1", 1);

        var report = meter.Report();
        Assert.Equal(1, report.StaleKey);
        Assert.Equal(1, report.KeysAcked);
    }

    [Fact]
    public void A_key_open_after_250_ms_counts_once_as_stale()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("a"u8, meter.Now());
        meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("a"u8)]);

        clock.AdvanceMicroseconds(249_000);
        Assert.Equal(0, meter.Report().StaleKey);
        clock.AdvanceMicroseconds(2_000);
        Assert.Equal(1, meter.Report().StaleKey);
        Assert.Equal(1, meter.Report().StaleKey);

        meter.NoteInputAck("p1", 1);
        var report = meter.Report();
        Assert.Equal(1, report.StaleKey);
        Assert.Equal(1, report.KeyToPaint.Count);
        Assert.True(report.KeyToPaint.MaxUs >= 250_000);
    }

    [Fact]
    public void A_peer_without_input_ack_reports_ack_unavailable_and_keeps_no_open_key()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("abc"u8, meter.Now());
        meter.NoteSent("p1", seq: null, [meter.StampKeys("abc"u8)]);
        clock.AdvanceMicroseconds(400_000);

        var report = meter.Report();
        Assert.True(report.AckUnavailable);
        Assert.Equal(3, report.KeysRead);
        Assert.Equal(3, report.KeysSent);
        Assert.Equal(0, report.OpenKeys);
        Assert.Equal(0, report.StaleKey);
        Assert.Equal(0, report.KeyToPaint.Count);
    }

    [Fact]
    public void Dropping_open_keys_counts_them_and_stops_the_wait()
    {
        var meter = new AttachLatencyMeter(new TickClock());
        meter.NoteStdinRead("ab"u8, meter.Now());
        meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("ab"u8)]);
        meter.DropOpen();

        var report = meter.Report();
        Assert.Equal(0, report.OpenKeys);
        Assert.Equal(2, report.DroppedKeys);
        meter.NoteInputAck("p1", 1);
        Assert.Equal(0, meter.Report().KeyToPaint.Count);
    }

    [Fact]
    public void The_open_key_limit_drops_the_oldest_batch_without_calling_it_stale()
    {
        var meter = new AttachLatencyMeter(new TickClock(), maxOpenBatches: 2);
        meter.NoteStdinRead("abc"u8, meter.Now());
        for (var i = 0; i < 3; i++)
            meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("a"u8)]);

        var report = meter.Report();
        Assert.Equal(2, report.OpenKeys);
        Assert.Equal(1, report.DroppedKeys);
        Assert.Equal(0, report.StaleKey);
    }

    [Fact]
    public void Reset_clears_histograms_and_counters()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("a"u8, meter.Now());
        meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("a"u8)]);
        meter.RecordHop(AttachLatencyHop.Decode, meter.Now());
        meter.NoteInputAck("p1", 1);

        meter.Reset();
        var report = meter.Report();
        Assert.Equal(0, report.KeysRead);
        Assert.Equal(0, report.KeysSent);
        Assert.Equal(0, report.KeysAcked);
        Assert.Equal(0, report.KeyToPaint.Count);
        Assert.All(report.Hops.Values, hop => Assert.Equal(0, hop.Count));
    }

    [Fact]
    public void Each_hop_has_its_own_histogram_in_the_report()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        var start = meter.Now();
        clock.AdvanceMicroseconds(40);
        meter.RecordHop(AttachLatencyHop.Compose, start);
        meter.RecordHop(AttachLatencyHop.HostWrite, start);

        var report = meter.Report();
        string[] names =
        [
            "decode", "gate-wait-stdin", "gate-wait-render", "paint-wait", "input-wire",
            "client-recv", "client-decode", "apply", "compose", "encode", "host-write", "predicted-paint",
        ];
        Assert.Equal(names.Order(), report.Hops.Keys.Order());
        Assert.Equal(1, report.Hops["compose"].Count);
        Assert.Equal(40, report.Hops["compose"].MaxUs);
        Assert.Equal(1, report.Hops["host-write"].Count);
        Assert.Equal(0, report.Hops["decode"].Count);
    }

    [Theory]
    [InlineData("a", 1)]
    [InlineData("ab c", 4)]
    [InlineData("\u001b[A\u001b[B", 2)]
    [InlineData("\u001bOP", 1)]
    [InlineData("\u001bx", 1)]
    [InlineData("\u001b", 1)]
    [InlineData("é漢", 2)]
    [InlineData("\u001b[200~hi\u001b[201~", 4)]
    public void Keys_count_escape_sequences_and_utf8_as_one_key(string text, int keys)
    {
        Assert.Equal(keys, AttachLatencyMeter.CountKeys(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Histogram_percentiles_stay_within_the_bucket_error()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        for (var us = 1; us <= 1_000; us++)
        {
            var start = meter.Now();
            clock.AdvanceMicroseconds(us);
            meter.RecordHop(AttachLatencyHop.Apply, start);
        }

        var apply = meter.Report().Hops["apply"];
        Assert.Equal(1_000, apply.Count);
        Assert.InRange(apply.P50Us, 500, 500 * 1.07);
        Assert.InRange(apply.P95Us, 950, 950 * 1.07);
        Assert.InRange(apply.P99Us, 990, 1_000);
        Assert.Equal(1_000, apply.MaxUs);
    }

    [Fact]
    public void The_report_serializes_with_snake_case_names()
    {
        var clock = new TickClock();
        var meter = new AttachLatencyMeter(clock);
        meter.NoteStdinRead("a"u8, meter.Now());
        meter.NoteSent("p1", meter.NextSeq(), [meter.StampKeys("a"u8)]);
        clock.AdvanceMicroseconds(800);
        meter.NoteInputAck("p1", 1);

        var json = JsonSerializer.Serialize(
            new AttachSemanticReply
            {
                Action = AttachSemanticActions.LatencyReport,
                Outcome = AttachSemanticOutcomes.Applied,
                Latency = meter.Report() with
                {
                    PredictionCounters = new Dictionary<string, long> { ["made"] = 3, ["reset.paste"] = 1 },
                },
            },
            AttachSemanticJsonContext.Default.AttachSemanticReply);
        using var doc = JsonDocument.Parse(json);
        var latency = doc.RootElement.GetProperty("latency");
        var counters = latency.GetProperty("prediction_counters");
        Assert.Equal(3, counters.GetProperty("made").GetInt64());
        Assert.Equal(1, counters.GetProperty("reset.paste").GetInt64());
        Assert.Equal(1, latency.GetProperty("keys_read").GetInt64());
        Assert.Equal(1, latency.GetProperty("key_to_paint").GetProperty("count").GetInt64());
        Assert.Equal(800, latency.GetProperty("key_to_paint").GetProperty("max_us").GetInt64());
        Assert.True(latency.GetProperty("key_to_paint").TryGetProperty("p99_us", out _));
        Assert.False(latency.GetProperty("ack_unavailable").GetBoolean());
        Assert.Equal(0, latency.GetProperty("stale_key").GetInt64());
        Assert.True(latency.GetProperty("hops").TryGetProperty("gate-wait-render", out _));
    }

    /// <summary>A monotonic clock that moves only when the test moves it.</summary>
    private sealed class TickClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void AdvanceMicroseconds(long us) => Interlocked.Add(ref _ticks, us * 1_000);
    }
}
