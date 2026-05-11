using System.Text.RegularExpressions;

namespace Hypa.Runtime.Domain.Filters;

public sealed record CompiledFilterStage
{
    public FilterStage Stage { get; init; } = new();
    public Regex? CompiledRegex { get; init; }
    public Regex? CompiledGuard { get; init; }
}
