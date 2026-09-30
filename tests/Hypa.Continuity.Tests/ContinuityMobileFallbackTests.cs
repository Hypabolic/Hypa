using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class ContinuityMobileFallbackTests
{
    [Fact]
    public void Compact_unavailable_cubes_keep_unavailable_text()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = false,
            RequestedWidth = 4,
            CubesState = SidebarCubeCatalogState.Unavailable,
        };
        var frame = SidebarSectionComposer.Compose(input, ChromeSectionRegistry.Core());
        Assert.Equal(SidebarCollapseDisplay.Compact, frame.Display);
        Assert.Equal(
            SidebarTokenGrammar.CubesUnavailableText,
            frame.Sections.Single(s => s.Id == SidebarTokenGrammar.CubesId).EmptyText);

        var stubs = SidebarLiveModel.ToStubRows(frame);
        Assert.Contains(
            stubs,
            r => r.Kind is SidebarStubKind.Empty
                && r.Id == SidebarTokenGrammar.CubesId + ":empty"
                && r.Label == SidebarTokenGrammar.CubesUnavailableText);

        var model = MobileSwitcherModel.Build(
            64,
            24,
            stubs,
            [("t1", "one", true)]);
        Assert.Contains(model.Rows, r => r.Label == SidebarTokenGrammar.CubesUnavailableText);
        Assert.DoesNotContain(model.Rows, r => r.Label == SidebarTokenGrammar.CubesEmptyText);
        Assert.DoesNotContain(model.Rows, r => r.Target is MobileSwitcherTarget.Cube);
    }

    [Fact]
    public void Compact_ready_empty_cubes_keep_no_cubes_text()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = false,
            RequestedWidth = 4,
            CubesState = SidebarCubeCatalogState.Ready,
        };
        var stubs = SidebarLiveModel.ToStubRows(
            SidebarSectionComposer.Compose(input, ChromeSectionRegistry.Core()));
        var model = MobileSwitcherModel.Build(
            64,
            24,
            stubs,
            [("t1", "one", true)]);
        Assert.Contains(model.Rows, r => r.Label == SidebarTokenGrammar.CubesEmptyText);
        Assert.DoesNotContain(model.Rows, r => r.Label == SidebarTokenGrammar.CubesUnavailableText);
    }

    [Fact]
    public void Composer_rows_drive_cube_targets()
    {
        var rows = new SidebarStubRow[]
        {
            new(SidebarStubKind.Section, "cubes", "Cubes", 0),
            new(SidebarStubKind.Cube, "plc_1", "Desk", 1),
        };
        var model = MobileSwitcherModel.Build(
            64,
            24,
            rows,
            [("t1", "one", true)],
            focusedWorkspaceId: "w1",
            focusedTabId: "t1",
            focusedPaneId: "p1");
        Assert.Contains(model.Rows, r =>
            r.Target is MobileSwitcherTarget.Close && r.Label == "Cubes");
        var cube = Assert.Single(model.Rows, r => r.Target is MobileSwitcherTarget.Cube);
        Assert.Equal("plc_1", cube.Id);
        Assert.Equal("plc_1", cube.PlacementId);
        Assert.Null(cube.WorkspaceId);
        Assert.Null(cube.PaneId);
        Assert.DoesNotContain(model.Rows, r =>
            r.Target is MobileSwitcherTarget.Workspace && (r.Id == "plc_1" || r.WorkspaceId == "plc_1"));
        var hit = ChromeHitTest.FromSwitcher(cube);
        Assert.Equal(ChromeHitKind.SidebarCube, hit.Kind);
        Assert.Equal("plc_1", hit.PlacementId);
        Assert.Null(hit.PaneId);
        Assert.Null(hit.WorkspaceId);
    }

    [Fact]
    public void Full_profile_pane_menu_holds_transfer()
    {
        var menu = ContextMenuModel.ForPane("p1", 2, 2, 80, 24, continuityEnabled: true);
        Assert.Equal(ContextMenuModel.Transfer, Assert.Single(menu.Items, i => i.Id == ContextMenuModel.Transfer).Id);
        var workspace = ContextMenuModel.ForWorkspace("w2", 2, 2, 80, 24, continuityEnabled: true);
        Assert.Contains(workspace.Items, i => i.Id == ContextMenuModel.Transfer);
        var engine = new MouseEngine();
        var geo = LayoutForWorkspace();
        var ws = geo.SidebarRows.Single(h => h.Kind is SidebarStubKind.Workspace);
        var open = engine.Feed(
            new MouseEvent(MouseButton.Right, MouseAction.Press, ws.Rect.Col, ws.Rect.Row),
            new MouseFeedContext(geo) { ContinuityEnabled = true });
        Assert.Contains(open[0].Menu!.Items, i => i.Id == ContextMenuModel.Transfer);
    }

    private static LayoutChromeGeometry LayoutForWorkspace()
    {
        var rows = new SidebarStubRow[] { new(SidebarStubKind.Workspace, "w2", "space", 0) };
        return LayoutChromeGeometry.Compute(
            80,
            24,
            new Hypa.AgentRuntime.Protocol.Models.LayoutNodeDto
            {
                Type = "split",
                Direction = "right",
                Ratio = 0.5,
                First = new Hypa.AgentRuntime.Protocol.Models.LayoutNodeDto { Type = "pane", PaneId = "p1" },
                Second = new Hypa.AgentRuntime.Protocol.Models.LayoutNodeDto { Type = "pane", PaneId = "p2" },
            },
            false,
            null,
            "p1",
            LayoutChromeGeometry.BareInsets,
            2,
            AttachClientMode.Terminal,
            [("t1", "one", true), ("t2", "two", false)],
            sidebarOpen: true,
            sidebarWidth: 18,
            sidebarRows: rows);
    }
}
