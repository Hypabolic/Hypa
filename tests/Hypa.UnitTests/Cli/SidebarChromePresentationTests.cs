using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SidebarChromePresentationTests
{
    [Fact]
    public void Geometry_PlacesNewAndMenuOnSpacesFooter()
    {
        var geo = ExpandedGeometry();
        Assert.Equal(0.5f, geo.SidebarSectionSplit);
        Assert.NotNull(geo.SpacesBody);
        Assert.NotNull(geo.AgentsBody);
        Assert.NotNull(geo.SectionDivider);

        var spaces = geo.SpacesPane!.Value;
        var body = geo.SpacesBody!.Value;
        Assert.True(geo.HasLeadingDivider(spaces.Row));
        Assert.Equal(spaces.Row + 3, body.Row);
        Assert.Equal(spaces.EndRow - 1, body.EndRow);
        Assert.Equal(geo.DividerRow, geo.SectionDivider!.Value.Row);

        var hits = geo.SidebarRows;
        var header = hits.Single(h => h.Kind is SidebarStubKind.Section && h.Id == "spaces");
        var created = hits.Single(h => h.Kind is SidebarStubKind.New);
        var menu = hits.Single(h => h.Kind is SidebarStubKind.Menu);
        Assert.Equal(spaces.Row + 1, header.Rect.Row);
        Assert.Equal(spaces.EndRow - 1, created.Rect.Row);
        Assert.Equal(spaces.EndRow - 1, menu.Rect.Row);
        Assert.Equal(spaces.Col, created.Rect.Col);
        Assert.True(menu.Rect.Col > created.Rect.Col);
        Assert.True(created.Rect.Row > body.Row);
        Assert.Equal(body.EndRow, created.Rect.Row);

        var workspace = hits.Single(h => h.Kind is SidebarStubKind.Workspace && h.Id == "ws-1");
        Assert.True(body.Contains(workspace.Rect.Col, workspace.Rect.Row));
        Assert.True(workspace.Selected);
    }

    [Fact]
    public void Geometry_ManualSplit_MovesDivider()
    {
        var half = ExpandedGeometry(split: 0.5f);
        var tall = ExpandedGeometry(split: 0.9f);
        Assert.True(tall.SpacesPane!.Value.Rows > half.SpacesPane!.Value.Rows);
        Assert.True(tall.AgentsPane!.Value.Rows < half.AgentsPane!.Value.Rows);
        Assert.Equal(0.9f, tall.SidebarSectionSplit);
    }

    [Fact]
    public void Compact_HidesFooterActions()
    {
        var frame = ComposeFrame(expanded: false);
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p-claude",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarCollapsed: true,
            sidebarFrame: frame);
        Assert.True(geo.SidebarCompact);
        Assert.DoesNotContain(geo.SidebarRows, h => h.Kind is SidebarStubKind.New or SidebarStubKind.Menu);
        Assert.Equal(geo.SpacesPane, geo.SpacesBody);
    }

    [Fact]
    public void HitTest_SectionDivider_WinsOverEmptyRow()
    {
        var geo = ExpandedGeometry();
        var divider = geo.SectionDivider!.Value;
        var hit = ChromeHitTest.Hit(geo, divider.Col + 2, divider.Row);
        Assert.Equal(ChromeHitKind.SidebarSectionDivider, hit?.Kind);
    }

    [Fact]
    public void Wheel_OnlyScrollsBodyRectangles()
    {
        var geo = ExpandedGeometry();
        var engine = new MouseEngine();
        var context = new MouseFeedContext(geo, FocusedPaneId: "p-claude");
        var spaces = geo.SpacesPane!.Value;
        var body = geo.SpacesBody!.Value;

        var headerWheel = engine.Feed(
            new MouseEvent(MouseButton.WheelDown, MouseAction.Wheel, spaces.Col + 1, spaces.Row),
            context);
        Assert.DoesNotContain(headerWheel, r => r.Kind is MouseCommandKind.ScrollSidebar);

        var footerWheel = engine.Feed(
            new MouseEvent(MouseButton.WheelDown, MouseAction.Wheel, spaces.Col + 1, spaces.EndRow - 1),
            context);
        Assert.DoesNotContain(footerWheel, r => r.Kind is MouseCommandKind.ScrollSidebar);

        var bodyWheel = engine.Feed(
            new MouseEvent(MouseButton.WheelDown, MouseAction.Wheel, body.Col + 1, body.Row),
            context);
        var scroll = Assert.Single(bodyWheel);
        Assert.Equal(MouseCommandKind.ScrollSidebar, scroll.Kind);
        Assert.Equal(SidebarPaneSlot.Spaces, scroll.SidebarSlot);

        var agentBody = geo.AgentsBody!.Value;
        var agentWheel = engine.Feed(
            new MouseEvent(MouseButton.WheelUp, MouseAction.Wheel, agentBody.Col + 1, agentBody.Row),
            context);
        var agentScroll = Assert.Single(agentWheel);
        Assert.Equal(SidebarPaneSlot.Agents, agentScroll.SidebarSlot);
    }

    [Fact]
    public void DividerDrag_SetsManualSplit()
    {
        var geo = ExpandedGeometry();
        var engine = new MouseEngine();
        var context = new MouseFeedContext(geo);
        var divider = geo.SectionDivider!.Value;
        var press = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, divider.Col + 2, divider.Row),
            context);
        Assert.Contains(press, r => r.Kind is MouseCommandKind.PreviewSidebarSection);
        Assert.Equal(MouseEngineState.DraggingSidebarSection, engine.State);

        var dragRow = geo.Sidebar!.Value.Row + (int)(geo.Sidebar.Value.Rows * 0.8f);
        var drag = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Drag, divider.Col + 2, dragRow),
            context);
        var preview = Assert.Single(drag);
        Assert.Equal(MouseCommandKind.PreviewSidebarSection, preview.Kind);
        Assert.InRange(preview.Ratio!.Value, 0.1, 0.9);

        var release = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, divider.Col + 2, dragRow),
            context);
        var committed = Assert.Single(release);
        Assert.Equal(MouseCommandKind.SetSidebarSectionSplit, committed.Kind);
        Assert.Equal(preview.Ratio, committed.Ratio);
    }

    [Fact]
    public void Paint_UsesHeadingAndSelectionThemeRoles()
    {
        var geo = ExpandedGeometry();
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            AttachUiConfig.Default,
            theme: ThemePalette.Catppuccin);

        var cubes = geo.ResourcePane!.Value;
        var spaces = geo.SpacesPane!.Value;
        Assert.True(cubes.Row < spaces.Row);
        Assert.Equal(cubes.EndRow, spaces.Row);
        Assert.True(geo.HasLeadingDivider(spaces.Row));
        var cubesDivider = ReadRow(host, spaces.Col, spaces.Row, spaces.Cols);
        Assert.Contains(AttachUiConfig.Default.Glyphs.H, cubesDivider);
        var dividerCell = host.CellAt(spaces.Col, spaces.Row);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.SurfaceDim), dividerCell.Style.Fg);
        var cubesHeading = ReadRow(host, cubes.Col, cubes.Row, cubes.Cols);
        Assert.Contains("cubes", cubesHeading, StringComparison.Ordinal);
        Assert.DoesNotContain("add", cubesHeading, StringComparison.Ordinal);
        var cubesFooter = ReadRow(host, cubes.Col, cubes.EndRow - 1, cubes.Cols);
        Assert.Contains("add", cubesFooter, StringComparison.Ordinal);
        Assert.Contains("share", cubesFooter, StringComparison.Ordinal);
        var heading = ReadRow(host, spaces.Col, spaces.Row + 1, spaces.Cols);
        Assert.Contains("workspaces", heading, StringComparison.Ordinal);
        var headingCell = host.CellAt(spaces.Col + 1, spaces.Row + 1);
        Assert.True(headingCell.Style.Bold);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Overlay0), headingCell.Style.Fg);

        var created = geo.SidebarRows.Single(h => h.Kind is SidebarStubKind.New);
        var newRow = ReadRow(host, created.Rect.Col, created.Rect.Row, created.Rect.Cols);
        Assert.Contains("new", newRow, StringComparison.Ordinal);
        var newCell = FirstGlyph(host, created.Rect, static c => c != ' ');
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Overlay0), newCell.Style.Fg);

        var workspace = geo.SidebarRows.Single(h => h.Kind is SidebarStubKind.Workspace && h.Id == "ws-1");
        var focused = host.CellAt(workspace.Rect.Col, workspace.Rect.Row);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.ActiveRowBg), focused.Style.Bg);

        var agentKindHit = geo.SidebarRows.First(h =>
            h.Kind is SidebarStubKind.Agent
            && h.Id == "p-claude"
            && h.Tokens is { Count: > 0 } tokens
            && tokens.Any(t => t.Id == "agent"));
        var agentKind = FirstGlyph(host, agentKindHit.Rect, static c => c == 'c');
        Assert.True(agentKind.Style.Dim);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Overlay0), agentKind.Style.Fg);
    }

    [Theory]
    [InlineData(SidebarTokenGrammar.Working, '●', "Yellow")]
    [InlineData(SidebarTokenGrammar.Blocked, '◆', "Red")]
    [InlineData(SidebarTokenGrammar.Idle, '○', "Green")]
    [InlineData(SidebarTokenGrammar.Done, '●', "Teal")]
    [InlineData(SidebarTokenGrammar.Unknown, '·', "Overlay0")]
    public void Paint_StateIcon_UsesStatusColours(string state, char icon, string role)
    {
        var geo = ExpandedGeometry(frame: ComposeFrame(agentState: state));
        var host = Paint(geo, ThemePalette.Catppuccin);
        var row = geo.SidebarRows.First(h =>
            h.Kind is SidebarStubKind.Agent
            && h.Id == "p-claude"
            && h.CardRowIndex == 0);
        var cell = FirstGlyph(host, row.Rect, ch => ch == icon);
        Assert.Equal(icon.ToString(), cell.Text);
        var expected = role switch
        {
            "Yellow" => ThemePalette.Catppuccin.Yellow,
            "Red" => ThemePalette.Catppuccin.Red,
            "Green" => ThemePalette.Catppuccin.Green,
            "Teal" => ThemePalette.Catppuccin.Teal,
            _ => ThemePalette.Catppuccin.Overlay0,
        };
        Assert.Equal(HostFrameCellSink.Encode(expected), cell.Style.Fg);
    }

    [Fact]
    public void Paint_GitAheadBehind_UsesGreenAndRed()
    {
        var bound = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.spaces]
            rows = [["git_status"]]
            """);
        Assert.True(bound.IsOk, bound.IsOk ? "" : bound.Error.ToString());
        var ui = AttachUiConfig.Default with { Sidebar = bound.Value.Ui.Sidebar };
        var geo = ExpandedGeometry(
            frame: ComposeFrame(git: WorkspaceGit("\u21912 \u21931"), ui: ui),
            ui: ui);
        var host = Paint(geo, ThemePalette.Catppuccin, ui);
        var row = Assert.Single(CardHits(geo, "ws-1"), h => h.CardRowIndex == 0);
        Assert.Contains(row.Tokens!, t => t.Id == "git_status" && t.Text.Contains('\u2191'));
        var ahead = FirstGlyph(host, row.Rect, static c => c == '\u2191');
        var behind = FirstGlyph(host, row.Rect, static c => c == '\u2193');
        Assert.Equal('\u2191', ahead.Text.Length > 0 ? ahead.Text[0] : ' ');
        Assert.Equal('\u2193', behind.Text.Length > 0 ? behind.Text[0] : ' ');
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Green), ahead.Style.Fg);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Red), behind.Style.Fg);
    }

    [Fact]
    public void Paint_ConfiguredTokenStyle_OverridesDefaultSpan()
    {
        var bound = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents]
            rows = [[{ token = "agent", fg = "#112233", bold = true, dim = false }]]
            """);
        Assert.True(bound.IsOk, bound.IsOk ? "" : bound.Error.ToString());
        var ui = AttachUiConfig.Default with { Sidebar = bound.Value.Ui.Sidebar };
        var geo = ExpandedGeometry(frame: ComposeFrame(ui: ui));
        var host = Paint(geo, ThemePalette.Catppuccin, ui);
        var row = geo.SidebarRows.First(h =>
            h.Kind is SidebarStubKind.Agent
            && h.Id == "p-claude"
            && h.Tokens is { Count: > 0 } tokens
            && tokens.Any(t => t.Id == "agent"));
        var cell = FirstGlyph(host, row.Rect, static c => c == 'c');
        Assert.True(cell.Style.Bold);
        Assert.False(cell.Style.Dim);
        Assert.Equal(HostFrameCellSink.Encode(ThemeColor.Rgb(0x11, 0x22, 0x33)), cell.Style.Fg);
    }

    [Fact]
    public void Paint_FocusedRow_UsesActiveRowBg_InTerminalAndRgb()
    {
        AssertFocusAndNavigation(
            ThemePalette.Terminal,
            focusedId: "ws-1",
            navigatedId: "ws-2");
        AssertFocusAndNavigation(
            ThemePalette.Catppuccin,
            focusedId: "ws-1",
            navigatedId: "ws-2");
    }

    [Fact]
    public void Paint_FocusedWorkspaceCard_FillsMetadataRows()
    {
        var ui = AttachUiConfig.Default with { Glyphs = ChromeGlyphSet.Ascii };
        var geo = ExpandedGeometry(frame: ComposeFrame(git: WorkspaceGit()), ui: ui);
        var host = Paint(geo, ThemePalette.Catppuccin, ui);

        var header = Assert.Single(CardHits(geo, "ws-1"), h => h.CardRowIndex == 0);
        var branch = Assert.Single(CardHits(geo, "ws-1"), h => h.CardRowIndex == 1);
        Assert.Equal(SidebarStubKind.Workspace, header.Kind);
        Assert.Equal(SidebarStubKind.Workspace, branch.Kind);
        Assert.True(header.Selected);
        Assert.False(branch.Selected);
        Assert.Contains("main", header.Label, StringComparison.Ordinal);
        Assert.Contains("feat/agent-runtime", branch.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("main", branch.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("feat/agent-runtime", header.Label, StringComparison.Ordinal);

        AssertRowBg(host, header.Rect, ThemePalette.Catppuccin.ActiveRowBg);
        AssertRowBg(host, branch.Rect, ThemePalette.Catppuccin.ActiveRowBg);

        var headerText = ReadRow(host, header.Rect.Col, header.Rect.Row, header.Rect.Cols);
        var branchText = ReadRow(host, branch.Rect.Col, branch.Rect.Row, branch.Rect.Cols);
        Assert.StartsWith(">", headerText.TrimStart());
        Assert.DoesNotContain(">", branchText, StringComparison.Ordinal);

        var name = FirstGlyph(host, header.Rect, static c => c == 'm');
        Assert.True(name.Style.Bold);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Text), name.Style.Fg);
        var meta = FirstGlyph(host, branch.Rect, static c => c == 'f');
        Assert.True(meta.Style.Dim);
        Assert.False(meta.Style.Bold);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Overlay0), meta.Style.Fg);
    }

    [Fact]
    public void Paint_NextUnselectedWorkspaceCard_KeepsSidebarBg()
    {
        var geo = ExpandedGeometry(frame: ComposeFrame(git: WorkspaceGit()));
        var host = Paint(geo, ThemePalette.Catppuccin);

        var header = Assert.Single(CardHits(geo, "ws-2"), h => h.CardRowIndex == 0);
        var branch = Assert.Single(CardHits(geo, "ws-2"), h => h.CardRowIndex == 1);
        Assert.False(header.Selected);
        Assert.False(branch.Selected);
        Assert.Contains("other", header.Label, StringComparison.Ordinal);
        Assert.Contains("main", branch.Label, StringComparison.Ordinal);

        AssertRowBg(host, header.Rect, ThemePalette.Catppuccin.SidebarBg);
        AssertRowBg(host, branch.Rect, ThemePalette.Catppuccin.SidebarBg);
        Assert.NotEqual(ThemePalette.Catppuccin.SidebarBg, ThemePalette.Catppuccin.ActiveRowBg);
        Assert.NotEqual(ThemePalette.Catppuccin.SidebarBg, ThemePalette.Catppuccin.SelectionBg);
    }

    [Fact]
    public void Paint_ScrollClipsFocusedCard_NextCardStaysUnselected()
    {
        var geo = ExpandedGeometry(frame: ComposeFrame(git: WorkspaceGit()), spacesScroll: 1);
        var host = Paint(geo, ThemePalette.Catppuccin);

        Assert.Empty(CardHits(geo, "ws-1"));
        var header = Assert.Single(CardHits(geo, "ws-2"), h => h.CardRowIndex == 0);
        var branch = Assert.Single(CardHits(geo, "ws-2"), h => h.CardRowIndex == 1);
        Assert.Equal(geo.SpacesBody!.Value.Row, header.Rect.Row);
        Assert.Equal(header.Rect.Row + 1, branch.Rect.Row);
        AssertRowBg(host, header.Rect, ThemePalette.Catppuccin.SidebarBg);
        AssertRowBg(host, branch.Rect, ThemePalette.Catppuccin.SidebarBg);
    }

    [Fact]
    public void Paint_OrdinaryEmptyAgents_LeavesBodyBlank()
    {
        var geo = ExpandedGeometry(frame: ComposeFrame(agents: false));
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            AttachUiConfig.Default,
            theme: ThemePalette.Catppuccin);
        var body = geo.AgentsBody!.Value;
        var text = ReadRow(host, body.Col, body.Row, body.Cols);
        Assert.DoesNotContain("No agents", text, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void MenuAnchor_UsesFooterHit()
    {
        var geo = ExpandedGeometry();
        Assert.True(geo.TryGlobalMenuAnchor(out _, out var row));
        Assert.Equal(geo.SpacesPane!.Value.EndRow, row);
    }

    private static void AssertFocusAndNavigation(ThemePalette theme, string focusedId, string navigatedId)
    {
        var geo = ExpandedGeometry(frame: ComposeFrame(navigatedWorkspaceId: navigatedId, git: WorkspaceGit()));
        var host = Paint(geo, theme);

        var focused = CardHits(geo, focusedId);
        var navigated = CardHits(geo, navigatedId);
        Assert.Equal(2, focused.Count);
        Assert.Equal(2, navigated.Count);
        Assert.NotEqual(focusedId, navigatedId);
        foreach (var hit in focused)
            AssertRowBg(host, hit.Rect, theme.ActiveRowBg);
        foreach (var hit in navigated)
            AssertRowBg(host, hit.Rect, LayoutChromePainter.NavigatedCardBackground(theme));
    }

    private static LayoutChromeGeometry ExpandedGeometry(
        float split = SidebarTwoPaneLayoutPolicy.DefaultSplitRatio,
        SidebarFrame? frame = null,
        int spacesScroll = 0,
        AttachUiConfig? ui = null) =>
        LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p-claude" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p-claude",
            ui ?? AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame ?? ComposeFrame(),
            spacesScroll: spacesScroll,
            sidebarSectionSplit: split);

    private static SidebarFrame ComposeFrame(
        bool expanded = true,
        bool agents = true,
        string? navigatedWorkspaceId = null,
        ISidebarGitStatus? git = null,
        string agentState = SidebarTokenGrammar.Working,
        AttachUiConfig? ui = null)
    {
        return SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = ui ?? AttachUiConfig.Default,
                Expanded = expanded,
                MouseCapture = true,
                RequestedWidth = 26,
                FocusedWorkspaceId = "ws-1",
                FocusedPaneId = agents ? "p-claude" : null,
                NavigatedWorkspaceId = navigatedWorkspaceId,
                Git = git,
                Workspaces =
                [
                    new SidebarWorkspaceItem { Id = "ws-1", Label = "main", Order = 0, Cwd = "/ws-1" },
                    new SidebarWorkspaceItem { Id = "ws-2", Label = "other", Order = 1, Cwd = "/ws-2" },
                ],
                Tabs =
                [
                    new SidebarTabItem { Id = "tab-1", WorkspaceId = "ws-1", Label = "one", Ordinal = 0, FocusedPaneId = "p-claude" },
                    new SidebarTabItem { Id = "tab-2", WorkspaceId = "ws-2", Label = "two", Ordinal = 1 },
                ],
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = "p-shell",
                        TabId = "tab-1",
                        WorkspaceId = "ws-1",
                        Label = "shell",
                        Agent = "shell",
                        State = SidebarTokenGrammar.Idle,
                    },
                    new SidebarPaneItem
                    {
                        Id = "p-claude",
                        TabId = "tab-1",
                        WorkspaceId = "ws-1",
                        Label = "claude",
                        Agent = agents ? "claude" : "shell",
                        State = agentState,
                    },
                ],
            },
            ChromeSectionRegistry.MuxRelease());
    }

    private static MapSidebarGitStatus WorkspaceGit(string status = "") =>
        new(new Dictionary<string, SidebarGitInfo>(StringComparer.Ordinal)
        {
            ["/ws-1"] = new("feat/agent-runtime", status),
            ["/ws-2"] = new("main", ""),
        });

    private static HostFrame Paint(LayoutChromeGeometry geo, ThemePalette theme, AttachUiConfig? ui = null)
    {
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            ui ?? AttachUiConfig.Default,
            theme: theme);
        return host;
    }

    private static IReadOnlyList<SidebarRowHit> CardHits(LayoutChromeGeometry geo, string id)
    {
        var hits = new List<SidebarRowHit>();
        foreach (var hit in geo.SidebarRows)
        {
            if (hit.PaneSlot is not SidebarPaneSlot.Spaces)
                continue;
            if (!string.Equals(hit.Id, id, StringComparison.Ordinal))
                continue;
            if (hit.Kind is not (SidebarStubKind.Workspace or SidebarStubKind.Gap))
                continue;
            hits.Add(hit);
        }

        return hits;
    }

    private static void AssertRowBg(HostFrame host, CellRect rect, ThemeColor expected)
    {
        var packed = HostFrameCellSink.Encode(expected);
        for (var col = rect.Col; col < rect.EndCol; col++)
            Assert.Equal(packed, host.CellAt(col, rect.Row).Style.Bg);
    }

    private static string ReadRow(HostFrame host, int col, int row, int cols)
    {
        var chars = new char[Math.Max(0, cols)];
        for (var i = 0; i < chars.Length; i++)
        {
            var text = host.CellAt(col + i, row).Text;
            chars[i] = text.Length > 0 ? text[0] : ' ';
        }

        return new string(chars);
    }

    private static AssembledCell FirstGlyph(HostFrame host, CellRect rect, Func<char, bool> match)
    {
        for (var c = rect.Col; c < rect.EndCol; c++)
        {
            var cell = host.CellAt(c, rect.Row);
            var ch = cell.Text.Length > 0 ? cell.Text[0] : ' ';
            if (match(ch))
                return cell;
        }

        return host.CellAt(rect.Col, rect.Row);
    }
}
