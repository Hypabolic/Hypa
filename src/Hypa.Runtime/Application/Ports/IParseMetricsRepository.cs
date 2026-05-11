using Hypa.Runtime.Domain.Metrics;

namespace Hypa.Runtime.Application.Ports;

public interface IParseMetricsRepository
{
    Task RecordAsync(ParseMetricsRecord record, CancellationToken ct);
    Task<IReadOnlyList<ParseMetricsRecord>> QueryAsync(int limit, CancellationToken ct);
}
