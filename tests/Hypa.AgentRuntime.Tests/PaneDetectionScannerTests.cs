using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PaneDetectionScannerTests
{
    [Fact]
    public void Mark_shortens_an_armed_identified_recheck()
    {
        var time = new ManualTimeProvider();
        var scans = new List<string>();
        using var scanner = new PaneDetectionScanner(time, (id, _) => scans.Add(id));

        scanner.ScheduleRecheck("p1", 1, TimeSpan.FromSeconds(5));
        scanner.Mark("p1", 1);
        time.Advance(PaneDetectionScanner.DetectionTick);

        Assert.Equal(["p1"], scans);
    }

    [Fact]
    public void Identified_recheck_fires_after_five_seconds_without_mark()
    {
        var time = new ManualTimeProvider();
        var scans = new List<string>();
        using var scanner = new PaneDetectionScanner(time, (id, _) => scans.Add(id));

        scanner.ScheduleRecheck("p1", 1, TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(scans);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["p1"], scans);
    }
}
