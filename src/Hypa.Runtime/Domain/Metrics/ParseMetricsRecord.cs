using Hypa.Runtime.Domain.Parsers;

namespace Hypa.Runtime.Domain.Metrics;

public sealed record ParseMetricsRecord
{
    public string RunId { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public ParseTier ParseTier { get; init; }
    public string? FilterId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}
