using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachVisibleSetTests
{
    [Fact]
    public async Task Publish_sends_authoritative_pane_ids()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_vs");
        await AttachSession.PublishVisibleSetAsync(port, live, ["p_a", "p_b"], CancellationToken.None);

        Assert.Single(port.Calls);
        Assert.Equal(ProtocolMethods.TerminalVisibleSet, port.Calls[0].Method);
        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_a", "p_b"], ids);
        Assert.Equal("sub_vs", port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
        Assert.Equal(["p_a", "p_b"], live.LastPublishedVisiblePaneIds);
        Assert.NotNull(live.LastVisibleSetPublishUtc);
    }

    [Fact]
    public async Task Repeated_unchanged_heartbeat_skips_when_fresh()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_vs");
        live.LastVisibleSetPublishUtc = DateTimeOffset.UtcNow;
        live.LastPublishedVisiblePaneIds = ["p_a"];

        await AttachSession.RenewVisibleSetIfDueAsync(port, live, CancellationToken.None);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task Idle_renewal_republishes_last_ids()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_vs");
        live.LastVisibleSetPublishUtc = DateTimeOffset.UtcNow - AttachSession.VisibleSetRenewPeriod - TimeSpan.FromSeconds(1);
        live.LastPublishedVisiblePaneIds = ["p_keep"];

        await AttachSession.RenewVisibleSetIfDueAsync(port, live, CancellationToken.None);
        Assert.Single(port.Calls);
        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_keep"], ids);
    }

    [Fact]
    public async Task Publish_uses_control_subscription_not_attach_client_id()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.AttachClientId = "conn_4";

        await AttachSession.PublishVisibleSetAsync(port, live, ["p_a"], CancellationToken.None);

        Assert.Equal("sub_ctrl", port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
        Assert.NotEqual("conn_4", port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
        Assert.Null(port.Calls[0].Params!["attach_client_id"]);
    }

    [Fact]
    public async Task Publish_includes_hidden_modal_overlay_id()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.OverlayPaneId = "p_modal";

        await AttachSession.PublishVisibleSetAsync(port, live, ["p_tiled"], CancellationToken.None);

        Assert.Equal("p_modal", port.Calls[0].Params!["overlay_pane_id"]!.GetValue<string>());
        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_tiled"], ids);
    }

    [Fact]
    public async Task Hidden_split_publishes_shown_pane_on_control_subscription()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.AttachClientId = "conn_4";
        live.PaneId = "p_vis";
        live.LastPublishedVisiblePaneIds = ["p_vis"];

        await AttachSession.PublishTiledVisibilityAfterHiddenActionAsync(
            port, live, HiddenPaneAction.SplitRight, "p_hidden", CancellationToken.None);

        Assert.Equal(ProtocolMethods.TerminalVisibleSet, port.Calls[0].Method);
        Assert.Equal("sub_ctrl", port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
        Assert.NotEqual(live.AttachClientId, port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_vis", "p_hidden"], ids);
    }

    [Fact]
    public async Task Hidden_new_tab_publishes_shown_pane()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.PaneId = "p_vis";
        live.LastPublishedVisiblePaneIds = ["p_vis"];

        await AttachSession.PublishTiledVisibilityAfterHiddenActionAsync(
            port, live, HiddenPaneAction.ShowNewTab, "p_hidden", CancellationToken.None);

        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("p_hidden", ids);
        Assert.Contains("p_vis", ids);
    }

    [Fact]
    public async Task Hidden_hide_removes_pane_from_tiled_set()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.LastPublishedVisiblePaneIds = ["p_vis", "p_hidden"];

        await AttachSession.PublishTiledVisibilityAfterHiddenActionAsync(
            port, live, HiddenPaneAction.Hide, "p_hidden", CancellationToken.None);

        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_vis"], ids);
    }

    [Fact]
    public async Task Hidden_modal_keeps_tiled_set_and_publishes_overlay()
    {
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_ctrl");
        live.ControlSub = "sub_ctrl";
        live.AttachClientId = "conn_4";
        live.OverlayPaneId = "p_hidden";
        live.LastPublishedVisiblePaneIds = ["p_vis"];

        await AttachSession.PublishTiledVisibilityAfterHiddenActionAsync(
            port, live, HiddenPaneAction.ShowModal, "p_hidden", CancellationToken.None);

        var ids = port.Calls[0].Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["p_vis"], ids);
        Assert.Equal("p_hidden", port.Calls[0].Params!["overlay_pane_id"]!.GetValue<string>());
        Assert.Equal("sub_ctrl", port.Calls[0].Params!["subscription_id"]!.GetValue<string>());
    }

    [Fact]
    public void Reconnect_updates_both_identities_from_subscribe()
    {
        using var doc = JsonDocument.Parse(
            """{"subscription_id":"sub_next","attach_client_id":"conn_next"}""");
        var parsed = AttachSession.ReadSubscribeResult(doc.RootElement);
        var port = new RecordingPort();
        var live = Live(port, renderSub: "sub_old");
        live.ControlSub = "sub_old";
        live.AttachClientId = "conn_old";
        live.OverlayPaneId = "p_stale";

        live.ControlSub = parsed.SubscriptionId;
        live.AttachClientId = parsed.AttachClientId;
        live.OverlayPaneId = null;
        live.RenderSub = parsed.SubscriptionId;

        Assert.Equal("sub_next", live.ControlSub);
        Assert.Equal("conn_next", live.AttachClientId);
        Assert.NotEqual(live.ControlSub, live.AttachClientId);
        Assert.Equal(live.ControlSub, live.RenderSub);
        Assert.Null(live.OverlayPaneId);
        Assert.Equal("sub_next", AttachSession.OwnedControlSubscriptionId(live));
    }

    [Fact]
    public async Task Failed_publish_marks_client_failed()
    {
        var port = new RecordingPort();
        port.Handler = (method, _) =>
        {
            if (string.Equals(method, ProtocolMethods.TerminalVisibleSet, StringComparison.Ordinal)
                && port.Calls.Count == 1)
            {
                throw new InvalidOperationException("publish failed");
            }

            return Empty();
        };
        var live = Live(port, renderSub: "sub_vs");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AttachSession.PublishVisibleSetAsync(port, live, ["p_a"], CancellationToken.None));

        Assert.Equal(2, port.Calls.Count);
        Assert.True(port.Calls[1].Params!["failed"]!.GetValue<bool>());
    }

    private static AttachLiveState Live(IAttachCommandPort port, string renderSub)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port),
            RenderSub = renderSub,
        };
    }

    private static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public Func<string, JsonObject?, JsonElement>? Handler { get; set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            if (Handler is not null)
                return Task.FromResult(Handler(method, parameters));
            return Task.FromResult(Empty());
        }
    }
}
