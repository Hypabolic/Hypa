namespace Hypa.Runtime.Domain.Filters;

public sealed record FilterStage
{
    public FilterStageKind Kind { get; init; }
    public string? Pattern { get; init; }
    public string? Replacement { get; init; }
    public string? Guard { get; init; }
    public string? TransformId { get; init; }
    public int? Count { get; init; }
}
