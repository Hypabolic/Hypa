using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PaneVisibilityServiceTests
{
    [Fact]
    public void Show_hidden_into_empty_tab_is_sole_leaf()
    {
        var (svc, state, tab, hidden) = GraphWithHidden();
        var before = state.Snapshot();

        var result = svc.ShowTiled(new PaneShowTiledRequest { PaneId = hidden });

        Assert.True(result.IsOk);
        Assert.True(result.Value.Changed);
        var pane = state.GetPane(hidden)!;
        var next = state.GetTab(tab)!;
        Assert.Equal(PanePlacement.Tiled, pane.Placement);
        Assert.False(svc.IsHidden(hidden));
        Assert.Equal([hidden], next.PaneIds);
        Assert.Empty(next.HiddenPaneIds);
        Assert.True(LayoutTreeOperations.ContainsPane(next.LayoutRoot, hidden));
        Assert.Single(LayoutTreeOperations.Leaves(next.LayoutRoot));
        Assert.Equal(before.Panes[hidden.Value].Cols, pane.Cols);
        Assert.Equal(before.Panes[hidden.Value].Rows, pane.Rows);
        Assert.Equal(before.Panes[hidden.Value].OccupantGeneration, pane.OccupantGeneration);
    }

    [Fact]
    public void Show_hidden_into_occupied_tab_inserts_one_leaf()
    {
        var (svc, state, tab, hidden, tiled) = GraphWithTiledAndHidden();

        var result = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            TargetPaneId = tiled,
            Direction = "right",
            Ratio = 0.4,
        });

        Assert.True(result.IsOk);
        var next = state.GetTab(tab)!;
        Assert.Equal(2, next.PaneIds.Count);
        Assert.Contains(next.PaneIds, p => p.Value == hidden.Value);
        Assert.Contains(next.PaneIds, p => p.Value == tiled.Value);
        Assert.Empty(next.HiddenPaneIds);
        Assert.Equal(2, LayoutTreeOperations.Leaves(next.LayoutRoot).Count);
        Assert.True(LayoutTreeOperations.ContainsPane(next.LayoutRoot, hidden));
        Assert.True(LayoutTreeOperations.ContainsPane(next.LayoutRoot, tiled));
    }

    [Fact]
    public void Invalid_target_leaves_graph_unchanged()
    {
        var (svc, state, tab, hidden, tiled) = GraphWithTiledAndHidden();
        var before = state.Snapshot();

        var result = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            TargetPaneId = new PaneId("missing"),
            Direction = "right",
        });

        Assert.False(result.IsOk);
        Assert.Equal(PaneVisibilityError.InvalidTargetCode, result.Error.Code);
        AssertUnchanged(state, before, tab, hidden, tiled);
    }

    [Fact]
    public void Empty_dest_rejects_invalid_supplied_arguments()
    {
        var (svc, state, tab, hidden) = GraphWithHidden();
        var before = state.Snapshot();

        var badTarget = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            TargetPaneId = new PaneId("ghost"),
        });
        Assert.False(badTarget.IsOk);
        AssertUnchanged(state, before, tab, hidden);

        var badDirection = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            Direction = "left",
        });
        Assert.False(badDirection.IsOk);
        AssertUnchanged(state, before, tab, hidden);

        var badRatio = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            Ratio = 1.5,
        });
        Assert.False(badRatio.IsOk);
        AssertUnchanged(state, before, tab, hidden);
    }

    [Fact]
    public void Hide_tiled_keeps_size_and_generation()
    {
        var (svc, state, tab, hidden, tiled) = GraphWithTiledAndHidden();
        Assert.True(svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            TargetPaneId = tiled,
            Direction = "down",
        }).IsOk);
        var shown = state.GetPane(hidden)!;

        var hide = svc.HideTiled(new PaneHideRequest { PaneId = hidden });

        Assert.True(hide.IsOk);
        Assert.True(hide.Value.Changed);
        var pane = state.GetPane(hidden)!;
        var next = state.GetTab(tab)!;
        Assert.Equal(PanePlacement.Hidden, pane.Placement);
        Assert.True(svc.IsHidden(hidden));
        Assert.DoesNotContain(next.PaneIds, p => p.Value == hidden.Value);
        Assert.Contains(next.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.False(LayoutTreeOperations.ContainsPane(next.LayoutRoot, hidden));
        Assert.Equal(shown.Cols, pane.Cols);
        Assert.Equal(shown.Rows, pane.Rows);
        Assert.Equal(shown.OccupantGeneration, pane.OccupantGeneration);
        Assert.True(pane.Cols > 0);
        Assert.True(pane.Rows > 0);
    }

    [Fact]
    public void Hide_last_tiled_leaf_leaves_empty_tab()
    {
        var (svc, state, tab, hidden) = GraphWithHidden();
        Assert.True(svc.ShowTiled(new PaneShowTiledRequest { PaneId = hidden }).IsOk);

        var hide = svc.HideTiled(new PaneHideRequest { PaneId = hidden });

        Assert.True(hide.IsOk);
        var next = state.GetTab(tab)!;
        Assert.Empty(next.PaneIds);
        Assert.Null(next.LayoutRoot);
        Assert.Contains(next.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.NotNull(state.GetTab(tab));
        Assert.Null(hide.Value.ClosedTabId);
    }

    [Fact]
    public void Hide_of_only_leaf_closes_tab_and_returns_focus_to_prior_tab()
    {
        var state = new AppState(SessionId.New("vis-close"));
        var workspace = state.CreateWorkspace("/tmp", "ws");
        var prior = state.GetTab(workspace.FocusedTabId!.Value)!;
        var shell = new PaneId("p-shell");
        state.RegisterPane(MakePane(shell, prior.Id, workspace.Id, PanePlacement.Tiled));
        var revealed = state.CreateTab(workspace.Id, "question", focus: true);
        state.UpdateTab(revealed.Id, current => current with { OpenedFromTabId = prior.Id });
        var child = new PaneId("p-child");
        state.RegisterPane(MakePane(child, revealed.Id, workspace.Id, PanePlacement.Tiled, cols: 40, rows: 12, gen: 7));
        var svc = new PaneVisibilityService(state);

        var hide = svc.HideTiled(new PaneHideRequest { PaneId = child });

        Assert.True(hide.IsOk);
        Assert.Equal(revealed.Id, hide.Value.ClosedTabId);
        Assert.Equal(prior.Id, hide.Value.TargetTabId);
        Assert.Null(state.GetTab(revealed.Id));
        Assert.DoesNotContain(state.GetWorkspace(workspace.Id)!.TabIds, id => id.Value == revealed.Id.Value);
        var pane = state.GetPane(child)!;
        Assert.Equal(PanePlacement.Hidden, pane.Placement);
        Assert.Equal(prior.Id, pane.TabId);
        Assert.True(pane.IsAlive);
        Assert.Equal(7, pane.OccupantGeneration);
        Assert.Contains(state.GetTab(prior.Id)!.HiddenPaneIds, id => id.Value == child.Value);
        Assert.Contains(state.GetTab(prior.Id)!.PaneIds, id => id.Value == shell.Value);
        Assert.Equal(prior.Id, state.GetWorkspace(workspace.Id)!.FocusedTabId);
    }

    [Fact]
    public void Hide_of_only_leaf_focuses_neighbor_when_prior_tab_is_gone()
    {
        var state = new AppState(SessionId.New("vis-neighbor"));
        var workspace = state.CreateWorkspace("/tmp", "ws");
        var first = state.GetTab(workspace.FocusedTabId!.Value)!;
        state.RegisterPane(MakePane(new PaneId("p-first"), first.Id, workspace.Id, PanePlacement.Tiled));
        var middle = state.CreateTab(workspace.Id, "middle", focus: false);
        state.RegisterPane(MakePane(new PaneId("p-middle"), middle.Id, workspace.Id, PanePlacement.Tiled));
        var revealed = state.CreateTab(workspace.Id, "question", focus: true);
        var child = new PaneId("p-child");
        state.RegisterPane(MakePane(child, revealed.Id, workspace.Id, PanePlacement.Tiled));
        var svc = new PaneVisibilityService(state);

        var hide = svc.HideTiled(new PaneHideRequest { PaneId = child });

        Assert.True(hide.IsOk);
        Assert.Equal(revealed.Id, hide.Value.ClosedTabId);
        Assert.Equal(middle.Id, hide.Value.TargetTabId);
        Assert.Null(state.GetTab(revealed.Id));
        Assert.Equal(middle.Id, state.GetWorkspace(workspace.Id)!.FocusedTabId);
        Assert.Equal(PanePlacement.Hidden, state.GetPane(child)!.Placement);
        Assert.Equal(middle.Id, state.GetPane(child)!.TabId);
        Assert.Contains(state.GetTab(middle.Id)!.HiddenPaneIds, id => id.Value == child.Value);
    }

    [Fact]
    public void Hide_of_unfocused_only_leaf_keeps_the_current_tab_focus()
    {
        var state = new AppState(SessionId.New("vis-unfocused"));
        var workspace = state.CreateWorkspace("/tmp", "ws");
        var focused = state.GetTab(workspace.FocusedTabId!.Value)!;
        state.RegisterPane(MakePane(new PaneId("p-shell"), focused.Id, workspace.Id, PanePlacement.Tiled));
        var extra = state.CreateTab(workspace.Id, "extra", focus: false);
        var child = new PaneId("p-child");
        state.RegisterPane(MakePane(child, extra.Id, workspace.Id, PanePlacement.Tiled));
        var svc = new PaneVisibilityService(state);

        var hide = svc.HideTiled(new PaneHideRequest { PaneId = child });

        Assert.True(hide.IsOk);
        Assert.Equal(extra.Id, hide.Value.ClosedTabId);
        Assert.Null(state.GetTab(extra.Id));
        Assert.Equal(focused.Id, state.GetWorkspace(workspace.Id)!.FocusedTabId);
        Assert.Equal(focused.Id, state.GetPane(child)!.TabId);
        Assert.Equal(PanePlacement.Hidden, state.GetPane(child)!.Placement);
    }

    [Fact]
    public void Cross_workspace_show_updates_owner_without_duplicate()
    {
        var state = new AppState(SessionId.New("vis-xws"));
        var wsA = state.CreateWorkspace("/tmp/a", "a");
        var tabA = state.GetTab(wsA.FocusedTabId!.Value)!;
        var hidden = new PaneId("p-hidden");
        state.RegisterPane(MakePane(hidden, tabA.Id, wsA.Id, PanePlacement.Hidden));
        var wsB = state.CreateWorkspace("/tmp/b", "b");
        var tabB = state.GetTab(wsB.FocusedTabId!.Value)!;
        var neighbour = new PaneId("p-b");
        state.RegisterPane(MakePane(neighbour, tabB.Id, wsB.Id, PanePlacement.Tiled));
        var svc = new PaneVisibilityService(state);

        var result = svc.ShowTiled(new PaneShowTiledRequest
        {
            PaneId = hidden,
            TabId = tabB.Id,
            TargetPaneId = neighbour,
            Direction = "right",
        });

        Assert.True(result.IsOk);
        var pane = state.GetPane(hidden)!;
        Assert.Equal(tabB.Id.Value, pane.TabId.Value);
        Assert.Equal(wsB.Id.Value, pane.WorkspaceId.Value);
        var dest = state.GetTab(tabB.Id)!;
        var source = state.GetTab(tabA.Id)!;
        Assert.Empty(source.HiddenPaneIds);
        Assert.DoesNotContain(source.PaneIds, p => p.Value == hidden.Value);
        Assert.False(LayoutTreeOperations.ContainsPane(source.LayoutRoot, hidden));
        Assert.Equal(1, dest.PaneIds.Count(p => p.Value == hidden.Value));
        Assert.Contains(dest.PaneIds, p => p.Value == neighbour.Value);
        Assert.True(LayoutTreeOperations.ContainsPane(dest.LayoutRoot, hidden));
        Assert.Equal(2, LayoutTreeOperations.Leaves(dest.LayoutRoot).Count);
    }

    [Fact]
    public void Show_preserves_unrelated_tab_zoom_focus_and_layout()
    {
        var state = new AppState(SessionId.New("vis-zoom"));
        var ws = state.CreateWorkspace("/tmp", "ws");
        var tabA = state.GetTab(ws.FocusedTabId!.Value)!;
        var hidden = new PaneId("p-hidden");
        state.RegisterPane(MakePane(hidden, tabA.Id, ws.Id, PanePlacement.Hidden));
        var tabB = state.CreateTab(ws.Id, "other", focus: false);
        var left = new PaneId("p-left");
        var right = new PaneId("p-right");
        state.RegisterPane(MakePane(left, tabB.Id, ws.Id, PanePlacement.Tiled));
        state.RegisterPane(MakePane(right, tabB.Id, ws.Id, PanePlacement.Tiled));
        var zoomed = state.SetTabLayout(
            tabB.Id,
            state.GetTab(tabB.Id)!.LayoutRoot,
            zoomed: true,
            zoomedPaneId: left,
            focusedPaneId: right);
        Assert.NotNull(zoomed);
        var otherBefore = state.GetTab(tabB.Id)!;
        var svc = new PaneVisibilityService(state);

        Assert.True(svc.ShowTiled(new PaneShowTiledRequest { PaneId = hidden }).IsOk);

        var other = state.GetTab(tabB.Id)!;
        Assert.Equal(otherBefore.PaneIds.Select(p => p.Value), other.PaneIds.Select(p => p.Value));
        Assert.Equal(otherBefore.FocusedPaneId?.Value, other.FocusedPaneId?.Value);
        Assert.True(other.Zoomed);
        Assert.Equal(left.Value, other.ZoomedPaneId?.Value);
        Assert.Equal(otherBefore.LayoutRoot!.ToCanonicalJson(true), other.LayoutRoot!.ToCanonicalJson(true));
        Assert.DoesNotContain(other.PaneIds, p => p.Value == hidden.Value);
        Assert.DoesNotContain(other.HiddenPaneIds, p => p.Value == hidden.Value);
    }

    [Fact]
    public void Stale_show_and_hide_are_unchanged()
    {
        var (svc, _, _, hidden) = GraphWithHidden();
        Assert.True(svc.ShowTiled(new PaneShowTiledRequest { PaneId = hidden }).IsOk);

        var again = svc.ShowTiled(new PaneShowTiledRequest { PaneId = hidden });
        Assert.True(again.IsOk);
        Assert.False(again.Value.Changed);

        Assert.True(svc.HideTiled(new PaneHideRequest { PaneId = hidden }).IsOk);
        var hideAgain = svc.HideTiled(new PaneHideRequest { PaneId = hidden });
        Assert.True(hideAgain.IsOk);
        Assert.False(hideAgain.Value.Changed);
    }

    private static (PaneVisibilityService Svc, AppState State, TabId Tab, PaneId Hidden)
        GraphWithHidden()
    {
        var state = new AppState(SessionId.New("vis"));
        var ws = state.CreateWorkspace("/tmp", "ws");
        var tab = state.GetTab(ws.FocusedTabId!.Value)!;
        var hidden = new PaneId("p-hidden");
        state.RegisterPane(MakePane(hidden, tab.Id, ws.Id, PanePlacement.Hidden, cols: 88, rows: 22, gen: 4));
        return (new PaneVisibilityService(state), state, tab.Id, hidden);
    }

    private static (PaneVisibilityService Svc, AppState State, TabId Tab, PaneId Hidden, PaneId Tiled)
        GraphWithTiledAndHidden()
    {
        var state = new AppState(SessionId.New("vis-occ"));
        var ws = state.CreateWorkspace("/tmp", "ws");
        var tab = state.GetTab(ws.FocusedTabId!.Value)!;
        var tiled = new PaneId("p-tiled");
        var hidden = new PaneId("p-hidden");
        state.RegisterPane(MakePane(tiled, tab.Id, ws.Id, PanePlacement.Tiled));
        state.RegisterPane(MakePane(hidden, tab.Id, ws.Id, PanePlacement.Hidden, cols: 90, rows: 30, gen: 2));
        return (new PaneVisibilityService(state), state, tab.Id, hidden, tiled);
    }

    private static PaneState MakePane(
        PaneId id,
        TabId tab,
        WorkspaceId ws,
        PanePlacement placement,
        int cols = 80,
        int rows = 24,
        int gen = 3) =>
        new()
        {
            Id = id,
            TabId = tab,
            WorkspaceId = ws,
            Placement = placement,
            Label = id.Value,
            Cwd = "/tmp",
            Cols = cols,
            Rows = rows,
            OccupantGeneration = gen,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        };

    private static void AssertUnchanged(
        AppState state,
        SessionState before,
        TabId tab,
        PaneId hidden,
        PaneId? tiled = null)
    {
        var after = state.Snapshot();
        Assert.Equal(before.Panes[hidden.Value].Placement, after.Panes[hidden.Value].Placement);
        Assert.Equal(before.Tabs[tab.Value].HiddenPaneIds.Select(p => p.Value),
            after.Tabs[tab.Value].HiddenPaneIds.Select(p => p.Value));
        Assert.Equal(before.Tabs[tab.Value].PaneIds.Select(p => p.Value),
            after.Tabs[tab.Value].PaneIds.Select(p => p.Value));
        Assert.Equal(
            before.Tabs[tab.Value].LayoutRoot?.ToCanonicalJson(true),
            after.Tabs[tab.Value].LayoutRoot?.ToCanonicalJson(true));
        if (tiled is { } id)
            Assert.True(LayoutTreeOperations.ContainsPane(after.Tabs[tab.Value].LayoutRoot, id));
    }
}
