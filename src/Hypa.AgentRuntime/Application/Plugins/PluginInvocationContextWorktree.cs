using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
// / Maps workspace membership onto plugin context.
/// <c>src/api/schema/workspaces.rs:79-85</c>.
/// </summary>
public static class PluginInvocationContextWorktree
{
    public static PluginWorktreeContext? FromMembership(WorktreeSpaceMembership? membership)
    {
        if (membership is null)
            return null;
        return new PluginWorktreeContext
        {
            RepoKey = membership.Key,
            RepoName = membership.Label,
            RepoRoot = membership.RepoRoot,
            CheckoutPath = membership.CheckoutPath,
            IsLinkedWorktree = membership.IsLinkedWorktree,
        };
    }
}
