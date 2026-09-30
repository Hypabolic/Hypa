using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class PaneLinkActivateMouseTests
{
    [Fact]
    public void Ctrl_left_press_on_pane_inner_sends_activate_without_url()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var snap = MouseTestGeom.Frame("hello", "p1") with { Generation = 7 };
        var ctx = new MouseFeedContext(geo, Snapshot: snap);
        var results = engine.Feed(
            new MouseEvent(
                MouseButton.Left,
                MouseAction.Press,
                pane.Content.Col + 2,
                pane.Content.Row + 1,
                Ctrl: true),
            ctx);
        var activate = Assert.Single(results);
        Assert.Equal(MouseCommandKind.ActivateLink, activate.Kind);
        Assert.Equal("p1", activate.PaneId);
        Assert.Equal(1, activate.ViewportRow);
        Assert.Equal(2, activate.Col);
        Assert.Equal(7L, activate.Generation);
        Assert.Equal(MouseEngineState.Idle, engine.State);
        Assert.Null(activate.Hit);
        Assert.NotNull(activate.SourceEvent);
    }

    [Fact]
    public void Ctrl_click_does_not_forward_child_mouse()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, ChildMouseMode: "sgr");
        var results = engine.Feed(
            new MouseEvent(
                MouseButton.Left,
                MouseAction.Press,
                pane.Content.Col + 1,
                pane.Content.Row,
                Ctrl: true),
            ctx);
        var activate = Assert.Single(results);
        Assert.Equal(MouseCommandKind.ActivateLink, activate.Kind);
        Assert.DoesNotContain(results, r => r.Kind is MouseCommandKind.ForwardSgr);
    }

    [Fact]
    public void Left_press_without_ctrl_still_starts_selection()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, Snapshot: MouseTestGeom.Frame("hello", "p1"));
        var results = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, pane.Content.Col + 1, pane.Content.Row),
            ctx);
        Assert.Empty(results);
        Assert.Equal(MouseEngineState.PendingClick, engine.State);
    }

    [Fact]
    public void Child_mouse_click_without_drag_forwards()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, ChildMouseMode: "sgr");
        var col = pane.Content.Col + 1;
        var row = pane.Content.Row;
        var press = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, col, row),
            ctx);
        Assert.Empty(press);
        Assert.Equal(MouseEngineState.PendingClick, engine.State);
        var release = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, col, row),
            ctx);
        Assert.Contains(release, r => r.Kind is MouseCommandKind.ForwardSgr);
        Assert.DoesNotContain(release, r => r.Kind is MouseCommandKind.Yank);
    }

    [Fact]
    public async Task Attach_ctrl_click_calls_activate_with_cell_and_generation()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var port = MouseTestGeom.ApplyPort();
        JsonObject? activateParams = null;
        port.Handler = (method, p) =>
        {
            if (method == ProtocolMethods.PaneLinkActivate)
            {
                activateParams = p;
                return MouseTestGeom.Parse("""{"url":null,"handled":false}""");
            }

            return MouseTestGeom.ApplyPort().Handler!(method, p);
        };
        var live = MouseTestGeom.ApplyLive(port, geo);
        live.SetPaneFrame(MouseTestGeom.Frame("hello", "p1") with { Generation = 4 });
        var ctx = AttachSession.MouseFeedContextFor(live);
        var ev = new MouseEvent(
            MouseButton.Left,
            MouseAction.Press,
            pane.Content.Col + 2,
            pane.Content.Row + 1,
            Ctrl: true);
        using var linked = new CancellationTokenSource();
        foreach (var result in live.Mouse.Feed(ev, ctx))
        {
            _ = await AttachSession.ApplyMouseResultAsync(
                result, live, port, tty: null, linked, CancellationToken.None);
        }

        Assert.NotNull(activateParams);
        Assert.Equal("p1", activateParams!["pane_id"]!.GetValue<string>());
        Assert.Equal(1, activateParams["viewport_row"]!.GetValue<int>());
        Assert.Equal(2, activateParams["col"]!.GetValue<int>());
        Assert.Equal(4L, activateParams["generation"]!.GetValue<long>());
        Assert.False(activateParams.ContainsKey("url"));
    }
}
