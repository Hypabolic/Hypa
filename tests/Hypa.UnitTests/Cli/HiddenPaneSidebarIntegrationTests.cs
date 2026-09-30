using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class HiddenPaneSidebarIntegrationTests
{
    [Fact]
    public void SubscribeResult_KeepsAttachClientIdDistinctFromSubscription()
    {
        using var doc = JsonDocument.Parse(
            """{"subscription_id":"sub_9","attach_client_id":"conn_4"}""");
        var parsed = AttachSession.ReadSubscribeResult(doc.RootElement);
        Assert.Equal("sub_9", parsed.SubscriptionId);
        Assert.Equal("conn_4", parsed.AttachClientId);
        Assert.NotEqual(parsed.SubscriptionId, parsed.AttachClientId);
    }

    [Fact]
    public void SubscribeResult_DoesNotCopySubscriptionWhenAttachClientMissing()
    {
        using var doc = JsonDocument.Parse("""{"subscription_id":"sub_only"}""");
        var parsed = AttachSession.ReadSubscribeResult(doc.RootElement);
        Assert.Equal("sub_only", parsed.SubscriptionId);
        Assert.Null(parsed.AttachClientId);
    }

    [Fact]
    public void Executor_CreatePaneIsJsonBooleanFalse()
    {
        var plan = HiddenPaneActionPlanner.Plan(
            NewTabRequest(),
            [CatalogChild()]).Value;
        var create = HiddenPaneActionExecutor.ToJson(plan.Calls.Single(c => c.Method == ProtocolMethods.TabCreate));
        Assert.Equal(JsonValueKind.False, create["create_pane"]!.GetValueKind());
        Assert.False(create["create_pane"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Executor_NewTabSuccess_FocusesTabAndWorkspace()
    {
        var port = new RecordingPort
        {
            Handler = (method, _) => method == ProtocolMethods.TabCreate
                ? Json("""{"tab_id":"tab_new","workspace_id":"ws_new"}""")
                : Json("""{"ok":true}"""),
        };
        var plan = HiddenPaneActionPlanner.Plan(NewTabRequest(), [CatalogChild()]).Value;
        var result = await HiddenPaneActionExecutor.ExecuteAsync(plan, port, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("tab_new", result.FocusTabId);
        Assert.Equal("ws_new", result.FocusWorkspaceId);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabCreate);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneShow && c.Params?["tab_id"]?.GetValue<string>() == "tab_new");
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabFocus && c.Params?["tab_id"]?.GetValue<string>() == "tab_new");
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceFocus && c.Params?["workspace_id"]?.GetValue<string>() == "ws_new");
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.TabClose);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneCreate);
    }

    [Fact]
    public async Task Executor_ShowFailure_CleansTabAndRestoresFocus()
    {
        var port = new RecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.TabCreate)
                    return Json("""{"tab_id":"tab_new","workspace_id":"ws_new"}""");
                if (method == ProtocolMethods.PaneShow)
                    throw new InvalidOperationException("show failed");
                if (method == ProtocolMethods.TabClose)
                    return Json("""{"ok":true,"closed":true,"tab_id":"tab_new"}""");
                return Json("""{"ok":true}""");
            },
        };
        var plan = HiddenPaneActionPlanner.Plan(NewTabRequest(), [CatalogChild()]).Value;
        var result = await HiddenPaneActionExecutor.ExecuteAsync(plan, port, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("tab_new", result.CleanedTabId);
        Assert.Equal("old-tab", result.RestoredTabId);
        Assert.Equal("old-ws", result.RestoredWorkspaceId);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.TabGet);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.TabClose
            && c.Params?["tab_id"]?.GetValue<string>() == "tab_new"
            && c.Params?["if_empty"]?.GetValue<bool>() == true);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabFocus && c.Params?["tab_id"]?.GetValue<string>() == "old-tab");
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceFocus && c.Params?["workspace_id"]?.GetValue<string>() == "old-ws");
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneCreate);
    }

    [Fact]
    public async Task Executor_FocusFailureAfterShow_KeepsCreatedTab()
    {
        var port = new RecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.TabCreate)
                    return Json("""{"tab_id":"tab_new","workspace_id":"ws_new"}""");
                if (method is ProtocolMethods.TabFocus or ProtocolMethods.WorkspaceFocus)
                    throw new InvalidOperationException("focus failed");
                return Json("""{"ok":true}""");
            },
        };
        var plan = HiddenPaneActionPlanner.Plan(NewTabRequest(), [CatalogChild()]).Value;
        var result = await HiddenPaneActionExecutor.ExecuteAsync(plan, port, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("tab_new", result.FocusTabId);
        Assert.Equal("ws_new", result.FocusWorkspaceId);
        Assert.Null(result.CleanedTabId);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneShow);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.TabClose);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneCreate);
    }

    [Fact]
    public async Task Executor_CancelledShow_CleansEmptyTabWithoutCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        var port = new RecordingPort
        {
            RejectCancelledCleanup = true,
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.TabCreate)
                {
                    cts.Cancel();
                    return Json("""{"tab_id":"tab_new","workspace_id":"ws_new"}""");
                }

                if (method == ProtocolMethods.PaneShow)
                    throw new OperationCanceledException(cts.Token);
                if (method == ProtocolMethods.TabClose)
                    return Json("""{"ok":true,"closed":true,"tab_id":"tab_new"}""");
                return Json("""{"ok":true}""");
            },
        };
        var plan = HiddenPaneActionPlanner.Plan(NewTabRequest(), [CatalogChild()]).Value;
        var result = await HiddenPaneActionExecutor.ExecuteAsync(plan, port, cts.Token);
        Assert.False(result.Succeeded);
        Assert.Equal("tab_new", result.CleanedTabId);
        var close = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.TabClose);
        Assert.False(close.Token.IsCancellationRequested);
        Assert.True(close.Params?["if_empty"]?.GetValue<bool>());
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.TabGet);
    }

    [Fact]
    public void Menu_SharesActionIds_AndHideDependsOnOverlay()
    {
        var hiddenOnly = HiddenPaneMenuModel.Items(hidden: true, overlayVisible: false);
        Assert.DoesNotContain(hiddenOnly, i => i.Id == HiddenPaneActions.Hide);
        Assert.Equal(HiddenPaneActions.ShowNewTab, hiddenOnly[0].Id);
        Assert.Equal(HiddenPaneActions.Close, hiddenOnly[^1].Id);
        Assert.Equal(KeyActionId.ClosePane, hiddenOnly[^1].Action);

        var overlay = HiddenPaneMenuModel.Items(hidden: true, overlayVisible: true);
        Assert.Contains(overlay, i => i.Id == HiddenPaneActions.Hide);
    }

    [Fact]
    public void GlobalMenu_IncludesHiddenList()
    {
        Assert.Contains(GlobalMenuModel.Items(), i => i.Id == GlobalMenuModel.HiddenPanes);
        var list = HiddenPaneMenuModel.ForList(
            [CatalogChild(), CatalogChild() with { PaneId = "vis", Hidden = false, ParentPaneId = null }],
            0, 0, 40, 12);
        Assert.Equal(ContextMenuKind.HiddenList, list.Kind);
        Assert.Equal(["child"], list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Hits_ToggleAndRowUseStableIdsAfterScroll()
    {
        var frame = SidebarSectionComposer.Compose(TreeInput());
        var geo = GeometryWith(frame);
        var parent = geo.SidebarRows.First(h => h.Id == "parent" && h.Kind is SidebarStubKind.Agent);
        Assert.True(parent.Expandable);
        var toggle = ChromeHitTest.Hit(geo, parent.Rect.Col, parent.Rect.Row);
        Assert.Equal(ChromeHitKind.SidebarTreeToggle, toggle?.Kind);
        Assert.Equal("parent", toggle?.PaneId);

        var child = geo.SidebarRows.First(h => h.Id == "child" && h.Kind is SidebarStubKind.HiddenPane);
        Assert.Equal(SidebarStubKind.HiddenPane, child.Kind);
        var rowHit = ChromeHitTest.Hit(geo, child.Rect.Col + 6, child.Rect.Row);
        Assert.Equal(ChromeHitKind.SidebarHiddenPane, rowHit?.Kind);
        Assert.Equal("child", rowHit?.PaneId);

        var layout = SidebarTwoPaneLayoutPolicy.Expanded(
            geo.Sidebar!.Value,
            SidebarTwoPaneLayoutPolicy.DefaultSplitRatio);
        var scrolled = SidebarHitModel.Hits(geo.Sidebar.Value, layout, frame, 0, 1);
        Assert.Contains(scrolled, h => h.Id == "child");
        Assert.Equal("child", ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.SidebarHiddenPane, PaneId: "child"),
            0,
            1).FocusPaneId);
        Assert.Equal("parent", ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.SidebarTreeToggle, PaneId: "parent"),
            0,
            1).SidebarTreeId);
    }

    [Fact]
    public void KeyboardEnter_AppliesHiddenPaneMenuWithSamePaneId()
    {
        var menu = HiddenPaneMenuModel.ForPane("child", 0, 0, 40, 12, hidden: true, overlayVisible: true);
        var engine = new MouseEngine();
        engine.AdoptOpenMenu(menu);
        var applied = engine.FeedKey(KeyChord.Parse("enter"));
        var result = applied.Single(r => r.Kind is MouseCommandKind.ApplyMenu);
        Assert.Equal("child", result.PaneId);
        Assert.Equal(HiddenPaneActions.ShowNewTab, result.MenuItem?.Id);
        Assert.Equal(ContextMenuKind.HiddenPane, result.Menu?.Kind);
    }

    [Fact]
    public void LeftClick_SelectsHiddenRowAndTogglesParent()
    {
        var geo = GeometryWith(SidebarSectionComposer.Compose(TreeInput()));
        var parent = geo.SidebarRows.First(h => h.Id == "parent" && h.Kind is SidebarStubKind.Agent);
        var child = geo.SidebarRows.First(h => h.Id == "child" && h.Kind is SidebarStubKind.HiddenPane);
        var engine = new MouseEngine();
        engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, child.Rect.Col + 6, child.Rect.Row),
            new MouseFeedContext(geo));
        var select = engine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, child.Rect.Col + 6, child.Rect.Row),
            new MouseFeedContext(geo));
        Assert.Equal(ChromeHitKind.SidebarHiddenPane, select.Single(r => r.Kind is MouseCommandKind.ApplyChromeHit).Hit?.Kind);
        Assert.Equal("child", select[0].Hit?.PaneId);

        var toggleEngine = new MouseEngine();
        toggleEngine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Press, parent.Rect.Col, parent.Rect.Row),
            new MouseFeedContext(geo));
        var toggle = toggleEngine.Feed(
            new MouseEvent(MouseButton.Left, MouseAction.Release, parent.Rect.Col, parent.Rect.Row),
            new MouseFeedContext(geo));
        Assert.Equal(ChromeHitKind.SidebarTreeToggle, toggle.Single(r => r.Kind is MouseCommandKind.ApplyChromeHit).Hit?.Kind);
        Assert.Equal("parent", toggle[0].Hit?.PaneId);
    }

    [Fact]
    public void RightClick_OpensHiddenPaneMenuForSamePaneId()
    {
        var geo = GeometryWith(SidebarSectionComposer.Compose(TreeInput()));
        var child = geo.SidebarRows.First(h => h.Id == "child" && h.Kind is SidebarStubKind.HiddenPane);
        var engine = new MouseEngine();
        var results = engine.Feed(
            new MouseEvent(MouseButton.Right, MouseAction.Press, child.Rect.Col + 6, child.Rect.Row),
            new MouseFeedContext(geo) { OverlayPaneId = "child" });
        var opened = results.Single(r => r.Kind is MouseCommandKind.OpenMenu);
        Assert.Equal(ContextMenuKind.HiddenPane, opened.Menu?.Kind);
        Assert.Equal("child", opened.PaneId);
        Assert.Contains(opened.Menu!.Items, i => i.Id == HiddenPaneActions.Hide);
        Assert.Contains(opened.Menu.Items, i => i.Id == HiddenPaneActions.ShowNewTab);
    }

    [Fact]
    public void Compose_IncludesHiddenShellAndBackground()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            Workspaces = [new SidebarWorkspaceItem { Id = "ws", Label = "main", Order = 0 }],
            Tabs = [new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "one", Ordinal = 0 }],
            Panes =
            [
                new SidebarPaneItem
                {
                    Id = "agent",
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = "agent",
                    Agent = "claude",
                    State = SidebarTokenGrammar.Idle,
                },
                new SidebarPaneItem
                {
                    Id = "orphan-shell",
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = "orph",
                    Agent = "shell",
                    Hidden = true,
                    Placement = PanePlacementWire.Hidden,
                    State = SidebarTokenGrammar.Blocked,
                },
            ],
            FocusedPaneId = "agent",
        };
        var view = new AgentsChromeSectionStrategy().Compose(
            input,
            new ResolvedSidebarSection
            {
                Id = SidebarTokenGrammar.AgentsId,
                Title = "Agents",
                Order = 0,
                Collapsed = false,
                Config = AttachSidebarSectionConfig.AgentsDefault,
            },
            collapsed: false,
            visible: true,
            width: 8,
            SidebarCollapseDisplay.Expanded);
        Assert.Contains(view.Rows, r => r.Id == "agent");
        Assert.Contains(view.Rows, r => r.Id == HiddenPaneIds.BackgroundGroup("ws"));
        Assert.Contains(view.Rows, r => r.Id == "orphan-shell" && r.Kind is SidebarRowKind.HiddenPane);
        Assert.All(view.Rows.Where(r => r.Label.Length > 0), r =>
            Assert.True(SafeDisplayText.Width(r.Label) <= 26));
    }

    private static HiddenPaneActionRequest NewTabRequest() =>
        new()
        {
            Action = HiddenPaneAction.ShowNewTab,
            PaneId = "child",
            CurrentTabId = "old-tab",
            WorkspaceId = "ws",
            PriorTabId = "old-tab",
            PriorWorkspaceId = "old-ws",
            LeaseId = "lease",
        };

    private static HiddenPaneRecord CatalogChild() =>
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

    private static SidebarComposeInput TreeInput() =>
        new()
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            Workspaces = [new SidebarWorkspaceItem { Id = "ws", Label = "main", Order = 0 }],
            Tabs = [new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "one", Ordinal = 0 }],
            Panes =
            [
                new SidebarPaneItem
                {
                    Id = "parent",
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = "parent",
                    Agent = "claude",
                    State = SidebarTokenGrammar.Idle,
                },
                new SidebarPaneItem
                {
                    Id = "child",
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = "child",
                    Agent = "shell",
                    Hidden = true,
                    Placement = PanePlacementWire.Hidden,
                    ParentPaneId = "parent",
                    State = SidebarTokenGrammar.Blocked,
                },
            ],
            FocusedPaneId = "parent",
        };

    private static LayoutChromeGeometry GeometryWith(SidebarFrame frame) =>
        LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "parent" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "parent",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame);

    private static JsonElement Json(string text) =>
        JsonDocument.Parse(text).RootElement.Clone();

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params, CancellationToken Token)> Calls { get; } = [];

        public Func<string, JsonObject?, JsonElement>? Handler { get; set; }

        public bool RejectCancelledCleanup { get; set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters, ct));
            if (RejectCancelledCleanup
                && string.Equals(method, ProtocolMethods.TabClose, StringComparison.Ordinal)
                && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            return Task.FromResult(Handler?.Invoke(method, parameters) ?? Json("""{"ok":true}"""));
        }
    }
}
