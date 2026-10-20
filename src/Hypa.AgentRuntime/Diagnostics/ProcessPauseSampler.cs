using System.Diagnostics;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Opt-in diagnostic for <see cref="AttachPathTrace"/>. A dedicated thread
/// wakes every <see cref="Period"/>. It writes a <c>gc-pause</c> record when
/// the process GC pause total grew by <see cref="ReportThreshold"/> or more,
/// and a <c>thread-stall</c> record when it woke <see cref="StallThreshold"/>
/// or more late. Both records use the trace wall clock, so a pause can be
/// joined to the stages of the same process and of other processes.
/// A <c>gc-pause</c> record has the highest generation collected in the
/// period as its route (<c>gen0</c>, <c>gen1</c>, <c>gen2</c>, with
/// <c>-bgc</c> when the last GC was a background GC) and the bytes the last
/// GC promoted as its byte count.
/// </summary>
public sealed class ProcessPauseSampler : IDisposable
{
    public const string StageGcPause = "gc-pause";
    public const string StageThreadStall = "thread-stall";

    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan ReportThreshold = TimeSpan.FromMilliseconds(2);
    public static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(10);

    private readonly Thread _thread;
    private volatile bool _stopped;

    private ProcessPauseSampler()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "hypa-pause-sampler",
        };
        _thread.Start();
    }

    /// <summary>Starts a sampler when the attach path trace is on. Null otherwise.</summary>
    public static ProcessPauseSampler? StartIfTracing() =>
        AttachPathTrace.IsEnabled ? new ProcessPauseSampler() : null;

    /// <summary>Stops the sampler. A sample that is in progress can still write one record.</summary>
    public void Dispose() => _stopped = true;

    private void Run()
    {
        var reportTicks = ToStopwatchTicks(ReportThreshold);
        var stallTicks = ToStopwatchTicks(StallThreshold);
        var periodTicks = ToStopwatchTicks(Period);
        var lastPause = GC.GetTotalPauseDuration();
        var lastGen1 = GC.CollectionCount(1);
        var lastGen2 = GC.CollectionCount(2);
        var lastWake = Stopwatch.GetTimestamp();
        while (!_stopped)
        {
            Thread.Sleep(Period);
            var now = Stopwatch.GetTimestamp();
            var pause = GC.GetTotalPauseDuration();
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var paused = ToStopwatchTicks(pause - lastPause);
            var late = now - lastWake - periodTicks;
            try
            {
                if (paused >= reportTicks)
                {
                    var last = GC.GetGCMemoryInfo(GCKind.Any);
                    AttachPathTrace.RecordHop(
                        AttachPathTrace.DomainOutput,
                        StageGcPause,
                        paneId: null,
                        paused,
                        GenerationRoute(gen2 != lastGen2 ? 2 : gen1 != lastGen1 ? 1 : 0, last.Concurrent),
                        (int)Math.Min(last.PromotedBytes, int.MaxValue));
                }
                if (late >= stallTicks)
                    AttachPathTrace.RecordHop(AttachPathTrace.DomainOutput, StageThreadStall, paneId: null, late);
            }
            catch (Exception)
            {
                // A trace sink that fails (a closed stderr pipe, a full disk)
                // must not end the process. The sampler stops.
                return;
            }

            lastPause = pause;
            lastGen1 = gen1;
            lastGen2 = gen2;
            lastWake = now;
        }
    }

    internal static string GenerationRoute(int generation, bool background) => (generation, background) switch
    {
        (2, true) => "gen2-bgc",
        (2, false) => "gen2",
        (1, _) => "gen1",
        _ => "gen0",
    };

    private static long ToStopwatchTicks(TimeSpan value) =>
        (long)(value.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
}
