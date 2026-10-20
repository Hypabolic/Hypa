using System.Numerics;

namespace Hypa.Cli.Attach.Latency;

/// <summary>
/// A fixed-bucket histogram of durations in microseconds. A value below 32 µs
/// has its own bucket. Each power of two above that has 16 buckets, so a
/// percentile is within about 6% of the true value. A record is lock-free
/// and allocates nothing.
/// </summary>
internal sealed class LatencyHistogram
{
    private const int LinearBuckets = 32;
    private const int SubBucketBits = 4;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int FirstOctave = 5;
    private const int LastOctave = 40;

    /// <summary>Values above about 12 days fall in the last bucket.</summary>
    internal const long MaxTrackedUs = (1L << (LastOctave + 1)) - 1;

    private const int BucketCount = LinearBuckets + ((LastOctave - FirstOctave + 1) * SubBuckets);

    private readonly long[] _counts = new long[BucketCount];
    private long _count;
    private long _max;

    public long Count => Volatile.Read(ref _count);

    public void Record(long microseconds)
    {
        if (microseconds < 0)
            microseconds = 0;
        Interlocked.Increment(ref _counts[BucketIndex(microseconds)]);
        Interlocked.Increment(ref _count);
        var max = Volatile.Read(ref _max);
        while (microseconds > max)
        {
            var prior = Interlocked.CompareExchange(ref _max, microseconds, max);
            if (prior == max)
                break;
            max = prior;
        }
    }

    public void Reset()
    {
        for (var i = 0; i < _counts.Length; i++)
            Interlocked.Exchange(ref _counts[i], 0);
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _max, 0);
    }

    public LatencyHistogramReport Report()
    {
        var counts = new long[_counts.Length];
        long total = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = Volatile.Read(ref _counts[i]);
            total += counts[i];
        }

        var max = Volatile.Read(ref _max);
        return new LatencyHistogramReport
        {
            Count = total,
            P50Us = Percentile(counts, total, 0.50, max),
            P95Us = Percentile(counts, total, 0.95, max),
            P99Us = Percentile(counts, total, 0.99, max),
            MaxUs = total == 0 ? 0 : max,
        };
    }

    /// <summary>
    /// The upper bound of the bucket that holds the rank, capped at the
    /// largest value seen. Zero when the histogram is empty.
    /// </summary>
    private static long Percentile(long[] counts, long total, double quantile, long max)
    {
        if (total == 0)
            return 0;
        var rank = (long)Math.Ceiling(quantile * total);
        if (rank < 1)
            rank = 1;
        long seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank)
                return Math.Min(UpperBound(i), max);
        }

        return max;
    }

    internal static int BucketIndex(long microseconds)
    {
        if (microseconds < LinearBuckets)
            return (int)microseconds;
        if (microseconds > MaxTrackedUs)
            microseconds = MaxTrackedUs;
        var octave = 63 - BitOperations.LeadingZeroCount((ulong)microseconds);
        var sub = (int)(microseconds >> (octave - SubBucketBits)) & (SubBuckets - 1);
        return LinearBuckets + ((octave - FirstOctave) * SubBuckets) + sub;
    }

    internal static long UpperBound(int index)
    {
        if (index < LinearBuckets)
            return index;
        var octave = ((index - LinearBuckets) / SubBuckets) + FirstOctave;
        var sub = (index - LinearBuckets) % SubBuckets;
        var lower = (1L << octave) + ((long)sub << (octave - SubBucketBits));
        return lower + (1L << (octave - SubBucketBits)) - 1;
    }
}

/// <summary>Percentiles of one histogram, in microseconds.</summary>
public sealed record LatencyHistogramReport
{
    public long Count { get; init; }

    public long P50Us { get; init; }

    public long P95Us { get; init; }

    public long P99Us { get; init; }

    public long MaxUs { get; init; }
}
