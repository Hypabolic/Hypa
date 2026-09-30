using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class WorktreeSidebarProvenanceTests
{
    [Fact]
    public void FromSnapshot_MapsWorktreeChromeOntoWorkspaceRows()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "focused_workspace_id": "ws-child",
              "worktree_directory": "/tmp/hypa-worktrees",
              "workspaces": [
                {
                  "workspace_id": "ws-parent",
                  "label": "repo",
                  "cwd": "/repo",
                  "worktree": { "key": "repo-key", "label": "repo", "is_linked_worktree": false }
                },
                {
                  "workspace_id": "ws-child",
                  "label": "repo",
                  "cwd": "/repo-feature",
                  "branch": "worktree/feature",
                  "worktree": { "key": "repo-key", "label": "repo", "is_linked_worktree": true }
                }
              ]
            }
            """);
        var input = SidebarLiveModel.FromSnapshot(
            doc.RootElement,
            AttachUiConfig.Default,
            expanded: true,
            requestedWidth: 26,
            collapsedWorktreeGroups: new HashSet<string>(StringComparer.Ordinal) { "repo-key" });

        Assert.Equal("repo-key", input.Workspaces[0].WorktreeKey);
        Assert.False(input.Workspaces[0].IsLinkedWorktree);
        Assert.True(input.Workspaces[1].IsLinkedWorktree);
        Assert.Equal("worktree/feature", input.Workspaces[1].Branch);
        Assert.Contains("repo-key", input.CollapsedWorktreeGroups!);

        var frame = SidebarSectionComposer.Compose(input);
        var ids = frame.Pane(SidebarPaneSlot.Spaces)!.Rows
            .Where(row => row.CardRowIndex == 0)
            .Select(row => row.Id)
            .ToArray();
        Assert.Equal(["ws-parent", "ws-child"], ids);
        Assert.Contains(
            frame.Pane(SidebarPaneSlot.Spaces)!.Rows,
            row => row.Id == "ws-child" && row.PrefixRole is SidebarPrefixRole.TreeLast);
    }
}
