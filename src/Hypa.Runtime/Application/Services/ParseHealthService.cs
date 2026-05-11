using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Parsers;

namespace Hypa.Runtime.Application.Services;

public sealed record ParseHealthRow(string Executable, ParseTier Tier, int Count, double Pct);

public sealed class ParseHealthService(IParseMetricsRepository repository)
{
    public async Task<IReadOnlyList<ParseHealthRow>> GetReportAsync(CancellationToken ct)
    {
        var records = await repository.QueryAsync(10_000, ct);
        if (records.Count == 0)
            return [];

        var groups = records
            .GroupBy(r => new { r.Executable, r.ParseTier })
            .Select(g => new { g.Key.Executable, g.Key.ParseTier, Count = g.Count() })
            .ToList();

        var totals = groups
            .GroupBy(g => g.Executable)
            .ToDictionary(g => g.Key, g => (double)g.Sum(x => x.Count));

        return groups
            .OrderBy(g => g.Executable)
            .ThenBy(g => g.ParseTier)
            .Select(g => new ParseHealthRow(
                g.Executable,
                g.ParseTier,
                g.Count,
                Math.Round(g.Count / totals[g.Executable] * 100, 1)))
            .ToList();
    }
}
