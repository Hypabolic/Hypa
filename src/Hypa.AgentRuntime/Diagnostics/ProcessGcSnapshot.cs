namespace Hypa.AgentRuntime.Application;

/// <summary>
/// The GC totals of this process since its start. Two snapshots give the
/// collections, the pause time and the allocated bytes between them. A
/// capture costs a few microseconds and one small allocation, so a health
/// reply or a latency report can include it with the trace off. Do not
/// capture on a hot path.
/// </summary>
public sealed record ProcessGcSnapshot
{
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required long PauseTotalUs { get; init; }
    public required long AllocatedBytes { get; init; }

    // The last-GC fields are not required: a reply from an older binary has no such fields.

    /// <summary>The index of the last GC. Polls that see the same index saw the same GC.</summary>
    public long LastGcIndex { get; init; }

    public int LastGcGeneration { get; init; }

    public long LastGcPromotedBytes { get; init; }

    public long LastGcPauseUs { get; init; }

    public static ProcessGcSnapshot Capture()
    {
        var last = GC.GetGCMemoryInfo(GCKind.Any);
        var lastPause = TimeSpan.Zero;
        foreach (var pause in last.PauseDurations)
            lastPause += pause;
        return new()
        {
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            PauseTotalUs = (long)GC.GetTotalPauseDuration().TotalMicroseconds,
            // Precise: the cheap count leaves out the open allocation contexts of other threads.
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            LastGcIndex = last.Index,
            LastGcGeneration = last.Generation,
            LastGcPromotedBytes = last.PromotedBytes,
            LastGcPauseUs = (long)lastPause.TotalMicroseconds,
        };
    }
}
