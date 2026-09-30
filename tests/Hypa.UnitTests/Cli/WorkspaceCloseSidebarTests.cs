using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class WorkspaceCloseSidebarTests
{
    [Fact]
    public void Align_focus_copies_remaining_workspace_without_live_pane()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t1", "p1");
        live.ChromeSeed = new ChromeComputeSeed(
            Root: null,
            Zoomed: false,
            ZoomedPaneId: null,
            FocusedPaneId: "p1",
            Cols: 80,
            Rows: 24);
        live.Dispatcher.ApplyFocusFromSnapshot(BothSnapshot());
        Assert.Equal("w1", live.Dispatcher.WorkspaceId);

        AttachSession.AlignLiveFocusFromSnapshot(live, RemainingSnapshot());

        Assert.Equal("w2", live.WorkspaceId);
        Assert.Equal("t2", live.TabId);
        Assert.Equal("p1", live.PaneId);
        Assert.Equal("w2", live.Dispatcher.WorkspaceId);
        Assert.Equal("t2", live.Dispatcher.TabId);
        Assert.Equal("p2", live.Dispatcher.PaneId);
        Assert.Null(live.ChromeSeed);
    }

    [Fact]
    public void Align_focus_is_noop_when_live_workspace_still_listed()
    {
        var port = new RecordingPort();
        var live = Live(port, "w2", "t2", "p2");
        live.Dispatcher.ApplyFocusFromSnapshot(RemainingSnapshot());
        var seed = new ChromeComputeSeed(
            Root: null,
            Zoomed: false,
            ZoomedPaneId: null,
            FocusedPaneId: "p2",
            Cols: 80,
            Rows: 24);
        live.ChromeSeed = seed;

        AttachSession.AlignLiveFocusFromSnapshot(live, RemainingSnapshot());

        Assert.Equal("w2", live.WorkspaceId);
        Assert.Equal("p2", live.PaneId);
        Assert.Same(seed, live.ChromeSeed);
    }

    [Fact]
    public async Task Refresh_chrome_drops_closed_space_when_export_fails()
    {
        var port = new RecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.SessionSnapshot)
                    return RemainingSnapshot();
                if (method == ProtocolMethods.LayoutExport)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        "Tab not found");
                }

                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);

        await AttachSession.RefreshChromeAsync(port, live, tty: null, CancellationToken.None);

        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w1");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w2");
        Assert.DoesNotContain(live.SidebarRows, r => r.Kind is SidebarStubKind.Workspace && r.Id == "w1");
        Assert.DoesNotContain(PaintedWorkspaceIds(live), id => id == "w1");
        Assert.Contains(PaintedWorkspaceIds(live), id => id == "w2");
        Assert.NotNull(live.Chrome);
        Assert.Equal("w2", live.WorkspaceId);
        Assert.Equal("t2", live.TabId);
        Assert.Equal("p1", live.PaneId);
        Assert.NotNull(live.StatusError);
    }

    [Fact]
    public async Task Close_workspace_drops_space_when_persist_fails()
    {
        var port = new RecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.WorkspaceClose)
                {
                    Assert.Equal("w1", parameters?["workspace_id"]?.GetValue<string>());
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.PersistenceUnavailable,
                        "persist failed");
                }

                if (method == ProtocolMethods.SessionSnapshot)
                    return RemainingSnapshot();
                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            AttachSession.DispatchLayoutActionAsync(
                new KeyActionRequest(KeyActionId.CloseWorkspace, TargetId: "w1"),
                live,
                port,
                CancellationToken.None));

        Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, ex.Code);
        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w1");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w2");
        Assert.DoesNotContain(PaintedWorkspaceIds(live), id => id == "w1");
        Assert.Contains(PaintedWorkspaceIds(live), id => id == "w2");
        Assert.NotNull(live.Chrome);
        Assert.NotNull(live.Chrome!.SidebarFrame);
    }

    [Fact]
    public async Task Persist_fail_through_engine_events_focuses_remaining_pane()
    {
        var port = new RecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.WorkspaceClose)
                {
                    Assert.Equal("w1", parameters?["workspace_id"]?.GetValue<string>());
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.PersistenceUnavailable,
                        "persist failed");
                }

                if (method == ProtocolMethods.SessionSnapshot)
                    return RemainingSnapshot();
                if (method == ProtocolMethods.RuntimeLeaseClaim)
                {
                    Assert.Equal("p2", parameters?["pane_id"]?.GetValue<string>());
                    var scope = parameters?["scope"]?.GetValue<string>() ?? "input";
                    return Parse($$"""{"lease_id":"lease-p2-{{scope}}","outcome":"granted"}""");
                }

                if (method == ProtocolMethods.PaneFocus)
                {
                    Assert.Equal("p2", parameters?["pane_id"]?.GetValue<string>());
                    return Parse(
                        """{"pane_id":"p2","tab_id":"t2","workspace_id":"w2"}""");
                }

                if (method == ProtocolMethods.LayoutExport)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        "Tab not found");
                }

                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        live.InputLease = "lease-w1-in";
        live.ResizeLease = "lease-w1-r";
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();

        await AttachSession.ApplyEngineEventsAsync(
            tty,
            live,
            [
                new KeyEngineEvent(
                    KeyEngineEventKind.Dispatch,
                    KeyActionId.CloseWorkspace,
                    TargetId: "w1"),
            ],
            port,
            linked,
            CancellationToken.None);

        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w1");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w2");
        Assert.DoesNotContain(PaintedWorkspaceIds(live), id => id == "w1");
        Assert.Contains(PaintedWorkspaceIds(live), id => id == "w2");
        Assert.Equal("w2", live.WorkspaceId);
        Assert.Equal("t2", live.TabId);
        Assert.Equal("p2", live.PaneId);
        Assert.True(string.IsNullOrEmpty(live.InputLease));
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.RuntimeLeaseClaim);
        Assert.Contains(
            port.Calls,
            c => c.Method == ProtocolMethods.PaneFocus
                && c.Params?["pane_id"]?.GetValue<string>() == "p2");
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneClose);
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("y")]
    public void Confirmed_workspace_close_dispatches_bound_target_before_leave(string key)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var engine = new KeyEngine(table, chrome: new AttachChromePolicy(ConfirmClose: true));
        engine.BindPendingCloseTarget(null, null, "w2");
        var armed = engine.RequestAction(KeyActionId.CloseWorkspace);
        Assert.Contains(
            armed,
            e => e.Kind is KeyEngineEventKind.EnterMode && e.Mode is AttachClientMode.ConfirmClose);
        Assert.DoesNotContain(armed, e => e.Kind is KeyEngineEventKind.Dispatch);

        var events = engine.Feed(KeyChord.Parse(key));
        Assert.Equal(KeyEngineEventKind.Dispatch, events[0].Kind);
        Assert.Equal(KeyActionId.CloseWorkspace, events[0].Action);
        Assert.Equal("w2", events[0].TargetId);
        var leave = events.ToList().FindIndex(
            e => e.Kind is KeyEngineEventKind.LeaveMode && e.Mode is AttachClientMode.ConfirmClose);
        Assert.True(leave > 0);
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("y")]
    public async Task Confirmed_menu_close_drops_target_space_from_painted_chrome(string key)
    {
        var closed = false;
        var port = new RecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.WorkspaceClose)
                {
                    Assert.Equal("w2", parameters?["workspace_id"]?.GetValue<string>());
                    closed = true;
                    return Empty();
                }

                if (method == ProtocolMethods.SessionSnapshot)
                    return closed ? UnfocusedRemainingSnapshot() : BothSnapshot();
                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);
        var item = new ContextMenuItem(ContextMenuModel.Close, "Close", KeyActionId.CloseWorkspace);
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();

        await AttachSession.ApplyContextMenuItemAsync(
            item,
            new MouseEngineResult(MouseCommandKind.ApplyMenu, MenuItem: item, WorkspaceId: "w2"),
            live,
            port,
            tty,
            CancellationToken.None);
        Assert.Equal(AttachClientMode.ConfirmClose, live.Engine.Mode);

        await AttachSession.ApplyEngineEventsAsync(
            tty,
            live,
            live.Engine.Feed(KeyChord.Parse(key)),
            port,
            linked,
            CancellationToken.None);

        Assert.Contains(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w2");
        Assert.DoesNotContain(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w1");
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneClose);
        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w2");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w1");
        Assert.DoesNotContain(PaintedWorkspaceIds(live), id => id == "w2");
        Assert.Contains(PaintedWorkspaceIds(live), id => id == "w1");
        Assert.Equal("w1", live.WorkspaceId);
        Assert.Null(live.PendingMenuFocus);
        Assert.Null(live.PendingCloseWorkspaceId);
        Assert.Null(live.PendingCloseTabId);
        Assert.Null(live.PendingClosePaneId);
        Assert.False(live.PendingCloseArmed);
    }

    [Fact]
    public void Restore_menu_focus_skips_workspace_missing_from_snapshot()
    {
        var live = Live(new RecordingPort(), "w2", "t2", "p2");
        live.LastSnapshot = RemainingSnapshot();
        live.PendingMenuFocus = new MenuFocusRestore("w1", "t1", "p1", null);

        AttachSession.RestoreMenuFocusIfArmed(live);

        Assert.Equal("w2", live.WorkspaceId);
        Assert.Equal("t2", live.TabId);
        Assert.Equal("p2", live.PaneId);
        Assert.Null(live.PendingMenuFocus);
    }

    [Fact]
    public void Restore_menu_focus_keeps_workspace_still_listed()
    {
        var live = Live(new RecordingPort(), "w2", "t2", "p2");
        live.LastSnapshot = BothSnapshot();
        live.PendingMenuFocus = new MenuFocusRestore("w1", "t1", "p1", null);

        AttachSession.RestoreMenuFocusIfArmed(live);

        Assert.Equal("w1", live.WorkspaceId);
        Assert.Equal("t1", live.TabId);
        Assert.Equal("p1", live.PaneId);
        Assert.Null(live.PendingMenuFocus);
    }

    [Fact]
    public async Task Refresh_chrome_skips_export_when_snapshot_has_no_remaining_tab()
    {
        var port = new RecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.SessionSnapshot)
                    return WorkspaceOnlySnapshot();
                if (method == ProtocolMethods.LayoutExport)
                    throw new InvalidOperationException("layout.export must not run");
                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);

        await AttachSession.RefreshChromeAsync(port, live, tty: null, CancellationToken.None);

        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.LayoutExport);
        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w1");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w2");
        Assert.DoesNotContain(PaintedWorkspaceIds(live), id => id == "w1");
        Assert.NotNull(live.Chrome);
    }

    [Fact]
    public async Task Close_unfocused_workspace_calls_close_for_menu_target()
    {
        var closed = false;
        var port = new RecordingPort
        {
            Handler = (method, _) =>
            {
                if (method == ProtocolMethods.WorkspaceClose)
                {
                    closed = true;
                    return Empty();
                }

                if (method == ProtocolMethods.SessionSnapshot)
                    return closed ? UnfocusedRemainingSnapshot() : BothSnapshot();
                return Empty();
            },
        };
        var live = Live(port, "w1", "t1", "p1");
        AttachSession.RebuildSidebar(live, BothSnapshot(), requestGit: false);

        await AttachSession.DispatchLayoutActionAsync(
            new KeyActionRequest(KeyActionId.CloseWorkspace, TargetId: "w2"),
            live,
            port,
            CancellationToken.None);

        Assert.Contains(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w2");
        Assert.DoesNotContain(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w1");
        Assert.DoesNotContain(live.SidebarInput!.Workspaces, w => w.Id == "w2");
        Assert.Contains(live.SidebarInput.Workspaces, w => w.Id == "w1");
        Assert.Equal("w1", live.WorkspaceId);
        Assert.Equal("p1", live.PaneId);
    }

    [Fact]
    public void Chosen_empty_tab_stays_selected()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-empty", "p-gone");
        live.DetachRequested = false;

        AttachSession.FollowMuxFocusWhenTabHasNoLeaf(live, EmptyAndPriorSnapshot());

        Assert.False(live.DetachRequested);
        Assert.Equal("t-empty", live.TabId);
        Assert.Equal("p-gone", live.PaneId);
        Assert.Equal("w1", live.WorkspaceId);
    }

    [Fact]
    public void Emptied_tab_adopts_mux_tab_and_stays_attached()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-empty", "p-gone");
        live.DetachRequested = false;

        AttachSession.FollowMuxFocusWhenTabHasNoLeaf(
            live, EmptyAndPriorSnapshot(), tabEmptiedByRefresh: true);

        Assert.False(live.DetachRequested);
        Assert.Equal("t-prior", live.TabId);
        Assert.Equal("p-shell", live.PaneId);
        Assert.Equal("w1", live.WorkspaceId);
        Assert.Equal("t-prior", live.Dispatcher.TabId);
        Assert.Equal("p-shell", live.Dispatcher.PaneId);
    }

    [Fact]
    public void Closed_tab_event_marks_the_client_tab_emptied()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-empty", "p-gone");
        var closed = Event(
            ProtocolEventTypes.TabLifecycle,
            """{"action":"closed","tab_id":"t-empty"}""");
        var other = Event(
            ProtocolEventTypes.TabLifecycle,
            """{"action":"closed","tab_id":"t-other"}""");
        var hidden = Event(
            ProtocolEventTypes.PanePlacementChanged,
            """{"to":"hidden","tab_id":"t-prior","pane_id":"p-gone"}""");
        var unrelated = Event(
            ProtocolEventTypes.LayoutUpdated,
            """{"tab_id":"t-empty"}""");

        Assert.True(AttachSession.ClientTabEmptiedByEvents(live, [closed]));
        Assert.False(AttachSession.ClientTabEmptiedByEvents(live, [other, unrelated]));
        Assert.True(AttachSession.ClientTabEmptiedByEvents(live, [hidden]));
    }

    [Fact]
    public void Missing_tab_adopts_mux_tab_and_stays_attached()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-closed", "p-gone");
        live.DetachRequested = false;

        AttachSession.FollowMuxFocusWhenTabHasNoLeaf(live, PriorOnlySnapshot());

        Assert.False(live.DetachRequested);
        Assert.Equal("t-prior", live.TabId);
        Assert.Equal("p-shell", live.PaneId);
    }

    [Fact]
    public void Sole_empty_tab_stays_attached()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-only", "p-gone");
        live.DetachRequested = false;

        AttachSession.FollowMuxFocusWhenTabHasNoLeaf(live, SoleEmptySnapshot());

        Assert.False(live.DetachRequested);
        Assert.Equal("t-only", live.TabId);
        Assert.Equal("p-gone", live.PaneId);
    }

    [Fact]
    public void Leaf_tab_stays_when_snapshot_also_has_an_empty_tab()
    {
        var port = new RecordingPort();
        var live = Live(port, "w1", "t-prior", "p-shell");

        AttachSession.FollowMuxFocusWhenTabHasNoLeaf(live, EmptyAndPriorSnapshot());

        Assert.Equal("t-prior", live.TabId);
        Assert.Equal("p-shell", live.PaneId);
        Assert.False(live.DetachRequested);
    }

    private static AttachLiveState Live(
        RecordingPort port,
        string workspaceId,
        string tabId,
        string paneId)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, workspaceId, tabId, paneId, "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = workspaceId,
            TabId = tabId,
            PaneId = paneId,
            ChromeEnabled = true,
            SidebarOpen = true,
            SidebarRequestedWidth = 26,
            SidebarWidth = 26,
        };
    }

    private static JsonElement EmptyAndPriorSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t-empty",
          "mux_focused_workspace_id":"w1",
          "mux_focused_tab_id":"t-prior",
          "mux_focused_pane_id":"p-shell",
          "workspaces":[{"workspace_id":"w1","label":"one"}],
          "tabs":[
            {"tab_id":"t-empty","workspace_id":"w1","focused_pane_id":null,"layout":null},
            {"tab_id":"t-prior","workspace_id":"w1","focused_pane_id":"p-shell","layout":{"type":"pane","pane_id":"p-shell"}}
          ]
        }
        """);

    private static JsonElement PriorOnlySnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t-prior",
          "mux_focused_workspace_id":"w1",
          "mux_focused_tab_id":"t-prior",
          "mux_focused_pane_id":"p-shell",
          "workspaces":[{"workspace_id":"w1","label":"one"}],
          "tabs":[
            {"tab_id":"t-prior","workspace_id":"w1","focused_pane_id":"p-shell","layout":{"type":"pane","pane_id":"p-shell"}}
          ]
        }
        """);

    private static JsonElement SoleEmptySnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t-only",
          "mux_focused_workspace_id":"w1",
          "mux_focused_tab_id":"t-only",
          "workspaces":[{"workspace_id":"w1","label":"one"}],
          "tabs":[
            {"tab_id":"t-only","workspace_id":"w1","layout":null}
          ]
        }
        """);

    private static JsonElement BothSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t1",
          "workspaces":[
            {"workspace_id":"w1","label":"one"},
            {"workspace_id":"w2","label":"two"}
          ],
          "tabs":[
            {"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1"},
            {"tab_id":"t2","workspace_id":"w2","focused_pane_id":"p2"}
          ],
          "panes":[
            {"pane_id":"p1","tab_id":"t1","workspace_id":"w1"},
            {"pane_id":"p2","tab_id":"t2","workspace_id":"w2"}
          ]
        }
        """);

    private static JsonElement UnfocusedRemainingSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t1",
          "workspaces":[
            {"workspace_id":"w1","label":"one"}
          ],
          "tabs":[
            {"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1"}
          ],
          "panes":[
            {"pane_id":"p1","tab_id":"t1","workspace_id":"w1"}
          ]
        }
        """);

    private static JsonElement RemainingSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w2",
          "focused_tab_id":"t2",
          "workspaces":[
            {"workspace_id":"w2","label":"two"}
          ],
          "tabs":[
            {"tab_id":"t2","workspace_id":"w2","focused_pane_id":"p2"}
          ],
          "panes":[
            {"pane_id":"p2","tab_id":"t2","workspace_id":"w2"}
          ]
        }
        """);

    private static JsonElement WorkspaceOnlySnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w2",
          "workspaces":[
            {"workspace_id":"w2","label":"two"}
          ]
        }
        """);

    private static IEnumerable<string> PaintedWorkspaceIds(AttachLiveState live)
    {
        foreach (var frame in new[] { live.SidebarFrame, live.Chrome?.SidebarFrame })
        {
            if (frame is null)
                continue;
            foreach (var pane in frame.Panes)
            {
                foreach (var row in pane.Rows)
                {
                    if (!string.IsNullOrWhiteSpace(row.WorkspaceId))
                        yield return row.WorkspaceId;
                }
            }
        }
    }

    [Fact]
    public async Task Presented_tab_observes_its_pane_and_paints()
    {
        var port = new RecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.SessionSnapshot)
                    return PresentedSnapshot();
                if (method == ProtocolMethods.LayoutExport)
                {
                    Assert.Equal("t-question", parameters?["tab_id"]?.GetValue<string>());
                    return Parse(
                        """
                        {
                          "focused_pane_id":"p-question",
                          "root":{"type":"pane","pane_id":"p-question","label":"ticker"}
                        }
                        """);
                }

                if (method == ProtocolMethods.TabList)
                {
                    return Parse(
                        """
                        [
                          {"tab_id":"t-prior","label":"main"},
                          {"tab_id":"t-question","label":"ticker"}
                        ]
                        """);
                }

                return Empty();
            },
        };
        var live = Live(port, "w1", "t-prior", "p-shell");
        live.RenderSub = "sub-r";
        live.ControlSub = "sub-r";
        live.ObservedPaneIds.Add("p-shell");
        live.SetPaneFrame(MouseTestGeom.Frame("shell", "p-shell", cols: 20));
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);

        await AttachSession.RefreshChromeAsync(
            port,
            live,
            tty,
            CancellationToken.None,
            followMuxFocus: true,
            presentedTabId: "t-question",
            presentedPaneId: "p-question");

        Assert.Equal("t-question", live.TabId);
        Assert.Equal("p-question", live.PaneId);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalObserve
                && call.Params?["pane_id"]?.GetValue<string>() == "p-question"
                && call.Params?["subscription_id"]?.GetValue<string>() == "sub-r");
        var pane = Assert.Single(live.Chrome!.Panes, p => p.PaneId == "p-question");
        Assert.True(pane.Content.Cols > 0 && pane.Content.Rows > 0);

        var payload = new JsonObject
        {
            ["pane_id"] = "p-question",
            ["kind"] = "snapshot",
            ["row_start"] = 0,
            ["row_end"] = pane.Content.Rows,
            ["complete"] = true,
            ["grid_cols"] = pane.Content.Cols,
            ["grid_rows"] = pane.Content.Rows,
            ["generation"] = 1,
            ["occupant_generation"] = 1,
            ["snapshot"] = new JsonObject
            {
                ["cells"] = GlyphGrid(pane.Content.Cols, pane.Content.Rows, "Q"),
                ["cursor"] = new JsonObject
                {
                    ["col"] = 0,
                    ["row"] = 0,
                    ["visible"] = true,
                },
            },
        };
        await AttachSession.HandleRenderEventAsync(
            Event(ProtocolEventTypes.TerminalRender, payload.ToJsonString()),
            port,
            new SnapshotAssembler(),
            tty,
            live,
            CancellationToken.None);

        Assert.Equal("Q", live.Host.CellAt(pane.Content.Col, pane.Content.Row).Text);
    }

    private static JsonArray GlyphGrid(int cols, int rows, string glyph)
    {
        var cells = new JsonArray();
        for (var r = 0; r < rows; r++)
        {
            var row = new JsonArray();
            for (var c = 0; c < cols; c++)
                row.Add(new JsonObject { ["text"] = glyph });
            cells.Add(row);
        }

        return cells;
    }

    private static JsonElement PresentedSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w1",
          "focused_tab_id":"t-question",
          "mux_focused_workspace_id":"w1",
          "mux_focused_tab_id":"t-question",
          "mux_focused_pane_id":"p-question",
          "workspaces":[{"workspace_id":"w1","label":"one"}],
          "tabs":[
            {"tab_id":"t-prior","workspace_id":"w1","focused_pane_id":"p-shell","layout":{"type":"pane","pane_id":"p-shell"}},
            {"tab_id":"t-question","workspace_id":"w1","focused_pane_id":"p-question","layout":{"type":"pane","pane_id":"p-question"}}
          ],
          "panes":[
            {"pane_id":"p-shell","tab_id":"t-prior","workspace_id":"w1"},
            {"pane_id":"p-question","tab_id":"t-question","workspace_id":"w1"}
          ]
        }
        """);

    private static JsonElement Event(string type, string payloadJson) =>
        Parse("{\"params\":{\"type\":\"" + type + "\",\"payload\":" + payloadJson + "}}");

    private static JsonElement Empty() => Parse("{}");

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public Func<string, JsonObject?, JsonElement>? Handler { get; set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            return Task.FromResult(Handler?.Invoke(method, parameters) ?? Empty());
        }
    }
}
