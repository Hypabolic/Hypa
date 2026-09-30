using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record WorktreeListParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("trust_repository")]
    public bool TrustRepository { get; init; }
}

public sealed record WorktreeCreateParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("focus")]
    public bool Focus { get; init; }

    [JsonPropertyName("trust_repository")]
    public bool TrustRepository { get; init; }
}

public sealed record WorktreeOpenParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("focus")]
    public bool Focus { get; init; }

    [JsonPropertyName("trust_repository")]
    public bool TrustRepository { get; init; }
}

public sealed record WorktreeRemoveParams
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("force")]
    public bool Force { get; init; }

    [JsonPropertyName("trust_repository")]
    public bool TrustRepository { get; init; }
}

public sealed record WorktreeSourceInfo
{
    [JsonPropertyName("repo_key")]
    public required string RepoKey { get; init; }

    [JsonPropertyName("repo_name")]
    public required string RepoName { get; init; }

    [JsonPropertyName("repo_root")]
    public required string RepoRoot { get; init; }

    [JsonPropertyName("source_checkout_path")]
    public required string SourceCheckoutPath { get; init; }

    [JsonPropertyName("source_workspace_id")]
    public string? SourceWorkspaceId { get; init; }
}

public sealed record WorktreeInfo
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("is_bare")]
    public bool IsBare { get; init; }

    [JsonPropertyName("is_detached")]
    public bool IsDetached { get; init; }

    [JsonPropertyName("is_prunable")]
    public bool IsPrunable { get; init; }

    [JsonPropertyName("is_linked_worktree")]
    public bool IsLinkedWorktree { get; init; }

    [JsonPropertyName("open_workspace_id")]
    public string? OpenWorkspaceId { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

public sealed record WorktreeListResult
{
    [JsonPropertyName("source")]
    public required WorktreeSourceInfo Source { get; init; }

    [JsonPropertyName("worktrees")]
    public required IReadOnlyList<WorktreeInfo> Worktrees { get; init; }
}

public sealed record WorktreeCreateResult
{
    [JsonPropertyName("workspace")]
    public required WorkspaceResult Workspace { get; init; }

    [JsonPropertyName("worktree")]
    public required WorktreeInfo Worktree { get; init; }

    [JsonPropertyName("created")]
    public bool Created { get; init; }

    [JsonPropertyName("already_open")]
    public bool AlreadyOpen { get; init; }
}

public sealed record WorktreeOpenResult
{
    [JsonPropertyName("workspace")]
    public required WorkspaceResult Workspace { get; init; }

    [JsonPropertyName("worktree")]
    public required WorktreeInfo Worktree { get; init; }

    [JsonPropertyName("created")]
    public bool Created { get; init; }

    [JsonPropertyName("already_open")]
    public bool AlreadyOpen { get; init; }
}

public sealed record WorktreeRemoveResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string WorkspaceId { get; init; }
}

/// <summary>Workspace snapshot chrome. <c>key</c> is an opaque digest. No checkout path.</summary>
public sealed record WorkspaceWorktreeChrome
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("is_linked_worktree")]
    public bool IsLinkedWorktree { get; init; }
}
