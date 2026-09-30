namespace Hypa.AgentRuntime.Domain.Worktrees;

/// <summary>
/// <see cref="Key"/> is an opaque digest of the canonical common dir.
/// </summary>
public sealed record GitSpaceMetadata
{
    public required string Key { get; init; }
    public required string CheckoutKey { get; init; }
    public required string RepoName { get; init; }
    public required string RepoRoot { get; init; }
    public required bool IsLinkedWorktree { get; init; }
}
