using System.Diagnostics;
using Hypa.Cli.Attach.Latency;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Release microbench of the always-on meter. It runs only when
/// <c>HYPA_MICROBENCH=1</c>, because a timing bound is not stable on a shared
/// CI runner. Run it with <c>dotnet test -c Release</c>.
/// </summary>
public sealed class AttachLatencyMeterBench(ITestOutputHelper output)
{
    private const int Keys = 200_000;

    [SkippableFact]
    public void Meter_costs_less_than_one_microsecond_per_event()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("HYPA_MICROBENCH") == "1", "Set HYPA_MICROBENCH=1.");
#if DEBUG
        Skip.If(true, "Run the microbench in Release.");
#endif
        var meter = new AttachLatencyMeter();
        var key = "a"u8.ToArray();
        Run(meter, key, 20_000);
        meter.Reset();

        var hopStart = meter.Now();
        var stopwatch = Stopwatch.StartNew();
        var events = Run(meter, key, Keys);
        stopwatch.Stop();
        var nsPerEvent = stopwatch.Elapsed.TotalNanoseconds / events;

        var hopOnly = Stopwatch.StartNew();
        for (var i = 0; i < Keys; i++)
            meter.RecordHop(AttachLatencyHop.Compose, hopStart);
        hopOnly.Stop();
        var nsPerHop = hopOnly.Elapsed.TotalNanoseconds / Keys;

        output.WriteLine($"events={events} ns_per_event={nsPerEvent:0.0} ns_per_hop={nsPerHop:0.0}");
        Assert.Equal(Keys, meter.Report().KeyToPaint.Count);
        Assert.True(nsPerEvent < 1_000, $"{nsPerEvent:0.0} ns per event");
        Assert.True(nsPerHop < 1_000, $"{nsPerHop:0.0} ns per hop");
    }

    /// <summary>
    /// One key through the meter: stdin read, decode hop, send, paint hops,
    /// and the ack close. Returns the event count.
    /// </summary>
    private static int Run(AttachLatencyMeter meter, byte[] key, int keys)
    {
        Span<AttachKeyStamp> stamp = stackalloc AttachKeyStamp[1];
        for (var i = 0; i < keys; i++)
        {
            var readAt = meter.Now();
            meter.NoteStdinRead(key, readAt);
            meter.RecordHop(AttachLatencyHop.Decode, readAt);
            var seq = meter.NextSeq();
            stamp[0] = meter.StampKeys(key);
            meter.NoteSent("p1", seq, stamp);
            meter.RecordHop(AttachLatencyHop.Apply, readAt);
            meter.RecordHop(AttachLatencyHop.HostWrite, readAt);
            meter.NoteInputAck("p1", seq);
        }

        return keys * 7;
    }
}
