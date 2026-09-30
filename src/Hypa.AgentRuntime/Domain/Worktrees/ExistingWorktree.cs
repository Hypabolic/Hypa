namespace Hypa.AgentRuntime.Domain.Worktrees;

public sealed record ExistingWorktree
{
    public required string Path { get; init; }
    public string? Branch { get; init; }
    public bool IsBare { get; init; }
    public bool IsDetached { get; init; }
    public bool IsPrunable { get; init; }
}
