using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachClientViewReconcilerTests
{
    [Fact]
    public void Valid_selection_wins_over_topology_default()
    {
        var topology = TwoWorkspaceTopology();
        var view = new AttachClientView
        {
            ClientId = "cli_a",
            EndpointId = "ep_a",
            BootId = "boot_a",
            FocusedWorkspaceId = "ws_b",
            ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ws_a"] = "tab_a2",
                ["ws_b"] = "tab_b1",
            },
            FocusedPaneIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a2"] = "pane_a2",
                ["tab_b1"] = "pane_b1",
            },
        };

        var next = AttachClientViewReconciler.Reconcile(view, topology);

        Assert.Equal("ws_b", next.FocusedWorkspaceId);
        Assert.Equal("tab_a2", next.ActiveTabIds["ws_a"]);
        Assert.Equal("tab_b1", next.ActiveTabIds["ws_b"]);
        Assert.Equal("pane_a2", next.FocusedPaneIds["tab_a2"]);
    }

    [Fact]
    public void Missing_workspace_uses_topology_then_first_snapshot_order()
    {
        var topology = TwoWorkspaceTopology();
        var view = new AttachClientView
        {
            ClientId = "cli_a",
            EndpointId = "ep_a",
            BootId = "boot_a",
            FocusedWorkspaceId = "ws_gone",
            ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ws_gone"] = "tab_gone",
            },
        };

        var withFocused = AttachClientViewReconciler.Reconcile(view, topology);
        Assert.Equal("ws_a", withFocused.FocusedWorkspaceId);
        Assert.Equal("tab_a1", withFocused.ActiveTabIds["ws_a"]);

        var noFocused = AttachClientViewReconciler.Reconcile(
            view,
            topology with { FocusedWorkspaceId = null });
        Assert.Equal("ws_a", noFocused.FocusedWorkspaceId);
    }

    [Fact]
    public void Missing_tab_and_pane_use_each_fallback_tier()
    {
        var topology = TwoWorkspaceTopology();
        var view = new AttachClientView
        {
            ClientId = "cli_a",
            EndpointId = "ep_a",
            BootId = "boot_a",
            FocusedWorkspaceId = "ws_a",
            ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ws_a"] = "tab_gone",
            },
            FocusedPaneIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tab_a1"] = "pane_gone",
            },
        };

        var next = AttachClientViewReconciler.Reconcile(view, topology);

        Assert.Equal("tab_a1", next.ActiveTabIds["ws_a"]);
        Assert.Equal("pane_a1", next.FocusedPaneIds["tab_a1"]);
        Assert.False(next.ActiveTabIds.ContainsKey("ws_gone"));
        Assert.False(next.FocusedPaneIds.ContainsKey("tab_gone"));
    }

    [Fact]
    public void Empty_topology_and_empty_tab_use_exact_copy()
    {
        var empty = AttachClientViewReconciler.Reconcile(
            new AttachClientView
            {
                ClientId = "cli_a",
                EndpointId = "ep_a",
                BootId = "boot_a",
                FocusedWorkspaceId = "ws_a",
                ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ws_a"] = "tab_a1",
                },
            },
            AttachClientTopology.Empty);
        Assert.Null(empty.FocusedWorkspaceId);
        Assert.Empty(empty.ActiveTabIds);
        Assert.Empty(empty.FocusedPaneIds);
        Assert.Equal(AttachClientViewCopy.EmptyTopology, AttachClientViewCopy.Describe(empty));

        var emptyTab = AttachClientViewReconciler.Reconcile(
            new AttachClientView
            {
                ClientId = "cli_a",
                EndpointId = "ep_a",
                BootId = "boot_a",
                FocusedWorkspaceId = "ws_a",
            },
            new AttachClientTopology
            {
                FocusedWorkspaceId = "ws_a",
                FallbackWorkspaceId = "ws_a",
                WorkspaceOrder = ["ws_a"],
                ActiveTabIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ws_a"] = "tab_empty",
                },
                TabWorkspaceIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tab_empty"] = "ws_a",
                },
                TabOrderByWorkspace = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["ws_a"] = ["tab_empty"],
                },
                PaneOrderByTab = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["tab_empty"] = [],
                },
            });
        Assert.Equal("tab_empty", emptyTab.FocusedTabId());
        Assert.Null(emptyTab.FocusedPaneId());
        Assert.Equal(AttachClientViewCopy.EmptyTab, AttachClientViewCopy.Describe(emptyTab));
    }

    private static AttachClientTopology TwoWorkspaceTopology() =>
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
