using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Sidebar;

public sealed class WorktreeWorkspaceGroupingTests
{
    [Fact]
    public void Order_PutsParentFirstThenChildren()
    {
        var workspaces = Group(
            Child("child-b", "repo", "worktree/beta"),
            Parent("parent", "repo"),
            Child("child-a", "repo", "worktree/alpha"),
            Ungrouped("other"));

        var ordered = WorktreeWorkspaceGrouping.Order(workspaces);

        Assert.Equal(
            ["parent", "child-b", "child-a", "other"],
            ordered.Select(entry => workspaces[entry.Index].Id).ToArray());
        Assert.Equal([false, true, true, false], ordered.Select(entry => entry.Indented).ToArray());
        Assert.Equal([false, false, true, false], ordered.Select(entry => entry.LastChild).ToArray());
        Assert.Equal("repo", ordered[0].GroupKey);
        Assert.Equal("repo", WorktreeWorkspaceGrouping.ParentGroupKey(workspaces, 1));
    }

    [Fact]
    public void Order_KeepsOrphanLinkedRowsUngrouped()
    {
        var workspaces = Group(
            Child("orphan", "missing-parent", "worktree/feature"),
            Ungrouped("plain"));

        var ordered = WorktreeWorkspaceGrouping.Order(workspaces);

        Assert.Equal(["orphan", "plain"], ordered.Select(entry => workspaces[entry.Index].Id).ToArray());
        Assert.All(ordered, entry => Assert.False(entry.Indented));
        Assert.Null(WorktreeWorkspaceGrouping.ParentGroupKey(workspaces, 0));
    }

    [Fact]
    public void Order_CollapsedGroupKeepsFocusedChild()
    {
        var workspaces = Group(
            Parent("parent", "repo"),
            Child("hidden", "repo", "worktree/hidden"),
            Child("focused", "repo", "worktree/focused"));
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "repo" };

        var hidden = WorktreeWorkspaceGrouping.Order(workspaces, collapsed, focusedWorkspaceId: "parent");
        Assert.Equal(["parent"], hidden.Select(entry => workspaces[entry.Index].Id).ToArray());

        var visible = WorktreeWorkspaceGrouping.Order(workspaces, collapsed, focusedWorkspaceId: "focused");
        Assert.Equal(["parent", "focused"], visible.Select(entry => workspaces[entry.Index].Id).ToArray());
        Assert.True(visible[1].Indented);
        Assert.True(visible[1].LastChild);
    }

    [Fact]
    public void Compose_SetsTreeConnectorsAndSuppressesChildGit()
    {
        var git = new MapSidebarGitStatus(new Dictionary<string, SidebarGitInfo>(StringComparer.Ordinal)
        {
            ["/parent"] = new("main", "dirty"),
            ["/child"] = new("worktree/feature", "clean"),
        });
        var frame = Compose(
            [
                Parent("parent", "repo", cwd: "/parent"),
                Child("child", "repo", "worktree/feature", cwd: "/child"),
            ],
            git: git,
            width: 26);

        var spaces = frame.Pane(SidebarPaneSlot.Spaces)!;
        var parent = spaces.Rows.Where(row => row.Id == "parent").ToArray();
        var child = spaces.Rows.Where(row => row.Id == "child").ToArray();
        Assert.Contains(parent, row => row.CardRowIndex == 0 && row.PrefixRole is SidebarPrefixRole.None);
        Assert.Contains(parent, row => row.Tokens.Any(token => token.Id == "branch" && token.Text == "main"));
        Assert.Contains(child, row => row.CardRowIndex == 0 && row.PrefixRole is SidebarPrefixRole.TreeLast);
        Assert.Contains(child, row => row.CardRowIndex == 0 && row.IndentCols >= WorktreeWorkspaceGrouping.ChildIndentCols);
        Assert.DoesNotContain(child, row => row.Tokens.Any(token => token.Id is "branch" or "git_status" && token.Text.Length > 0));
        Assert.Contains(child, row => row.Label.Contains("feature", StringComparison.Ordinal));
        Assert.DoesNotContain(child, row => row.Label.Contains("worktree/", StringComparison.Ordinal));
        Assert.All(child, row => Assert.Equal("repo", row.GroupKey));
    }

    [Fact]
    public void Compose_AggregatesHiddenChildAttention()
    {
        var frame = Compose(
            [
                Parent("parent", "repo"),
                Child("child", "repo", "worktree/alert"),
            ],
            collapsed: ["repo"],
            panes:
            [
                Pane("parent-pane", "parent", SidebarTokenGrammar.Idle),
                Pane("child-pane", "child", SidebarTokenGrammar.Blocked),
            ]);

        var parent = Assert.Single(
            frame.Pane(SidebarPaneSlot.Spaces)!.Rows,
            row => row.Id == "parent" && row.CardRowIndex == 0);
        Assert.Equal(SidebarTokenGrammar.Blocked, parent.State);
        Assert.DoesNotContain(frame.Pane(SidebarPaneSlot.Spaces)!.Rows, row => row.Id == "child");
    }

    [Fact]
    public void Compose_FitsUnicodeOnNarrowWidth()
    {
        var frame = Compose(
            [
                Parent("parent", "repo", label: "日本語リポジトリ"),
                Child("child", "repo", "worktree/漢字-branch", label: "作業ツリー"),
            ],
            width: 12);

        var rows = frame.Pane(SidebarPaneSlot.Spaces)!.Rows;
        Assert.Contains(rows, row => row.Id == "parent" && row.Label.Contains("日本語", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Id == "child" && row.PrefixRole is SidebarPrefixRole.TreeLast);
        Assert.All(rows, row =>
            Assert.True(SafeDisplayText.Width(SafeDisplayText.Clip(row.Label, 12)) <= 12));
    }

    [Fact]
    public void Hits_KeepWorkspaceIdAndTreeCells()
    {
        var frame = Compose(
            [
                Parent("parent", "repo"),
                Child("child", "repo", "worktree/feature"),
            ]);
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame);
        var hits = geo.SidebarRows.Where(hit => hit.Kind is SidebarStubKind.Workspace).ToArray();
        Assert.Contains(hits, hit => hit.Id == "parent" && hit.GroupKey == "repo" && hit.PrefixRole is SidebarPrefixRole.None);
        Assert.Contains(hits, hit =>
            hit.Id == "child"
            && hit.GroupKey == "repo"
            && hit.PrefixRole is SidebarPrefixRole.TreeLast
            && hit.IndentCols >= WorktreeWorkspaceGrouping.ChildIndentCols);
        var parentHit = Assert.Single(hits, hit => hit.Id == "parent" && hit.CardRowIndex == 0);
        Assert.NotNull(parentHit.GroupToggle);
        Assert.Equal(1, parentHit.GroupToggle!.Value.Cols);
        Assert.Equal(parentHit.Rect.EndCol - 1, parentHit.GroupToggle.Value.Col);
        Assert.Equal(parentHit.Rect.Row, parentHit.GroupToggle.Value.Row);
        Assert.DoesNotContain(hits, hit => hit.Id == "child" && hit.GroupToggle is not null);
    }

    [Fact]
    public void Hits_GroupToggleFollowsScrolledParentCard()
    {
        var workspaces = new List<SidebarWorkspaceItem>();
        for (var i = 0; i < 8; i++)
            workspaces.Add(Ungrouped($"plain-{i}"));
        workspaces.Add(Parent("parent", "repo"));
        workspaces.Add(Child("child", "repo", "worktree/feature"));
        var frame = Compose(workspaces);
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame,
            spacesScroll: 8);
        var parent = Assert.Single(
            geo.SidebarRows,
            hit => hit.Kind is SidebarStubKind.Workspace && hit.Id == "parent" && hit.CardRowIndex == 0);
        var body = geo.SpacesBody!.Value;
        Assert.NotNull(parent.GroupToggle);
        Assert.Equal(body.Row, parent.Rect.Row);
        Assert.Equal(body.Row, parent.GroupToggle!.Value.Row);
        Assert.Equal(body.EndCol - 1, parent.GroupToggle.Value.Col);
        Assert.DoesNotContain(
            geo.SidebarRows,
            hit => hit.Kind is SidebarStubKind.Workspace && hit.Id.StartsWith("plain-", StringComparison.Ordinal));

        var toggle = ChromeHitTest.Hit(geo, parent.GroupToggle.Value.Col, parent.GroupToggle.Value.Row);
        Assert.Equal(ChromeHitKind.SidebarWorktreeGroupToggle, toggle?.Kind);
        Assert.Equal("parent", toggle?.WorkspaceId);
        Assert.Equal("repo", toggle?.GroupKey);
        var apply = ChromeHitApply.Apply(toggle!, overflowOffset: 0, maxOverflowOffset: 0);
        Assert.Equal("repo", apply.ToggleWorktreeGroupKey);
        Assert.Null(apply.FocusWorkspaceId);

        var row = ChromeHitTest.Hit(geo, parent.Rect.Col + 1, parent.Rect.Row);
        Assert.Equal(ChromeHitKind.SidebarWorkspace, row?.Kind);
        Assert.Equal("parent", row?.WorkspaceId);
    }

    private static SidebarFrame Compose(
        IReadOnlyList<SidebarWorkspaceItem> workspaces,
        ISidebarGitStatus? git = null,
        IReadOnlyList<string>? collapsed = null,
        IReadOnlyList<SidebarPaneItem>? panes = null,
        int width = 26)
    {
        return SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                MouseCapture = true,
                RequestedWidth = width,
                FocusedWorkspaceId = "parent",
                Workspaces = workspaces,
                Panes = panes ?? [],
                Git = git,
                CollapsedWorktreeGroups = collapsed is null
                    ? null
                    : new HashSet<string>(collapsed, StringComparer.Ordinal),
            });
    }

    private static SidebarWorkspaceItem[] Group(params SidebarWorkspaceItem[] items)
    {
        for (var i = 0; i < items.Length; i++)
            items[i] = items[i] with { Order = i };
        return items;
    }

    private static SidebarWorkspaceItem Parent(string id, string key, string? label = null, string cwd = "") =>
        new()
        {
            Id = id,
            Label = label ?? id,
            Cwd = cwd,
            WorktreeKey = key,
            WorktreeLabel = key,
            IsLinkedWorktree = false,
        };

    private static SidebarWorkspaceItem Child(
        string id,
        string key,
        string branch,
        string? label = null,
        string cwd = "") =>
        new()
        {
            Id = id,
            Label = label ?? id,
            Cwd = cwd,
            WorktreeKey = key,
            WorktreeLabel = key,
            IsLinkedWorktree = true,
            Branch = branch,
        };

    private static SidebarWorkspaceItem Ungrouped(string id) =>
        new() { Id = id, Label = id };

    private static SidebarPaneItem Pane(string id, string workspaceId, string state) =>
        new()
        {
            Id = id,
            TabId = "t1",
            WorkspaceId = workspaceId,
            State = state,
        };
}
