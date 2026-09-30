using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class HiddenPaneActionPlannerTests
{
    [Fact]
    public void ShowNewTab_PlansEmptyTabAndCleanup()
    {
        var plan = HiddenPaneActionPlanner.Plan(Req(HiddenPaneAction.ShowNewTab), [Hidden()]).Value;
        Assert.True(plan.CreateEmptyTab);
        Assert.True(plan.CleanupCreatedTabOnFailure);
        Assert.True(plan.FocusCreatedTab);
        Assert.True(plan.RestoreFocusOnFailure);
        Assert.Equal("old-tab", plan.PriorTabId);
        Assert.Equal("old-ws", plan.PriorWorkspaceId);
        var create = plan.Calls.Single(c => c.Method == ProtocolMethods.TabCreate);
        Assert.False(create.CreatePane);
        Assert.False(create.IsCleanup);
        Assert.Equal(ProtocolMethods.PaneShow, plan.Calls.Single(c => c.Method == ProtocolMethods.PaneShow).Method);
        Assert.Equal(PanePlacementWire.Tiled, plan.Calls.Single(c => c.Method == ProtocolMethods.PaneShow).Mode);
        Assert.True(plan.Calls.Single(c => c.IsCleanup).Method == ProtocolMethods.TabClose);
    }

    [Fact]
    public void Split_UsesSamePaneIdAndDirection()
    {
        var right = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.SplitRight) with { VisibleTargetPaneId = "vis" },
            [Hidden()]).Value;
        Assert.Equal("child", right.PaneId);
        Assert.Equal("right", right.Calls[0].Direction);
        Assert.Equal("vis", right.Calls[0].TargetPaneId);
        var below = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.SplitBelow) with { VisibleTargetPaneId = "vis" },
            [Hidden()]).Value;
        Assert.Equal("down", below.Calls[0].Direction);
        Assert.Equal("child", below.Calls[0].PaneId);
    }

    [Fact]
    public void Split_MissingTarget_IsUsefulError()
    {
        var result = HiddenPaneActionPlanner.Plan(Req(HiddenPaneAction.SplitRight), [Hidden()]);
        Assert.False(result.IsOk);
        Assert.Equal(HiddenPaneActionError.NoVisibleTargetCode, result.Error.Code);
        Assert.Contains("visible pane", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modal_RequiresDistinctAttachClientId()
    {
        var missing = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.ShowModal) with { AttachClientId = null },
            [Hidden()]);
        Assert.False(missing.IsOk);
        Assert.Equal(HiddenPaneActionError.MissingAttachClientCode, missing.Error.Code);

        var ok = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.ShowModal) with { AttachClientId = "conn_1" },
            [Hidden()]).Value;
        Assert.Equal("overlay", ok.Calls[0].Mode);
        Assert.Equal("conn_1", ok.Calls[0].AttachClientId);
        Assert.NotEqual("sub_1", ok.Calls[0].AttachClientId);
    }

    [Fact]
    public void Hide_AllowsOverlayOwnedGraphHiddenPane()
    {
        var hidden = Hidden();
        var blocked = HiddenPaneActionPlanner.Plan(Req(HiddenPaneAction.Hide), [hidden]);
        Assert.False(blocked.IsOk);

        var allowed = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.Hide) with { OverlayVisible = true, AttachClientId = "conn_1" },
            [hidden]).Value;
        Assert.Equal(ProtocolMethods.PaneHide, allowed.Calls[0].Method);
        Assert.Equal("conn_1", allowed.Calls[0].AttachClientId);
        Assert.Equal("child", allowed.Calls[0].PaneId);
    }

    [Fact]
    public void Close_SetsConfirmFlagAndSamePaneId()
    {
        var plan = HiddenPaneActionPlanner.Plan(Req(HiddenPaneAction.Close), [Hidden()]).Value;
        Assert.True(plan.ConfirmClose);
        Assert.Equal(ProtocolMethods.PaneClose, plan.Calls[0].Method);
        Assert.Equal("child", plan.Calls[0].PaneId);
    }

    [Fact]
    public void OverlayVisibility_AdoptsOnlyThisAttachClient()
    {
        Assert.Equal(
            "child",
            HiddenPaneOverlayVisibility.Apply(null, "conn_1", "child", "overlay", "conn_1"));
        Assert.Null(
            HiddenPaneOverlayVisibility.Apply(null, "conn_1", "child", "overlay", "conn_other"));
        Assert.Null(
            HiddenPaneOverlayVisibility.Apply(null, "conn_1", "child", "overlay", "sub_1"));
        Assert.Null(
            HiddenPaneOverlayVisibility.Apply("child", "conn_1", "child", "hidden", "conn_1"));
        var plan = HiddenPaneActionPlanner.Plan(
            Req(HiddenPaneAction.Hide) with { OverlayVisible = true, AttachClientId = "conn_1" },
            [Hidden()]).Value;
        Assert.Equal("conn_1", plan.Calls[0].AttachClientId);
    }

    [Fact]
    public void VisibleTarget_SkipsHiddenAndSelf()
    {
        var panes = new[]
        {
            Hidden(),
            Hidden() with { PaneId = "vis", Hidden = false, Placement = PanePlacementWire.Tiled, TabId = "old-tab" },
            Hidden() with { PaneId = "other-tab", Hidden = false, Placement = PanePlacementWire.Tiled, TabId = "x" },
        };
        Assert.Equal("vis", HiddenPaneVisibleTarget.Find(panes, "old-tab", "child"));
        Assert.Null(HiddenPaneVisibleTarget.Find(panes, "old-tab", "vis"));
    }

    private static HiddenPaneActionRequest Req(HiddenPaneAction action) =>
        new()
        {
            Action = action,
            PaneId = "child",
            CurrentTabId = "old-tab",
            WorkspaceId = "ws",
            PriorTabId = "old-tab",
            PriorWorkspaceId = "old-ws",
            LeaseId = "lease",
        };

    private static HiddenPaneRecord Hidden() =>
        new()
        {
            PaneId = "child",
            TabId = "tab",
            WorkspaceId = "ws",
            Label = "child",
            Hidden = true,
            Placement = PanePlacementWire.Hidden,
            ParentPaneId = "parent",
        };
}
