using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class VisibilityRollbackTests
{
    [Fact]
    public void Failed_hide_restores_down_split_ratio_and_leaf_order()
    {
        var state = new AppState(SessionId.New("rollback-hide"));
        var workspace = state.CreateWorkspace("/tmp", "ws");
        var tab = state.GetTab(workspace.FocusedTabId!.Value)!;
        var first = new PaneId("p-first");
        var second = new PaneId("p-second");
        state.RegisterPane(Pane(first, tab, workspace.Id, PanePlacement.Tiled));
        state.RegisterPane(Pane(second, tab, workspace.Id, PanePlacement.Hidden));
        var visibility = new PaneVisibilityService(state);
        var shown = visibility.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = second,
            TargetPaneId = first,
            Direction = "down",
            Ratio = 0.3,
        });
        Assert.True(shown.IsOk);
        var before = state.GetTab(tab.Id)!.LayoutRoot!.ToCanonicalJson(true);
        var restore = state.CaptureVisibility(second);
        Assert.NotNull(restore);
        Assert.True(visibility.HideTiled(new PaneHideRequest { PaneId = second }).IsOk);
        state.RevertVisibility(restore, null);
        var after = state.GetTab(tab.Id)!;
        Assert.Equal(before, after.LayoutRoot!.ToCanonicalJson(true));
        Assert.Equal(PanePlacement.Tiled, state.GetPane(second)!.Placement);
        var split = Assert.IsType<LayoutSplitNode>(after.LayoutRoot);
        Assert.Equal(LayoutNode.DirectionDown, split.Direction);
        Assert.Equal(0.3, split.Ratio, 6);
    }

    [Fact]
    public void Failed_hide_of_only_leaf_restores_the_closed_tab()
    {
        var state = new AppState(SessionId.New("rollback-close"));
        var workspace = state.CreateWorkspace("/tmp", "ws");
        var prior = state.GetTab(workspace.FocusedTabId!.Value)!;
        state.RegisterPane(Pane(new PaneId("p-shell"), prior, workspace.Id, PanePlacement.Tiled));
        var revealed = state.CreateTab(workspace.Id, "question", focus: true);
        state.UpdateTab(revealed.Id, current => current with { OpenedFromTabId = prior.Id });
        var child = new PaneId("p-child");
        var revealedTab = state.GetTab(revealed.Id)!;
        state.RegisterPane(Pane(child, revealedTab, workspace.Id, PanePlacement.Tiled));
        var visibility = new PaneVisibilityService(state);
        var restore = state.CaptureVisibility(child);
        Assert.NotNull(restore);
        Assert.True(visibility.HideTiled(new PaneHideRequest { PaneId = child }).IsOk);
        Assert.Null(state.GetTab(revealed.Id));
        var focusedAfterHide = state.GetWorkspace(workspace.Id)!.FocusedTabId;
        state.RevertVisibility(
            restore with { OperationFocusedTabId = focusedAfterHide },
            createdTabId: null);

        var restored = state.GetTab(revealed.Id);
        Assert.NotNull(restored);
        Assert.Contains(state.GetWorkspace(workspace.Id)!.TabIds, id => id.Value == revealed.Id.Value);
        Assert.Equal(PanePlacement.Tiled, state.GetPane(child)!.Placement);
        Assert.Equal(revealed.Id, state.GetPane(child)!.TabId);
        Assert.True(LayoutTreeOperations.ContainsPane(restored!.LayoutRoot, child));
        Assert.Equal(revealed.Id, state.GetWorkspace(workspace.Id)!.FocusedTabId);
        Assert.DoesNotContain(
            state.GetTab(prior.Id)!.HiddenPaneIds,
            id => id.Value == child.Value);
    }

    private static PaneState Pane(PaneId id, TabState tab, WorkspaceId workspace, PanePlacement placement) =>
        new()
        {
            Id = id,
            TabId = tab.Id,
            WorkspaceId = workspace,
            Placement = placement,
            Label = id.Value,
            Cwd = "/tmp",
            Cols = 80,
            Rows = 24,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        };
}
