using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.UnitTests;

public sealed class ProcessPauseSamplerTests
{
    [Fact]
    public void A_gc_pause_record_names_the_generation_and_the_promoted_bytes()
    {
        using var capture = AttachPathTrace.BeginCapture();
        using var sampler = ProcessPauseSampler.StartIfTracing();
        Assert.NotNull(sampler);

        // A live heap of many small objects makes a full compacting GC take
        // more than the report threshold. Collect until the sampler records
        // one. Each collect is the next step only when no record came yet:
        // the record is the event, and the GC itself sets the pace.
        var live = new object[1_000_000];
        for (var i = 0; i < live.Length; i++)
            live[i] = new byte[32];

        AttachPathRecord? record = null;
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                record = capture.Output.Where(r =>
                    r.Stage == ProcessPauseSampler.StageGcPause && r.Route == "gen2").Cast<AttachPathRecord?>().FirstOrDefault();
                if (record is not null)
                    return true;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                return false;
            },
            TestWait.HangGuard));

        Assert.True(record!.Value.ByteCount > 0);
        Assert.True(record.Value.ElapsedUs >= (long)ProcessPauseSampler.ReportThreshold.TotalMicroseconds);
        GC.KeepAlive(live);
    }
}
