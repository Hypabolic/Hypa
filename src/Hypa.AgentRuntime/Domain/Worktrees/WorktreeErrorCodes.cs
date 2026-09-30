namespace Hypa.AgentRuntime.Domain.Worktrees;

public static class WorktreeErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string WorkspaceNotFound = "workspace_not_found";
    public const string NotGitWorktree = "not_git_worktree";
    public const string LinkedWorktreeSource = "linked_worktree_source";
    public const string WorktreeNotFound = "worktree_not_found";
    public const string NotLinkedWorktree = "not_linked_worktree";
    public const string DirtyRequiresForce = "dirty_worktree_requires_force";
    public const string CreateFailed = "worktree_create_failed";
    public const string RemoveFailed = "worktree_remove_failed";
    public const string ListFailed = "worktree_list_failed";
    public const string OpenFailed = "worktree_open_failed";
    public const string OperationInProgress = "worktree_operation_in_progress";
    public const string AmbiguousBranch = "ambiguous_worktree_branch";
    public const string LastWorkspace = "last_workspace";
}
