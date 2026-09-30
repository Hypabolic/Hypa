using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class HostCellSelectionTests
{
    [Fact]
    public void Host_frame_extracts_visible_run()
    {
        var host = new HostFrame();
        host.Resize(20, 3);
        host.StampGlyphRun(2, 1, "hypa-invite:abc", AssembledStyle.Default);
        var text = host.ExtractRange(2, 1, 16, 1);
        Assert.Equal("hypa-invite:abc", text);
    }

    [Fact]
    public void Child_mouse_drag_selects_host_cells()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo, ChildMouseMode: "sgr");
        var startCol = pane.Content.Col + 1;
        var row = pane.Content.Row;
        Assert.Empty(engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, row),
            ctx));
        var drag = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, startCol + 4, row),
            ctx);
        Assert.Equal(MouseEngineState.Selecting, engine.State);
        Assert.True(engine.Selection.HostRange);
        Assert.DoesNotContain(drag, r => r.Kind is MouseCommandKind.ForwardSgr);
        var release = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, startCol + 4, row),
            ctx);
        Assert.Contains(release, r => r.Kind is MouseCommandKind.Yank);
        Assert.DoesNotContain(release, r => r.Kind is MouseCommandKind.ForwardSgr);
    }

    [Fact]
    public void Chrome_label_drag_selects_host_cells()
    {
        var geo = MouseTestGeom.Split(
            sidebarOpen: true,
            rows: [new SidebarStubRow(SidebarStubKind.Cube, "plc_1", "Intel", 0)]);
        var cube = Assert.Single(geo.SidebarRows, r => r.Kind is SidebarStubKind.Cube);
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo);
        var col = cube.Rect.Col + 1;
        var row = cube.Rect.Row;
        Assert.Empty(engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, col, row),
            ctx));
        engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, col + 3, row),
            ctx);
        Assert.Equal(MouseEngineState.Selecting, engine.State);
        Assert.True(engine.Selection.HostRange);
        var release = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, col + 3, row),
            ctx);
        Assert.Contains(release, r => r.Kind is MouseCommandKind.Yank);
        Assert.DoesNotContain(release, r => r.Kind is MouseCommandKind.ApplyChromeHit);
    }

    [Fact]
    public void Miss_hit_drag_selects_error_row()
    {
        var geo = MouseTestGeom.Split() with { HasEndpointError = true };
        var engine = new MouseEngine();
        var ctx = new MouseFeedContext(geo);
        Assert.Empty(engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, 1, geo.Rows - 1),
            ctx));
        engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, 8, geo.Rows - 1),
            ctx);
        Assert.Equal(MouseEngineState.Selecting, engine.State);
        Assert.True(engine.Selection.HostRange);
    }

    [Fact]
    public void Modal_blocks_host_cell_select_and_clears_leftover()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var engine = new MouseEngine();
        var open = new MouseFeedContext(geo);
        var blocked = new MouseFeedContext(geo) { ModalBlocksChrome = true };
        var startCol = pane.Content.Col + 1;
        var row = pane.Content.Row;
        Assert.Empty(engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, row),
            blocked));
        engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, startCol + 8, row),
            blocked);
        Assert.False(engine.Selection.HostRange);
        Assert.Equal(MouseEngineState.PendingClick, engine.State);
        engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, startCol + 8, row),
            blocked);

        var leftover = new MouseEngine();
        leftover.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, row),
            open);
        leftover.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, startCol + 8, row),
            open);
        Assert.True(leftover.Selection.HostRange);
        leftover.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, startCol + 8, row),
            blocked);
        Assert.False(leftover.Selection.Active);
        Assert.False(leftover.Selection.HostRange);
    }

    [Fact]
    public async Task Yank_copies_composed_host_cells()
    {
        var geo = MouseTestGeom.Split();
        var pane = geo.Panes.Single(p => p.PaneId == "p1");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), geo);
        live.Host.Resize(geo.Cols, geo.Rows);
        live.Host.StampGlyphRun(
            pane.Content.Col,
            pane.Content.Row,
            "SELECT ME",
            AssembledStyle.Default);
        var ctx = AttachSession.MouseFeedContextFor(live);
        using var linked = new CancellationTokenSource();
        foreach (var ev in new[]
        {
            new MouseEvent(MouseButton.Left, MouseAction.Press, pane.Content.Col, pane.Content.Row),
            new MouseEvent(MouseButton.Left, MouseAction.Drag, pane.Content.Col + 8, pane.Content.Row),
            new MouseEvent(MouseButton.Left, MouseAction.Release, pane.Content.Col + 8, pane.Content.Row),
        })
        {
            foreach (var result in live.Mouse.Feed(ev, ctx))
            {
                _ = await AttachSession.ApplyMouseResultAsync(
                    result,
                    live,
                    MouseTestGeom.ApplyPort(),
                    tty: null,
                    linked,
                    CancellationToken.None);
            }
        }

        Assert.NotNull(live.LastOsc52);
        Assert.Equal(Osc52Yank.Encode("SELECT ME"), live.LastOsc52);
        Assert.False(live.Mouse.Selection.Active);
        Assert.False(live.Mouse.Selection.HostRange);
    }
}
