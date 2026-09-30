namespace Hypa.AgentRuntime.Domain.Worktrees;

/// <summary>
/// Provenance on a workspace. <see cref="Key"/> is an opaque digest of the
/// </summary>
public sealed record WorktreeSpaceMembership
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string RepoRoot { get; init; }
    public required string CheckoutPath { get; init; }
    public required bool IsLinkedWorktree { get; init; }
}
