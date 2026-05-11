using System.Text.RegularExpressions;

namespace Hypa.Runtime.Domain.Filters;

public sealed record CompiledFilterDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public string? MatchCommand { get; init; }
    public Regex? CompiledMatchCommand { get; init; }
    public FilterScope Scope { get; init; } = FilterScope.BuiltIn;
    public bool MergeStderr { get; init; }
    public IReadOnlyList<CompiledFilterStage> Stages { get; init; } = [];
}