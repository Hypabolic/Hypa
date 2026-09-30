using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Worktrees;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class WorktreeDialogRoutingTests
{
    [Fact]
    public async Task MenuAndPrefixUseCapturedWorkspaceNotLaterFocus()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-focused");
        SeedWorkspaces(live, Parent("ws-parent"), Child("ws-child"), Ungrouped("ws-focused"));

        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        Assert.Equal("ws-parent", LastWorkspaceId(port, ProtocolMethods.WorktreeList));
        Assert.Equal(WorktreeDialogKind.Create, live.Worktrees.Kind);
        Assert.Equal("ws-parent", live.Worktrees.Create!.SourceWorkspaceId);

        live.WorkspaceId = "ws-focused";
        Assert.True(live.Worktrees.TryBeginCreate(out var create));
        Assert.Equal("ws-parent", create!.WorkspaceId);
    }

    [Fact]
    public async Task PrefixKeysShareTheSameWorkflow()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"), Child("ws-child"));
        await AttachSession.DispatchLayoutActionAsync(
            new KeyActionRequest(KeyActionId.OpenWorktree),
            live,
            port,
            CancellationToken.None);
        Assert.Equal(WorktreeDialogKind.Open, live.Worktrees.Kind);
        Assert.Equal("ws-parent", live.Worktrees.Open!.SourceWorkspaceId);
        Assert.Equal(AttachClientModePublication.WorktreeToken, LastClientMode(port));
        Assert.True(live.Engine.ExclusiveSurfaceOpen);
        Assert.Equal(AttachClientModePublication.WorktreeToken, live.Dispatcher.ClientMode);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRenew
                && call.Params?["client_mode"]?.GetValue<string>()
                    == AttachClientModePublication.WorktreeToken);
    }

    [Fact]
    public async Task CreateRouteKeepsSlashFromUtf8Bytes()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(
            tty: null,
            live,
            [.. "feature/client-shell\r"u8.ToArray()],
            port,
            CancellationToken.None);
        Assert.Equal(
            "feature/client-shell",
            LastObject(port, ProtocolMethods.WorktreeCreate)?["branch"]?.GetValue<string>());
        Assert.False(live.Worktrees.IsOpen);
    }

    [Fact]
    public async Task OpenSearchKeepsSlashAfterFocusFromUtf8Bytes()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.OpenWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        await AttachSession.HandleWorktreeDialogKeysAsync(
            tty: null,
            live,
            [.. "//tmp"u8.ToArray()],
            port,
            CancellationToken.None);
        Assert.True(live.Worktrees.Open!.SearchFocused);
        Assert.Equal("/tmp", live.Worktrees.Open.Query);
    }

    [Fact]
    public async Task ModalKeysDoNotDispatchPaneActions()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(
            tty: null,
            live,
            [.. "abc\r"u8.ToArray()],
            port,
            CancellationToken.None);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.PaneSendKeys);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.WorktreeCreate);
        Assert.Equal("abc", LastObject(port, ProtocolMethods.WorktreeCreate)?["branch"]?.GetValue<string>());
    }

    [Fact]
    public async Task RepeatedSubmitIsIgnoredUntilErrorClearsBusy()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        live.Worktrees.InsertText("feature");
        live.Worktrees.TryBeginCreate(out _);
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(
            tty: null,
            live,
            [(byte)'\r', (byte)'\r'],
            port,
            CancellationToken.None);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task DirtyRefuseThenCancelKeepsCheckout()
    {
        var port = new WorktreeRecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.WorktreeRemove)
                    throw new ControlPlaneException(-32002, "dirty", WorktreeDialogModel.DirtyRequiresForce);
                return WorktreeListJson("ws-child");
            },
        };
        var live = Live(port, focused: "ws-child");
        SeedWorkspaces(live, Parent("ws-parent"), Child("ws-child"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.RemoveWorktree, "ws-child", live, port, tty: null, CancellationToken.None);
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.True(live.Worktrees.Remove!.ForceConfirmation);
        Assert.True(live.Worktrees.Cancel());
        Assert.False(live.Worktrees.IsOpen);
        Assert.Equal(1, port.Calls.Count(call => call.Method == ProtocolMethods.WorktreeRemove));
        Assert.False(LastObject(port, ProtocolMethods.WorktreeRemove)!["force"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DirtyRefuseThenForceSendsForceTrue()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-child");
        SeedWorkspaces(live, Parent("ws-parent"), Child("ws-child"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.RemoveWorktree, "ws-child", live, port, tty: null, CancellationToken.None);
        port.Handler = (method, _) =>
        {
            if (method == ProtocolMethods.WorktreeRemove)
                throw new ControlPlaneException(-32002, "dirty", WorktreeDialogModel.DirtyRequiresForce);
            return WorktreeListJson("ws-child");
        };
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        port.Handler = null;
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        var remove = Assert.Single(port.Calls, call => call.Method == ProtocolMethods.WorktreeRemove);
        Assert.True(remove.Params?["force"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CreateErrorStaysReviewableAndAllowsRetry()
    {
        var port = new WorktreeRecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.WorktreeCreate)
                    throw new ControlPlaneException(-32002, "create failed", "worktree_create_failed");
                return WorktreeListJson();
            },
        };
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        live.Worktrees.InsertText("feature");
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.Equal(WorktreeDialogKind.Create, live.Worktrees.Kind);
        Assert.Equal("create failed", live.Worktrees.Create!.Error);
        Assert.False(live.Worktrees.Create.Creating);

        port.Handler = null;
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.WorktreeCreate);
        Assert.False(live.Worktrees.IsOpen);
    }

    [Fact]
    public async Task NonDirtyErrorDoesNotOfferForce()
    {
        var port = new WorktreeRecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.WorktreeRemove)
                    throw new ControlPlaneException(-32002, "failed", "worktree_remove_failed");
                return WorktreeListJson("ws-child");
            },
        };
        var live = Live(port, focused: "ws-child");
        SeedWorkspaces(live, Parent("ws-parent"), Child("ws-child"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.RemoveWorktree, "ws-child", live, port, tty: null, CancellationToken.None);
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.False(live.Worktrees.Remove!.ForceConfirmation);
        Assert.Equal("failed", live.Worktrees.Remove.Error);
    }

    [Fact]
    public async Task SuccessfulRetryClearsStaleErrorOverlay()
    {
        var port = new WorktreeRecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.WorktreeCreate)
                    throw new ControlPlaneException(-32002, "create failed", "worktree_create_failed");
                return WorktreeListJson();
            },
        };
        var live = Live(port, focused: "ws-parent");
        live.ChromeEnabled = true;
        live.ResizeLease = "lease-r";
        live.Ui = DesktopUi();
        live.TabHits = [new TabBarTabSpec("t1", "main", true)];
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        live.Worktrees.InsertText("feature");
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.Equal("create failed", live.StatusError);

        var root = new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "main" };
        var failedGeo = AttachSession.ComputeLiveChrome(live, 80, 24, root, zoomed: false, zoomedPaneId: null, focusedPaneId: "p1");
        Assert.True(failedGeo.HasEndpointError);
        var failedHost = StampTerminalOverlay(failedGeo, live.StatusError);
        var paneRow = failedGeo.Content.EndRow - 1;
        Assert.Contains("ERROR", RowText(failedHost, paneRow), StringComparison.Ordinal);
        Assert.Contains("create failed", RowText(failedHost, paneRow), StringComparison.Ordinal);

        port.Handler = null;
        port.Calls.Clear();
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.Null(live.StatusError);
        Assert.False(live.Worktrees.IsOpen);

        var okGeo = AttachSession.ComputeLiveChrome(live, 80, 24, root, zoomed: false, zoomedPaneId: null, focusedPaneId: "p1");
        Assert.False(okGeo.HasEndpointError);
        var okHost = StampTerminalOverlay(okGeo, live.StatusError, paneGlyph: "P");
        Assert.Equal("P", okHost.CellAt(okGeo.Content.Col, paneRow).Text);
        Assert.DoesNotContain("ERROR", RowText(okHost, paneRow), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessRefreshesSnapshotAndFocusesCreatedWorkspace()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        live.Worktrees.InsertText("feature");
        await AttachSession.HandleWorktreeDialogKeysAsync(null, live, [(byte)'\r'], port, CancellationToken.None);
        Assert.False(live.Worktrees.IsOpen);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.UiClientMode
                && call.Params?["client_mode"]?.GetValue<string>()
                    == AttachClientModePublication.WorktreeToken);
        Assert.Equal("terminal", LastClientMode(port));
        Assert.False(live.Engine.ExclusiveSurfaceOpen);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.SessionSnapshot);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.WorkspaceFocus
                && call.Params?["workspace_id"]?.GetValue<string>() == "ws-created");
    }

    [Fact]
    public async Task OpenRowClickSubmitsSelectedCheckout()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.OpenWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        var layout = WorktreeDialogPainter.Measure(live.Worktrees, 80, 24);
        live.Worktrees.Layout = layout;
        var row = layout.Rows[1];
        port.Calls.Clear();
        Assert.True(await AttachSession.TryHandleWorktreeDialogMouseAsync(
            tty: null,
            live,
            [new MouseEvent(MouseButton.Left, MouseAction.Press, row.Rect.Col, row.Rect.Row)],
            port,
            CancellationToken.None));
        Assert.Equal("/repo-feat", LastObject(port, ProtocolMethods.WorktreeOpen)?["path"]?.GetValue<string>());
        Assert.False(live.Worktrees.IsOpen);
    }

    [Fact]
    public async Task ActivePaneOverlayBlocksWorktreeDialog()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        live.Engine.OverlayOpen = true;
        live.Engine.OverlayPaneId = "p-overlay";
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        Assert.False(live.Worktrees.IsOpen);
        Assert.Equal("ui_busy", live.StatusError);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.WorktreeList);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.UiClientMode);
    }

    [Fact]
    public async Task Rejected_worktree_mode_closes_dialog_and_keeps_form_off()
    {
        var port = new WorktreeRecordingPort { RejectWorktreeMode = true };
        var live = Live(port, focused: "ws-parent");
        live.AttachClientId = "conn_owner";
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        Assert.False(live.Worktrees.IsOpen);
        Assert.Equal("ui_busy", live.StatusError);
        Assert.False(live.Engine.ExclusiveSurfaceOpen);
        Assert.True(AttachClientModePublication.TryBeginExternalModal(live, out var busy));
        Assert.Null(busy);
    }

    [Fact]
    public async Task EscCancelRestoresTerminalClientMode()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        Assert.True(live.Worktrees.IsOpen);
        Assert.Equal(AttachClientModePublication.WorktreeToken, LastClientMode(port));
        await AttachSession.HandleWorktreeDialogKeysAsync(
            tty: null,
            live,
            [(byte)0x1b],
            port,
            CancellationToken.None);
        Assert.False(live.Worktrees.IsOpen);
        Assert.Equal("terminal", LastClientMode(port));
        Assert.False(live.Engine.ExclusiveSurfaceOpen);
        Assert.True(AttachClientModePublication.TryBeginExternalModal(live, out var busy));
        Assert.Null(busy);
        live.Worktrees.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 1);
        Assert.False(AttachClientModePublication.TryBeginExternalModal(live, out busy));
        Assert.Equal("ui_busy", busy);
    }

    [Fact]
    public async Task WorktreeClientModeMakesPopupCommandBusy()
    {
        var port = new WorktreeRecordingPort();
        var live = Live(port, focused: "ws-parent");
        SeedWorkspaces(live, Parent("ws-parent"));
        await AttachSession.BeginWorktreeActionAsync(
            KeyActionId.NewWorktree, "ws-parent", live, port, tty: null, CancellationToken.None);
        live.Dispatcher.Commands = [new KeyCommandBinding("prefix+shift+c", "echo popup", "popup")];
        AttachSession.BindCustomCommandContext(live);
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            live.Dispatcher.HandleAsync(
                    new KeyActionRequest(KeyActionId.Command, 0),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal("ui_busy", ex.Message);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.PopupOpen);
    }

    [Fact]
    public void ParentMenuShowsCreateOpen_LinkedMenuShowsDelete()
    {
        var parent = ContextMenuModel.ForWorkspace(
            "ws-parent", 0, 0, 80, 24, isGit: true, isLinkedWorktree: false, hasWorktreeChildren: true);
        Assert.Contains(parent.Items, item => item.Id == ContextMenuModel.NewWorktree && item.Action == KeyActionId.NewWorktree);
        Assert.Contains(parent.Items, item => item.Id == ContextMenuModel.OpenWorktree && item.Action == KeyActionId.OpenWorktree);
        Assert.DoesNotContain(parent.Items, item => item.Id == ContextMenuModel.RemoveWorktree);
        Assert.Contains(parent.Items, item => item.Id == ContextMenuModel.ToggleWorktreeGroup);

        var child = ContextMenuModel.ForWorkspace(
            "ws-child", 0, 0, 80, 24, isGit: true, isLinkedWorktree: true);
        Assert.Contains(child.Items, item => item.Id == ContextMenuModel.RemoveWorktree && item.Action == KeyActionId.RemoveWorktree);
        Assert.DoesNotContain(child.Items, item => item.Id == ContextMenuModel.NewWorktree);
        Assert.Contains(child.Items, item => item.Id == ContextMenuModel.Rename);
        Assert.Contains(child.Items, item => item.Id == ContextMenuModel.Close);
    }

    private static AttachLiveState Live(WorktreeRecordingPort port, string focused)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, focused, "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = focused,
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-i",
            ChromeEnabled = true,
            LastSnapshot = JsonDocument.Parse(
                """{"worktree_directory":"/tmp/hypa-worktrees","focused_workspace_id":"ws-parent"}""").RootElement.Clone(),
        };
        live.Renew.Track("lease-r");
        live.Renew.Track("lease-i");
        return live;
    }

    private static void SeedWorkspaces(AttachLiveState live, params SidebarWorkspaceItem[] workspaces)
    {
        live.SidebarInput = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Workspaces = workspaces,
            FocusedWorkspaceId = live.WorkspaceId,
            Expanded = true,
            RequestedWidth = 26,
        };
    }

    private static SidebarWorkspaceItem Parent(string id) =>
        new() { Id = id, Label = id, WorktreeKey = "repo", WorktreeLabel = "repo" };

    private static SidebarWorkspaceItem Child(string id) =>
        new()
        {
            Id = id,
            Label = id,
            WorktreeKey = "repo",
            WorktreeLabel = "repo",
            IsLinkedWorktree = true,
            Branch = "worktree/feature",
        };

    private static SidebarWorkspaceItem Ungrouped(string id) =>
        new() { Id = id, Label = id };

    private static AttachUiConfig DesktopUi() => new()
    {
        PaneBorders = false,
        PaneOuterBorders = false,
        PaneGaps = false,
        PaneScrollbars = false,
        HideTabBarWhenSingleTab = false,
        TabBarPosition = TabBarPosition.Top,
        TabBarRight = [],
    };

    private static HostFrame StampTerminalOverlay(
        LayoutChromeGeometry geo,
        string? endpointError,
        string paneGlyph = "P")
    {
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        var sink = new HostFrameCellSink(host);
        LayoutChromePainter.Stamp(sink, geo, DesktopUi(), theme: ThemePalette.Catppuccin);
        var style = AssembledStyle.Default;
        for (var row = geo.Content.Row; row < geo.Content.EndRow; row++)
        {
            for (var col = geo.Content.Col; col < geo.Content.EndCol; col++)
                host.StampGlyphRun(col, row, paneGlyph, style, 1);
        }

        var overlay = ModeBarModel.OverlayRow(geo, geo.Cols, geo.Rows);
        ModeBarPainter.Stamp(
            sink,
            AttachClientMode.Terminal,
            geo.TabSlot,
            overlay.Cols,
            geo.Rows,
            row: overlay.Row,
            theme: ThemePalette.Catppuccin,
            originCol: overlay.Col,
            endpointError: endpointError);
        return host;
    }

    private static string RowText(HostFrame host, int row)
    {
        var chars = new char[host.Cols];
        for (var col = 0; col < host.Cols; col++)
        {
            var text = host.CellAt(col, row).Text;
            chars[col] = text.Length > 0 ? text[0] : ' ';
        }

        return new string(chars);
    }

    private static string? LastClientMode(WorktreeRecordingPort port) =>
        LastObject(port, ProtocolMethods.UiClientMode)?["client_mode"]?.GetValue<string>();

    private static string? LastWorkspaceId(WorktreeRecordingPort port, string method) =>
        LastObject(port, method)?["workspace_id"]?.GetValue<string>();

    private static JsonObject? LastObject(WorktreeRecordingPort port, string method) =>
        port.Calls.Last(call => call.Method == method).Params;

    private static JsonElement WorktreeListJson(string? openId = null) =>
        JsonDocument.Parse(
            $$"""
            {
              "source": {
                "repo_key": "repo",
                "repo_name": "repo",
                "repo_root": "/repo",
                "source_checkout_path": "/repo",
                "source_workspace_id": "ws-parent"
              },
              "worktrees": [
                {
                  "path": "/repo",
                  "branch": "main",
                  "is_bare": false,
                  "is_detached": false,
                  "is_prunable": false,
                  "is_linked_worktree": false,
                  "open_workspace_id": "ws-parent",
                  "label": "repo"
                },
                {
                  "path": "/repo-feat",
                  "branch": "worktree/feature",
                  "is_bare": false,
                  "is_detached": false,
                  "is_prunable": false,
                  "is_linked_worktree": true,
                  "open_workspace_id": {{(openId is null ? "null" : $"\"{openId}\"")}},
                  "label": "feature"
                }
              ]
            }
            """).RootElement.Clone();

    private sealed class WorktreeRecordingPort : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public Func<string, JsonObject?, JsonElement>? Handler { get; set; }

        public bool RejectWorktreeMode { get; set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            if (RejectWorktreeMode
                && string.Equals(method, ProtocolMethods.UiClientMode, StringComparison.Ordinal)
                && string.Equals(parameters?["client_mode"]?.GetValue<string>(), "worktree", StringComparison.Ordinal))
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "ui_busy");
            }

            if (Handler is not null)
                return Task.FromResult(Handler(method, parameters));
            return Task.FromResult(method switch
            {
                ProtocolMethods.WorktreeList => WorktreeListJson("ws-child"),
                ProtocolMethods.WorktreeCreate or ProtocolMethods.WorktreeOpen =>
                    JsonDocument.Parse(
                        """{"workspace":{"workspace_id":"ws-created"},"worktree":{"path":"/repo-feat","label":"feature"},"created":true}""")
                        .RootElement.Clone(),
                ProtocolMethods.SessionSnapshot => JsonDocument.Parse(
                    """{"worktree_directory":"/tmp/hypa-worktrees","focused_workspace_id":"ws-created"}""").RootElement.Clone(),
                ProtocolMethods.LayoutExport => JsonDocument.Parse("""{"root":{"type":"pane","pane_id":"p1"}}""").RootElement.Clone(),
                ProtocolMethods.TabList => JsonDocument.Parse("""[]""").RootElement.Clone(),
                _ => JsonDocument.Parse("{}").RootElement.Clone(),
            });
        }
    }
}
