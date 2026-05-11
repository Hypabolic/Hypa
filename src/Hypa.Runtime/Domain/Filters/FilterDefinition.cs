namespace Hypa.Runtime.Domain.Filters;

public sealed record FilterDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public string? MatchCommand { get; init; }
    public FilterScope Scope { get; init; } = FilterScope.BuiltIn;
    public bool MergeStderr { get; init; }
    public IReadOnlyList<FilterStage> Stages { get; init; } = [];
}
