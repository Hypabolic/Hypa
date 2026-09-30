namespace Hypa.AgentRuntime.Domain.Worktrees;

public sealed record WorktreeCommand
{
    public required string Program { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
}
