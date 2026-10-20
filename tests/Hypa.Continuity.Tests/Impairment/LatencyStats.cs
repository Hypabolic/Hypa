using System.Globalization;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>Nearest-rank percentiles of a set of latencies in milliseconds.</summary>
internal readonly record struct LatencyStats(int Count, double P50, double P95, double P99, double Max)
{
    public static LatencyStats From(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
            return new LatencyStats(0, 0, 0, 0, 0);
        var sorted = values.Order().ToArray();
        return new LatencyStats(
            sorted.Length,
            Rank(sorted, 0.50),
            Rank(sorted, 0.95),
            Rank(sorted, 0.99),
            sorted[^1]);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"n={Count} p50={P50:0.0}ms p95={P95:0.0}ms p99={P99:0.0}ms max={Max:0.0}ms");

    private static double Rank(double[] sorted, double quantile)
    {
        var index = (int)Math.Ceiling(quantile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
