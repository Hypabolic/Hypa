namespace Hypa.AgentRuntime.Domain.Worktrees;

/// <summary>Expected worktree failure. Feature-specific error for <see cref="Result{T,E}"/>.</summary>
public sealed record WorktreeError(string Code, string Message);
