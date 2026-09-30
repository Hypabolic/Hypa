using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachClientViewHintTests
{
    [Fact]
    public void Same_boot_reconnect_presents_hint()
    {
        var store = new AttachClientViewHintStore();
        store.Remember("plc_a", "boot_1", "ws_a", "tab_a", "pane_a");

        var intent = store.Present(
            new EndpointActivationIntent { EndpointId = "plc_a" },
            "boot_1");

        Assert.Equal("ws_a", intent.WorkspaceId);
        Assert.Equal("tab_a", intent.TabId);
        Assert.Equal("pane_a", intent.PaneId);
        Assert.True(store.Contains("plc_a"));
    }

    [Fact]
    public void Changed_boot_discards_hint()
    {
        var store = new AttachClientViewHintStore();
        store.Remember("plc_a", "boot_1", "ws_a", "tab_a", "pane_a");

        var intent = store.Present(
            new EndpointActivationIntent { EndpointId = "plc_a" },
            "boot_2");

        Assert.Null(intent.WorkspaceId);
        Assert.Null(intent.TabId);
        Assert.False(store.Contains("plc_a"));
    }

    [Fact]
    public void Removal_discards_hint_disable_keeps_it()
    {
        var store = new AttachClientViewHintStore();
        store.Remember("plc_a", "boot_1", "ws_a", "tab_a", "pane_a");
        store.Remember("plc_b", "boot_1", "ws_b", "tab_b", "pane_b");

        store.Discard("plc_a");
        Assert.False(store.Contains("plc_a"));
        Assert.True(store.Contains("plc_b"));

        var disabled = store.Present(
            new EndpointActivationIntent { EndpointId = "plc_b" },
            "boot_1");
        Assert.Equal("tab_b", disabled.TabId);
    }

    [Fact]
    public void Explicit_intent_is_not_replaced()
    {
        var store = new AttachClientViewHintStore();
        store.Remember("plc_a", "boot_1", "ws_a", "tab_a", "pane_a");

        var intent = store.Present(
            new EndpointActivationIntent
            {
                EndpointId = "plc_a",
                TabId = "tab_chosen",
            },
            "boot_1");

        Assert.Equal("tab_chosen", intent.TabId);
        Assert.Null(intent.PaneId);
    }

    [Fact]
    public void Empty_state_copy_is_exact()
    {
        Assert.Equal(
            "No workspaces. Create a workspace to continue.",
            AttachClientViewCopy.EmptyTopology);
        Assert.Equal("No panes in this tab.", AttachClientViewCopy.EmptyTab);
    }
}
