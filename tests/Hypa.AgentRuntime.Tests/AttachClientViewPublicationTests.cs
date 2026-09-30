using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachClientViewPublicationTests
{
    [Fact]
    public void Two_clients_keep_independent_tabs()
    {
        var store = new AttachClientViewPublication();
        var topology = SampleTopology();
        Assert.True(store.Bind(Bind("conn_a", "cli_a", generation: 1), topology).IsOk);
        Assert.True(store.Bind(Bind("conn_b", "cli_b", generation: 1), topology).IsOk);

        var first = store.ApplyFocus(Focus("conn_a", "cli_a", 1, tabId: "tab_a2"), topology);
        var second = store.ApplyFocus(Focus("conn_b", "cli_b", 1, tabId: "tab_b1"), topology);

        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        Assert.Equal("tab_a2", store.Get("conn_a")!.FocusedTabId());
        Assert.Equal("tab_b1", store.Get("conn_b")!.FocusedTabId());
        Assert.Equal("tab_a2", first.Value.View.ActiveTabIds["ws_a"]);
        Assert.Equal("tab_b1", second.Value.View.ActiveTabIds["ws_b"]);
    }

    [Fact]
    public void Stale_generation_cannot_overwrite_another_view()
    {
        var store = new AttachClientViewPublication();
        var topology = SampleTopology();
        store.Bind(Bind("conn_old", "cli_a", generation: 1), topology);
        store.Bind(Bind("conn_new", "cli_a", generation: 2), topology);
        store.ApplyFocus(Focus("conn_new", "cli_a", 2, tabId: "tab_a2"), topology);

        var stale = store.ApplyFocus(Focus("conn_old", "cli_a", 1, tabId: "tab_b1"), topology);

        Assert.False(stale.IsOk);
        Assert.True(
            stale.Error.Code is "stale_generation" or "invalid_connection",
            stale.Error.Code);
        Assert.Null(store.Get("conn_old"));
        Assert.Equal("tab_a2", store.Get("conn_new")!.FocusedTabId());
    }

    [Fact]
    public void Detach_releases_server_view_and_inactive_panes_leave_union()
    {
        var store = new AttachClientViewPublication();
        var topology = SampleTopology();
        store.Bind(Bind("conn_a", "cli_a", generation: 1), topology);
        store.Bind(Bind("conn_b", "cli_b", generation: 1), topology);
        store.ApplyFocus(Focus("conn_a", "cli_a", 1, paneId: "pane_a2"), topology);
        store.ApplyFocus(Focus("conn_b", "cli_b", 1, paneId: "pane_b1"), topology);

        var active = new HashSet<string>(StringComparer.Ordinal) { "conn_a", "conn_b" };
        var union = store.CollectActivePaneIds(active.Contains);
        Assert.Equal(["pane_a2", "pane_b1"], union);

        store.Unbind("conn_a");
        active.Remove("conn_a");
        Assert.Null(store.Get("conn_a"));
        Assert.Equal(["pane_b1"], store.CollectActivePaneIds(active.Contains));
        Assert.Equal(["pane_b1"], store.CollectActivePaneIds(id => id == "conn_b"));
    }

    [Fact]
    public void Reconnect_hint_restores_only_topology_valid_ids()
    {
        var store = new AttachClientViewPublication();
        var topology = SampleTopology();
        store.Bind(Bind("conn_1", "cli_a", generation: 1), topology);
        store.ApplyFocus(Focus("conn_1", "cli_a", 1, tabId: "tab_a2", paneId: "pane_a2"), topology);
        var hint = store.Get("conn_1")!;
        store.Unbind("conn_1");

        store.Bind(Bind("conn_2", "cli_a", generation: 2), topology);
        var restored = store.ApplyFocus(
            Focus(
                "conn_2",
                "cli_a",
                2,
                workspaceId: hint.FocusedWorkspaceId,
                tabId: hint.FocusedTabId(),
                paneId: hint.FocusedPaneId()),
            topology);
        Assert.True(restored.IsOk);
        Assert.Equal("tab_a2", restored.Value.View.FocusedTabId());

        var goneTab = SampleTopology() with
        {
            ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ws_a"] = "tab_a1",
                ["ws_b"] = "tab_b1",
            },
            TabWorkspaceIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a1"] = "ws_a",
                ["tab_b1"] = "ws_b",
            },
            TabOrderByWorkspace = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ws_a"] = ["tab_a1"],
                ["ws_b"] = ["tab_b1"],
            },
        };
        var fallback = store.ApplyFocus(
            Focus("conn_2", "cli_a", 2, tabId: "tab_a2", paneId: "pane_a2"),
            goneTab);
        Assert.True(fallback.IsOk);
        Assert.Equal("tab_a1", fallback.Value.View.FocusedTabId());
        Assert.Equal("pane_a1", fallback.Value.View.FocusedPaneId());
    }

    private static AttachClientViewBindRequest Bind(
        string connectionId,
        string clientId,
        ulong generation) =>
        new()
        {
            ConnectionId = connectionId,
            ClientId = clientId,
            EndpointId = "ep_a",
            BootId = "boot_a",
            ConnectionGeneration = generation,
        };

    private static AttachClientViewFocusRequest Focus(
        string connectionId,
        string clientId,
        ulong generation,
        string? workspaceId = null,
        string? tabId = null,
        string? paneId = null) =>
        new()
        {
            ConnectionId = connectionId,
            ClientId = clientId,
            ConnectionGeneration = generation,
            WorkspaceId = workspaceId,
            TabId = tabId,
            PaneId = paneId,
        };

    private static AttachClientTopology SampleTopology() =>
        new()
        {
            FocusedWorkspaceId = "ws_a",
            FallbackWorkspaceId = "ws_a",
            WorkspaceOrder = ["ws_a", "ws_b"],
            ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ws_a"] = "tab_a1",
                ["ws_b"] = "tab_b1",
            },
            TabWorkspaceIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a1"] = "ws_a",
                ["tab_a2"] = "ws_a",
                ["tab_b1"] = "ws_b",
            },
            TabOrderByWorkspace = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["ws_a"] = ["tab_a1", "tab_a2"],
                ["ws_b"] = ["tab_b1"],
            },
            FocusedPaneIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a1"] = "pane_a1",
                ["tab_a2"] = "pane_a2",
                ["tab_b1"] = "pane_b1",
            },
            FallbackPaneIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a1"] = "pane_a1",
                ["tab_a2"] = "pane_a2",
                ["tab_b1"] = "pane_b1",
            },
            PaneTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pane_a1"] = "tab_a1",
                ["pane_a2"] = "tab_a2",
                ["pane_b1"] = "tab_b1",
            },
            PaneOrderByTab = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["tab_a1"] = ["pane_a1"],
                ["tab_a2"] = ["pane_a2"],
                ["tab_b1"] = ["pane_b1"],
            },
        };
}
