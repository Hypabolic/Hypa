using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AttachChromeRefreshEventTests
{
    [Theory]
    [InlineData(ProtocolEventTypes.LayoutUpdated, true)]
    [InlineData(ProtocolEventTypes.PanePlacementChanged, true)]
    [InlineData(ProtocolEventTypes.WorkspaceLifecycle, true)]
    [InlineData(ProtocolEventTypes.TabLifecycle, true)]
    [InlineData(ProtocolEventTypes.TerminalRender, false)]
    [InlineData(ProtocolEventTypes.PaneLifecycle, false)]
    [InlineData(ProtocolEventTypes.PaneAgentStatusChanged, false)]
    public void Chrome_refresh_follows_layout_and_placement_events(string type, bool expected) =>
        Assert.Equal(expected, AttachSession.IsChromeRefreshEvent(type));

    [Theory]
    [InlineData(ProtocolEventTypes.PaneAgentStatusChanged, true)]
    [InlineData(ProtocolEventTypes.NotificationShown, true)]
    [InlineData(ProtocolEventTypes.ConfigReloaded, true)]
    [InlineData(ProtocolEventTypes.ClientWindowTitleChanged, true)]
    [InlineData(ProtocolEventTypes.PaneInputRejected, true)]
    [InlineData(ProtocolEventTypes.LayoutUpdated, false)]
    [InlineData(ProtocolEventTypes.TerminalRender, false)]
    public void Live_notify_consumes_agent_status_without_layout_refresh(string type, bool expected) =>
        Assert.Equal(expected, AttachSession.IsLiveNotifyEvent(type));

    [Fact]
    public void Agent_status_event_replaces_stale_sidebar_kind()
    {
        var live = LiveWithGrokPane();
        Assert.Equal("grok", AttachSession.AgentNameOf(live));
        using var doc = StatusEvent("idle", "claude");

        Assert.True(AttachSession.ApplyNotifyEvent(live, doc.RootElement, tty: null));
        Assert.Equal("claude", AttachSession.AgentNameOf(live));
        Assert.Equal("idle", AttachSession.AgentStateOf(live));
    }

    [Fact]
    public void Agent_status_event_clears_sidebar_kind_when_agent_is_null()
    {
        var live = LiveWithGrokPane();
        using var doc = StatusEvent("unknown", agent: null);

        Assert.True(AttachSession.ApplyNotifyEvent(live, doc.RootElement, tty: null));
        Assert.Null(AttachSession.AgentNameOf(live));
        Assert.Equal("unknown", AttachSession.AgentStateOf(live));
    }

    [Fact]
    public async Task Render_path_consumes_agent_status_without_layout_export()
    {
        var live = LiveWithGrokPane();
        var port = new RecordingPort();
        using var doc = StatusEvent("idle", "claude");

        await AttachSession.HandleRenderEventAsync(
            doc.RootElement,
            port,
            new SnapshotAssembler(),
            tty: null,
            live,
            CancellationToken.None);

        Assert.Equal("claude", AttachSession.AgentNameOf(live));
        Assert.DoesNotContain(port.Methods, m =>
            string.Equals(m, ProtocolMethods.LayoutExport, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quiet_pane_follow_up_loads_the_agent_into_the_sidebar()
    {
        var clock = new ManualClock();
        var live = LiveWithShellPane(clock);
        var port = new SnapshotPort(AgentSnapshot("shell"));
        await AttachSession.TryRefreshSidebarLiveAsync(
            port, live, tty: null, controlGate: null, CancellationToken.None, armFollowUp: true);
        Assert.Equal("shell", AttachSession.AgentNameOf(live));
        Assert.NotNull(live.SidebarRefreshAfter);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await AttachSession.TryFlushDeferredSidebarRefreshAsync(
            live, port, tty: null, controlGate: null, CancellationToken.None);
        Assert.Equal("shell", AttachSession.AgentNameOf(live));

        port.Snapshot = AgentSnapshot("grok");
        clock.Advance(AttachSession.SidebarLiveFollowUp);
        await AttachSession.TryFlushDeferredSidebarRefreshAsync(
            live, port, tty: null, controlGate: null, CancellationToken.None);
        Assert.Equal("grok", AttachSession.AgentNameOf(live));
        Assert.Null(live.SidebarRefreshAfter);
    }

    [Fact]
    public async Task In_flight_snapshot_does_not_cover_a_newer_agent_status()
    {
        var live = LiveWithShellPane(TimeProvider.System);
        var port = new SnapshotPort(AgentSnapshot("shell"), beforeReturn: () =>
        {
            using var doc = StatusEvent("idle", "grok");
            Assert.True(AttachSession.ApplyNotifyEvent(live, doc.RootElement, tty: null));
        });

        await AttachSession.TryRefreshSidebarLiveAsync(
            port, live, tty: null, controlGate: null, CancellationToken.None, ignoreThrottle: true);

        Assert.Equal("grok", AttachSession.AgentNameOf(live));
    }

    private static JsonDocument StatusEvent(string status, string? agent)
    {
        var payload = new JsonObject
        {
            ["pane_id"] = "p1",
            ["tab_id"] = "t1",
            ["occupant_generation"] = 1,
            ["agent_status"] = status,
            ["seen"] = true,
        };
        if (agent is null)
            payload["agent"] = JsonNode.Parse("null");
        else
            payload["agent"] = JsonValue.Create(agent);
        return JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneAgentStatusChanged,
                ["payload"] = payload,
            },
        }.ToJsonString());
    }

    private static JsonElement AgentSnapshot(string agent)
    {
        using var doc = JsonDocument.Parse(
            $$"""{"panes":[{"pane_id":"p1","tab_id":"t1","workspace_id":"w1","agent":"{{agent}}","state":"idle"}]}""");
        return doc.RootElement.Clone();
    }

    private static AttachLiveState LiveWithShellPane(TimeProvider time)
    {
        var live = LiveWithGrokPane();
        live.Time = time;
        live.SidebarInput = live.SidebarInput! with
        {
            Panes =
            [
                new SidebarPaneItem
                {
                    Id = "p1",
                    TabId = "t1",
                    WorkspaceId = "w1",
                    Agent = "shell",
                    State = "idle",
                },
            ],
        };
        return live;
    }

    private static AttachLiveState LiveWithGrokPane()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new NullPort(), "w1", "t1", "p1", "lease"),
            ChromeEnabled = true,
            PaneId = "p1",
            SidebarOpen = true,
            SidebarInput = new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                FocusedPaneId = "p1",
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = "p1",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "grok",
                        State = "idle",
                    },
                ],
            },
        };
    }

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<string> Methods { get; } = [];

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Methods.Add(method);
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class SnapshotPort : IAttachCommandPort
    {
        public SnapshotPort(JsonElement snapshot, Action? beforeReturn = null)
        {
            Snapshot = snapshot;
            _beforeReturn = beforeReturn;
        }

        public JsonElement Snapshot { get; set; }

        private readonly Action? _beforeReturn;

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            _beforeReturn?.Invoke();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _utc += by;

        public override DateTimeOffset GetUtcNow() => _utc;
    }

    private sealed class NullPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }
}
