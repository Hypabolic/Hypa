using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachCommandDispatcherTests
{
    [Fact]
    public async Task Workspace_tab_pane_layout_actions_call_rpc()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.WorkspaceList => Arr(Obj("workspace_id", "w1"), Obj("workspace_id", "w2")),
            ProtocolMethods.TabList => Arr(Obj("tab_id", "t1"), Obj("tab_id", "t2")),
            ProtocolMethods.TabCreate => El(Obj(("tab_id", "t2"), ("workspace_id", "w1"), ("focused_pane_id", "p2"))),
            ProtocolMethods.TabFocus => El(Obj(("tab_id", "t2"), ("focused_pane_id", "p2"))),
            ProtocolMethods.LayoutExport => Layout(),
            ProtocolMethods.SessionSnapshot => Snapshot(),
            ProtocolMethods.WorkspaceCreate => El(Obj(("workspace_id", "w2"), ("focused_tab_id", "t9"))),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");

        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.NewWorkspace), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.NewTab), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.NextTab), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.FocusPaneRight), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.SplitVertical), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Zoom), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ClosePane), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.CyclePaneNext), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.SwapPaneLeft), CancellationToken.None);

        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.WorkspaceCreate);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabCreate);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabList);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.TabFocus);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocusDirection);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneSplit);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneZoom);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneClose);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.LayoutExport);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocus);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.PaneSwap
            && c.Params?["direction"]?.GetValue<string>() == "left");
    }

    [Fact]
    public async Task New_tab_and_workspace_pass_prompt_label()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.TabCreate => El(Obj(("tab_id", "t2"), ("workspace_id", "w1"), ("focused_pane_id", "p2"))),
            ProtocolMethods.WorkspaceCreate => El(Obj(("workspace_id", "w2"), ("focused_tab_id", "t9"))),
            ProtocolMethods.SessionSnapshot => Snapshot(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.NewTab, PromptText: "docs"), CancellationToken.None);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.NewWorkspace, PromptText: "lab"), CancellationToken.None);

        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.TabCreate
            && c.Params?["label"]?.GetValue<string>() == "docs");
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceCreate
            && c.Params?["label"]?.GetValue<string>() == "lab");
    }

    [Fact]
    public async Task Resize_pane_calls_set_split_ratio_without_resize_mode()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.LayoutExport => Layout(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ResizePaneRight), CancellationToken.None);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.LayoutSetSplitRatio
            && c.Params?["lease_id"]?.GetValue<string>() == "lease-r");
    }

    [Fact]
    public async Task Right_pane_toward_split_decreases_first_ratio()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.LayoutExport => Layout(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p2", "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ResizePaneLeft), CancellationToken.None);
        var call = port.Calls.Single(c => c.Method == ProtocolMethods.LayoutSetSplitRatio);
        Assert.Equal(0.45, call.Params?["ratio"]?.GetValue<double>());
    }

    [Fact]
    public async Task Resize_direction_matches_herdr_first_child_ratio()
    {
        await AssertRatioAsync(Layout(), "p1", KeyActionId.ResizePaneLeft, 0.45);
        await AssertRatioAsync(Layout(), "p1", KeyActionId.ResizePaneRight, 0.55);
        await AssertRatioAsync(Layout(), "p2", KeyActionId.ResizePaneLeft, 0.45);
        await AssertRatioAsync(Layout(), "p2", KeyActionId.ResizePaneRight, 0.55);
        await AssertRatioAsync(VerticalLayout(), "p1", KeyActionId.ResizePaneUp, 0.45);
        await AssertRatioAsync(VerticalLayout(), "p1", KeyActionId.ResizePaneDown, 0.55);
        await AssertRatioAsync(VerticalLayout(), "p2", KeyActionId.ResizePaneUp, 0.45);
        await AssertRatioAsync(VerticalLayout(), "p2", KeyActionId.ResizePaneDown, 0.55);
    }

    [Fact]
    public async Task Nested_resize_uses_matching_ancestor_axis()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.LayoutExport => NestedLayout(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p2", "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ResizePaneRight), CancellationToken.None);
        var horizontal = port.Calls.Single(c => c.Method == ProtocolMethods.LayoutSetSplitRatio);
        Assert.Equal(0, horizontal.Params?["path"]?.AsArray().Count);

        port.Calls.Clear();
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ResizePaneDown), CancellationToken.None);
        var vertical = port.Calls.Single(c => c.Method == ProtocolMethods.LayoutSetSplitRatio);
        var path = vertical.Params?["path"]?.AsArray();
        Assert.NotNull(path);
        Assert.Single(path!);
        Assert.Equal(1, path[0]!.GetValue<int>());
    }

    [Fact]
    public async Task New_workspace_focuses_created_ids()
    {
        var focused = "w1";
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, p) =>
        {
            if (method == ProtocolMethods.WorkspaceCreate)
            {
                return Parse(
                    """{"workspace_id":"w2","focused_tab_id":"t2","pane":{"pane_id":"p2"}}""");
            }

            if (method == ProtocolMethods.WorkspaceFocus)
            {
                focused = p?["workspace_id"]?.GetValue<string>() ?? focused;
                return Empty();
            }

            if (method == ProtocolMethods.SessionSnapshot)
            {
                return focused == "w2"
                    ? Parse(
                        """{"focused_workspace_id":"w2","focused_tab_id":"t2","tabs":[{"tab_id":"t2","focused_pane_id":"p2"}],"panes":[{"pane_id":"p2"}]}""")
                    : Parse(
                        """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1"}]}""");
            }

            return Empty();
        };

        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.NewWorkspace), CancellationToken.None);

        Assert.Equal("w2", dispatcher.WorkspaceId);
        Assert.Equal("t2", dispatcher.TabId);
        Assert.Equal("p2", dispatcher.PaneId);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceFocus
            && c.Params?["workspace_id"]?.GetValue<string>() == "w2");
        Assert.True(
            port.Calls.FindIndex(c => c.Method == ProtocolMethods.WorkspaceCreate)
            < port.Calls.FindIndex(c => c.Method == ProtocolMethods.WorkspaceFocus));
        var create = port.Calls.First(c => c.Method == ProtocolMethods.WorkspaceCreate);
        Assert.Null(create.Params?["cwd"]);
        Assert.True(create.Params?["create_pane"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Close_workspace_uses_menu_target_not_focused()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1"}]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.CloseWorkspace, TargetId: "w2"),
            CancellationToken.None);

        Assert.Contains(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w2");
        Assert.DoesNotContain(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w1");
        Assert.Equal("w1", dispatcher.WorkspaceId);
        Assert.Equal("t1", dispatcher.TabId);
        Assert.Equal("p1", dispatcher.PaneId);
    }

    [Fact]
    public async Task Close_workspace_without_target_closes_focused()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w2","focused_tab_id":"t2","tabs":[{"tab_id":"t2","focused_pane_id":"p2"}],"panes":[{"pane_id":"p2"}]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.CloseWorkspace),
            CancellationToken.None);

        Assert.Contains(
            port.Calls,
            c => c.Method == ProtocolMethods.WorkspaceClose
                && c.Params?["workspace_id"]?.GetValue<string>() == "w1");
        Assert.Equal("w2", dispatcher.WorkspaceId);
        Assert.Equal("t2", dispatcher.TabId);
        Assert.Equal("p2", dispatcher.PaneId);
    }

    [Fact]
    public async Task Set_move_tab_calls_tab_move_index()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.TabList => Arr(
                Obj("tab_id", "t1"),
                Obj("tab_id", "t2"),
                Obj("tab_id", "t3")),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t2", "p1");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.MoveTabPrevious), CancellationToken.None);
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.MoveTabNext), CancellationToken.None);

        var moves = port.Calls.Where(c => c.Method == ProtocolMethods.TabMove).ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal("t2", moves[0].Params?["tab_id"]?.GetValue<string>());
        Assert.Equal(0, moves[0].Params?["index"]?.GetValue<int>());
        Assert.Equal("t2", moves[1].Params?["tab_id"]?.GetValue<string>());
        Assert.Equal(2, moves[1].Params?["index"]?.GetValue<int>());
    }

    [Fact]
    public async Task Last_pane_after_close_does_not_focus_closed_id()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p2"}],"panes":[{"pane_id":"p2"}]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        dispatcher.Seed("w1", "t1", "p1", "lease-r", lastPaneId: "p2");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ClosePane), CancellationToken.None);
        Assert.Equal("p2", dispatcher.PaneId);
        Assert.NotEqual("p1", dispatcher.LastPaneId);

        port.Calls.Clear();
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.LastPane), CancellationToken.None);
        Assert.DoesNotContain(port.Calls, c =>
            c.Method == ProtocolMethods.PaneFocus
            && c.Params?["pane_id"]?.GetValue<string>() == "p1");
    }

    [Fact]
    public async Task Close_last_pane_clears_pane_id()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1"}],"panes":[]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.ClosePane), CancellationToken.None);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.PaneClose);
        Assert.Null(dispatcher.PaneId);
    }

    [Fact]
    public async Task Switch_workspace_index_focuses_listed_id()
    {
        var focused = "w1";
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, p) =>
        {
            if (method == ProtocolMethods.WorkspaceList)
            {
                return Arr(
                    Obj("workspace_id", "w1"),
                    Obj("workspace_id", "w2"),
                    Obj("workspace_id", "w3"));
            }

            if (method == ProtocolMethods.WorkspaceFocus)
            {
                focused = p?["workspace_id"]?.GetValue<string>() ?? focused;
                return Empty();
            }

            if (method == ProtocolMethods.SessionSnapshot)
            {
                return focused == "w2"
                    ? Parse(
                        """{"focused_workspace_id":"w2","focused_tab_id":"t2","tabs":[{"tab_id":"t2","focused_pane_id":"p2"}],"panes":[{"pane_id":"p2"}]}""")
                    : Snapshot();
            }

            return Empty();
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.SwitchWorkspace, Index: 2),
            CancellationToken.None);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceFocus
            && c.Params?["workspace_id"]?.GetValue<string>() == "w2");
        Assert.Equal("w2", dispatcher.WorkspaceId);
        Assert.True(
            port.Calls.FindIndex(c => c.Method == ProtocolMethods.WorkspaceList)
            < port.Calls.FindIndex(c => c.Method == ProtocolMethods.WorkspaceFocus));
    }

    [Fact]
    public async Task Indexed_tabs_reuse_tab_index_focus()
    {
        var focusedTab = "t1";
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, p) =>
        {
            if (method == ProtocolMethods.TabList)
                return Arr(Obj("tab_id", "t1"), Obj("tab_id", "t2"));
            if (method == ProtocolMethods.TabFocus)
            {
                focusedTab = p?["tab_id"]?.GetValue<string>() ?? focusedTab;
                return focusedTab == "t2"
                    ? El(Obj(("tab_id", "t2"), ("focused_pane_id", "p2")))
                    : El(Obj(("tab_id", "t1"), ("focused_pane_id", "p1")));
            }

            if (method == ProtocolMethods.SessionSnapshot)
            {
                return focusedTab == "t2"
                    ? Parse(
                        """{"focused_workspace_id":"w1","focused_tab_id":"t2","tabs":[{"tab_id":"t2","focused_pane_id":"p2"}],"panes":[{"pane_id":"p2"}]}""")
                    : Snapshot();
            }

            return Empty();
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedTabs, Index: 2),
            CancellationToken.None);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.TabFocus
            && c.Params?["tab_id"]?.GetValue<string>() == "t2");
        Assert.Equal("t2", dispatcher.TabId);

        port.Calls.Clear();
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.SwitchTab, Index: 1),
            CancellationToken.None);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.TabFocus
            && c.Params?["tab_id"]?.GetValue<string>() == "t1");
    }

    [Fact]
    public async Task Focus_agent_index_selects_agent_pane()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1","agent":"claude"},{"pane_id":"p2","agent":"codex"},{"pane_id":"p3"}]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.FocusAgent, Index: 2),
            CancellationToken.None);
        Assert.Equal("p2", dispatcher.PaneId);
        Assert.Equal("p1", dispatcher.LastPaneId);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocus);

        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedAgents, Index: 1),
            CancellationToken.None);
        Assert.Equal("p1", dispatcher.PaneId);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocus);
    }

    [Fact]
    public async Task Focus_agent_index_follows_sidebar_spaces_order()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => DivergentAgentSnapshot(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w2", "t2", "p-b");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.FocusAgent, Index: 1),
            CancellationToken.None);
        Assert.Equal("p-a", dispatcher.PaneId);
        Assert.Equal("p-b", dispatcher.LastPaneId);

        port.Calls.Clear();
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedAgents, Index: 1),
            CancellationToken.None);
        Assert.Equal("p-a", dispatcher.PaneId);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocus);
    }

    [Fact]
    public async Task Focus_agent_index_follows_sidebar_priority_order()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => DivergentAgentSnapshot(),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(
            port,
            "w1",
            "t1",
            "p-a",
            agentPanelSort: AgentPanelSort.Priority);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedAgents, Index: 1),
            CancellationToken.None);
        Assert.Equal("p-block", dispatcher.PaneId);
        Assert.Equal("p-a", dispatcher.LastPaneId);
    }

    [Fact]
    public async Task Cycle_agent_follows_sidebar_spaces_order()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """
                {
                  "focused_workspace_id":"w2",
                  "focused_tab_id":"t2",
                  "workspaces":[
                    {"workspace_id":"w1","label":"A"},
                    {"workspace_id":"w2","label":"B"},
                    {"workspace_id":"w3","label":"C"}
                  ],
                  "tabs":[
                    {"tab_id":"t1","workspace_id":"w1","ordinal":0,"focused_pane_id":"p-a"},
                    {"tab_id":"t2","workspace_id":"w2","ordinal":0,"focused_pane_id":"p-b"},
                    {"tab_id":"t3","workspace_id":"w3","ordinal":0,"focused_pane_id":"p-c"}
                  ],
                  "panes":[
                    {"pane_id":"p-c","tab_id":"t3","workspace_id":"w3","agent":"cursor","state":"idle"},
                    {"pane_id":"p-b","tab_id":"t2","workspace_id":"w2","agent":"codex","state":"idle"},
                    {"pane_id":"p-a","tab_id":"t1","workspace_id":"w1","agent":"claude","state":"idle"}
                  ]
                }
                """),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w2", "t2", "p-b");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.NextAgent),
            CancellationToken.None);
        Assert.Equal("p-c", dispatcher.PaneId);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.PreviousAgent),
            CancellationToken.None);
        Assert.Equal("p-b", dispatcher.PaneId);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.PreviousAgent),
            CancellationToken.None);
        Assert.Equal("p-a", dispatcher.PaneId);
    }

    [Fact]
    public async Task Cycle_agent_from_non_agent_pane_selects_first_or_last()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.SessionSnapshot => Parse(
                """
                {
                  "focused_workspace_id":"w2",
                  "focused_tab_id":"t-shell",
                  "workspaces":[
                    {"workspace_id":"w1","label":"A"},
                    {"workspace_id":"w2","label":"B"},
                    {"workspace_id":"w3","label":"C"}
                  ],
                  "tabs":[
                    {"tab_id":"t1","workspace_id":"w1","ordinal":0,"focused_pane_id":"p-a"},
                    {"tab_id":"t2","workspace_id":"w2","ordinal":0,"focused_pane_id":"p-b"},
                    {"tab_id":"t3","workspace_id":"w3","ordinal":0,"focused_pane_id":"p-c"},
                    {"tab_id":"t-shell","workspace_id":"w2","ordinal":1,"focused_pane_id":"p-shell"}
                  ],
                  "panes":[
                    {"pane_id":"p-c","tab_id":"t3","workspace_id":"w3","agent":"cursor","state":"idle"},
                    {"pane_id":"p-b","tab_id":"t2","workspace_id":"w2","agent":"codex","state":"idle"},
                    {"pane_id":"p-a","tab_id":"t1","workspace_id":"w1","agent":"claude","state":"idle"},
                    {"pane_id":"p-shell","tab_id":"t-shell","workspace_id":"w2"}
                  ]
                }
                """),
            _ => Empty(),
        };

        var next = new AttachCommandDispatcher(port, "w2", "t-shell", "p-shell");
        await next.HandleAsync(
            new KeyActionRequest(KeyActionId.NextAgent),
            CancellationToken.None);
        Assert.Equal("p-a", next.PaneId);

        var previous = new AttachCommandDispatcher(port, "w2", "t-shell", "p-shell");
        await previous.HandleAsync(
            new KeyActionRequest(KeyActionId.PreviousAgent),
            CancellationToken.None);
        Assert.Equal("p-c", previous.PaneId);
    }

    [Fact]
    public async Task Indexed_jump_out_of_range_is_noop()
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.WorkspaceList => Arr(Obj("workspace_id", "w1"), Obj("workspace_id", "w2")),
            ProtocolMethods.TabList => Arr(Obj("tab_id", "t1"), Obj("tab_id", "t2")),
            ProtocolMethods.SessionSnapshot => Parse(
                """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1","agent":"claude"},{"pane_id":"p2","agent":"codex"}]}"""),
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.SwitchWorkspace, Index: 9),
            CancellationToken.None);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedWorkspaces, Index: 0),
            CancellationToken.None);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedTabs, Index: 9),
            CancellationToken.None);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.FocusAgent, Index: 9),
            CancellationToken.None);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.IndexedAgents),
            CancellationToken.None);

        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.WorkspaceFocus);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.TabFocus);
        Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneFocus);
        Assert.Equal("w1", dispatcher.WorkspaceId);
        Assert.Equal("t1", dispatcher.TabId);
        Assert.Equal("p1", dispatcher.PaneId);
    }

    [Fact]
    public async Task Rename_with_prompt_calls_workspace_rename()
    {
        var port = new RecordingAttachCommandPort();
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1");
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.RenameWorkspace), CancellationToken.None);
        Assert.Empty(port.Calls);
        await dispatcher.HandleAsync(
            new KeyActionRequest(KeyActionId.RenameWorkspace, PromptText: "docs"),
            CancellationToken.None);
        Assert.Contains(port.Calls, c =>
            c.Method == ProtocolMethods.WorkspaceRename
            && c.Params?["label"]?.GetValue<string>() == "docs");
    }

    private static JsonElement El(JsonObject obj) => Parse(obj.ToJsonString());

    private static JsonElement Arr(params JsonObject[] items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(item);
        return Parse(arr.ToJsonString());
    }

    private static JsonObject Obj(string name, string value) => new() { [name] = value };

    private static JsonObject Obj(params (string Name, string Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in fields)
            obj[name] = value;
        return obj;
    }

    private static async Task AssertRatioAsync(
        JsonElement layout,
        string paneId,
        KeyActionId action,
        double expected)
    {
        var port = new RecordingAttachCommandPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.LayoutExport => layout,
            _ => Empty(),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", paneId, "lease-r");
        await dispatcher.HandleAsync(new KeyActionRequest(action), CancellationToken.None);
        var call = port.Calls.Single(c => c.Method == ProtocolMethods.LayoutSetSplitRatio);
        Assert.Equal(expected, call.Params?["ratio"]?.GetValue<double>());
    }

    private static JsonElement Layout() => Parse(
        """{"root":{"type":"split","direction":"right","ratio":0.5,"first":{"type":"pane","pane_id":"p1"},"second":{"type":"pane","pane_id":"p2"}}}""");

    private static JsonElement VerticalLayout() => Parse(
        """{"root":{"type":"split","direction":"down","ratio":0.5,"first":{"type":"pane","pane_id":"p1"},"second":{"type":"pane","pane_id":"p2"}}}""");

    private static JsonElement NestedLayout() => Parse(
        """{"root":{"type":"split","direction":"right","ratio":0.5,"first":{"type":"pane","pane_id":"p1"},"second":{"type":"split","direction":"down","ratio":0.5,"first":{"type":"pane","pane_id":"p2"},"second":{"type":"pane","pane_id":"p3"}}}}""");

    private static JsonElement Snapshot() => Parse(
        """{"focused_workspace_id":"w1","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1"}]}""");

    // Snapshot pane order is B, blocked, C, A. Spaces sidebar is A, B, C.
    // Priority sidebar is blocked, then A, B, C.
    private static JsonElement DivergentAgentSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"w2",
          "focused_tab_id":"t2",
          "workspaces":[
            {"workspace_id":"w1","label":"A"},
            {"workspace_id":"w2","label":"B"},
            {"workspace_id":"w3","label":"C"}
          ],
          "tabs":[
            {"tab_id":"t1","workspace_id":"w1","ordinal":0,"focused_pane_id":"p-a"},
            {"tab_id":"t2","workspace_id":"w2","ordinal":0,"focused_pane_id":"p-b"},
            {"tab_id":"t3","workspace_id":"w3","ordinal":0,"focused_pane_id":"p-c"}
          ],
          "panes":[
            {"pane_id":"p-b","tab_id":"t2","workspace_id":"w2","agent":"codex","state":"idle"},
            {"pane_id":"p-block","tab_id":"t2","workspace_id":"w2","agent":"gemini","state":"blocked"},
            {"pane_id":"p-c","tab_id":"t3","workspace_id":"w3","agent":"cursor","state":"idle"},
            {"pane_id":"p-a","tab_id":"t1","workspace_id":"w1","agent":"claude","state":"idle"}
          ]
        }
        """);

    [Fact]
    public async Task Command_missing_index_toasts_without_rpc()
    {
        var port = new RecordingAttachCommandPort();
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1")
        {
            Commands = [new KeyCommandBinding("prefix+shift+c", "echo hi")],
        };
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command), CancellationToken.None)
                .AsTask());
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        Assert.Equal(AttachCommandDispatcher.CustomCommandFailed, ex.Message);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task Command_popup_is_busy_when_settings_help_or_popup_open()
    {
        var port = new RecordingAttachCommandPort();
        var commands = new[]
        {
            new KeyCommandBinding("prefix+shift+c", "echo popup", "popup"),
        };
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1")
        {
            Commands = commands,
            ClientMode = "settings",
        };
        var settings = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None)
                .AsTask());
        Assert.Equal("ui_busy", settings.Message);

        dispatcher.ClientMode = "keybindhelp";
        var help = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None)
                .AsTask());
        Assert.Equal("ui_busy", help.Message);

        dispatcher.ClientMode = AttachClientModePublication.WorktreeToken;
        var worktree = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None)
                .AsTask());
        Assert.Equal("ui_busy", worktree.Message);

        dispatcher.ClientMode = "terminal";
        dispatcher.PopupIsOpen = true;
        var popup = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None)
                .AsTask());
        Assert.Equal("ui_busy", popup.Message);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task Plugin_action_command_calls_action_invoke()
    {
        var port = new RecordingAttachCommandPort();
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1")
        {
            Commands = [new KeyCommandBinding("prefix+a", "copy-context", "plugin_action")],
        };
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None);
        var call = Assert.Single(port.Calls);
        Assert.Equal(ProtocolMethods.PluginActionInvoke, call.Method);
        Assert.Equal("copy-context", call.Params?["action_id"]?.GetValue<string>());
    }

    private static JsonElement Empty() => Parse("{}");

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RecordingAttachCommandPort : IAttachCommandPort
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
