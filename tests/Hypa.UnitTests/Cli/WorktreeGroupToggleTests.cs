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

public sealed class WorktreeGroupToggleTests
{
    [Fact]
    public void GlyphClick_TogglesGroupWithoutFocusingAndPersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-wt-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileClientViewPreferencesStore(
                new MapEnv(new Dictionary<string, string>
                {
                    [FileAttachConfigLoader.XdgConfigHomeVariable] = root,
                }, home: "/unused-home"));
            var live = Live(store);
            SeedGroup(live);
            var geo = Geometry(live, spacesScroll: 8);
            var parent = Assert.Single(
                geo.SidebarRows,
                hit => hit.Kind is SidebarStubKind.Workspace && hit.Id == "parent" && hit.CardRowIndex == 0);
            Assert.NotNull(parent.GroupToggle);
            Assert.Equal(geo.SpacesBody!.Value.Row, parent.GroupToggle!.Value.Row);

            var engine = new MouseEngine();
            var context = new MouseFeedContext(geo);
            engine.Feed(
                new MouseEvent(
                    MouseButton.Left,
                    MouseAction.Press,
                    parent.GroupToggle.Value.Col,
                    parent.GroupToggle.Value.Row),
                context);
            var release = engine.Feed(
                new MouseEvent(
                    MouseButton.Left,
                    MouseAction.Release,
                    parent.GroupToggle.Value.Col,
                    parent.GroupToggle.Value.Row),
                context);
            var applied = Assert.Single(release, result => result.Kind is MouseCommandKind.ApplyChromeHit);
            Assert.Equal(ChromeHitKind.SidebarWorktreeGroupToggle, applied.Hit?.Kind);
            Assert.Equal("repo", applied.Hit?.GroupKey);
            Assert.Null(ChromeHitApply.Apply(applied.Hit!, 0, 1).FocusWorkspaceId);

            AttachSession.ToggleWorktreeGroup(live, "repo");
            Assert.Contains("repo", live.CollapsedWorktreeGroups!);
            Assert.Equal(["repo"], store.Load().CollapsedGroups ?? []);
            Assert.Equal("ws-focused", live.WorkspaceId);

            var reattached = Live(store);
            AttachSession.RestoreSidebarSectionSplit(reattached);
            Assert.Contains("repo", reattached.CollapsedWorktreeGroups!);
            Assert.Equal(SidebarTwoPaneLayoutPolicy.DefaultSplitRatio, reattached.SidebarSectionSplit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GlyphPaint_UsesAccentOnScrolledParentRow()
    {
        var live = Live();
        SeedGroup(live);
        var geo = Geometry(live, spacesScroll: 8);
        var parent = Assert.Single(
            geo.SidebarRows,
            hit => hit.Kind is SidebarStubKind.Workspace && hit.Id == "parent" && hit.CardRowIndex == 0);
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            AttachUiConfig.Default,
            theme: ThemePalette.Catppuccin);
        var cell = host.CellAt(parent.GroupToggle!.Value.Col, parent.GroupToggle.Value.Row);
        Assert.Equal(AttachUiConfig.Default.Glyphs.ToggleCollapse.ToString(), cell.Text);
        Assert.Equal(HostFrameCellSink.Encode(ThemePalette.Catppuccin.Accent), cell.Style.Fg);

        AttachSession.ToggleWorktreeGroup(live, "repo");
        var collapsedGeo = Geometry(live, spacesScroll: 8);
        host.Resize(collapsedGeo.Cols, collapsedGeo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            collapsedGeo,
            AttachUiConfig.Default,
            theme: ThemePalette.Catppuccin);
        var collapsedParent = Assert.Single(
            collapsedGeo.SidebarRows,
            hit => hit.Kind is SidebarStubKind.Workspace && hit.Id == "parent" && hit.CardRowIndex == 0);
        var collapsedCell = host.CellAt(
            collapsedParent.GroupToggle!.Value.Col,
            collapsedParent.GroupToggle.Value.Row);
        Assert.Equal(AttachUiConfig.Default.Glyphs.ToggleExpand.ToString(), collapsedCell.Text);
        Assert.DoesNotContain(
            collapsedGeo.SidebarRows,
            hit => hit.Kind is SidebarStubKind.Workspace && hit.Id == "child");
    }

    [Fact]
    public void SplitPersist_KeepsCollapsedGroups()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-wt-split-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileClientViewPreferencesStore(
                new MapEnv(new Dictionary<string, string>
                {
                    [FileAttachConfigLoader.XdgConfigHomeVariable] = root,
                }, home: "/unused-home"));
            var live = Live(store);
            live.CollapsedWorktreeGroups = new HashSet<string>(StringComparer.Ordinal) { "repo" };
            live.SidebarSectionSplit = 0.8f;
            live.SidebarSectionSplitSource = SidebarSectionSplitSource.Manual;
            AttachSession.PersistSidebarSectionSplit(live);

            var loaded = store.Load();
            Assert.Equal(0.8f, loaded.SidebarSectionSplit);
            Assert.Equal(["repo"], loaded.CollapsedGroups ?? []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AttachLiveState Live(IClientViewPreferencesStore? store = null)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = null!,
            WorkspaceId = "ws-focused",
            ChromeEnabled = true,
            ClientViewPreferences = store,
        };
    }

    private static void SeedGroup(AttachLiveState live)
    {
        var workspaces = new List<SidebarWorkspaceItem>();
        for (var i = 0; i < 8; i++)
            workspaces.Add(new() { Id = $"plain-{i}", Label = $"plain-{i}", Order = i });
        workspaces.Add(new()
        {
            Id = "parent",
            Label = "parent",
            WorktreeKey = "repo",
            WorktreeLabel = "repo",
            Order = 8,
        });
        workspaces.Add(new()
        {
            Id = "child",
            Label = "child",
            WorktreeKey = "repo",
            WorktreeLabel = "repo",
            IsLinkedWorktree = true,
            Branch = "worktree/feature",
            Order = 9,
        });
        live.SidebarInput = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Workspaces = workspaces,
            FocusedWorkspaceId = "ws-focused",
            Expanded = true,
            MouseCapture = true,
            RequestedWidth = 26,
            CollapsedWorktreeGroups = live.CollapsedWorktreeGroups,
        };
        AttachSession.RecomposeLiveSidebar(live);
    }

    private static LayoutChromeGeometry Geometry(AttachLiveState live, int spacesScroll)
    {
        return LayoutChromeGeometry.Compute(
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
            sidebarFrame: live.SidebarFrame,
            spacesScroll: spacesScroll);
    }

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome { get; } = home;
        public string? AppData => null;
        public bool IsWindows => false;
        public bool IsMacOs => false;
    }
}
