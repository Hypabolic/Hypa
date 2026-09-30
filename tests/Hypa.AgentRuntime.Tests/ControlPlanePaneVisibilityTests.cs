using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlanePaneVisibilityTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public ControlPlanePaneVisibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-vis-t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Show_hidden_emits_placement_then_layout_and_keeps_runtime()
    {
        var (cp, state, store, sink) = await NewSubscribedAsync();
        try
        {
            var (hidden, tiled, conn, leaseId) = await SeedOccupiedAsync(cp);
            sink.Lines.Clear();

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowParams(hidden, leaseId, direction: "right", target: tiled),
                conn,
                CancellationToken.None);

            Assert.True(shown.GetProperty("ok").GetBoolean());
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.False(shown.GetProperty("hidden").GetBoolean());
            Assert.True(shown.GetProperty("cols").GetInt32() > 0);
            Assert.True(shown.GetProperty("rows").GetInt32() > 0);
            Assert.NotNull(cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);

            var types = EventTypes(sink);
            Assert.Contains(ProtocolEventTypes.PanePlacementChanged, types);
            Assert.Contains(ProtocolEventTypes.LayoutUpdated, types);
            Assert.True(
                types.IndexOf(ProtocolEventTypes.PanePlacementChanged)
                < types.IndexOf(ProtocolEventTypes.LayoutUpdated));

            var loaded = (await store.TryLoadAsync("pane-vis")).Value!;
            Assert.Equal(PanePlacement.Tiled, loaded.Panes[hidden].Placement);
            var tab = loaded.Tabs[shown.GetProperty("tab_id").GetString()!];
            Assert.Contains(tab.PaneIds, p => p.Value == hidden);
            Assert.DoesNotContain(tab.HiddenPaneIds, p => p.Value == hidden);
            Assert.Equal(state.GetPane(new PaneId(hidden))!.WorkspaceId.Value,
                loaded.Panes[hidden].WorkspaceId.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hide_keeps_pty_size_and_generation()
    {
        var (cp, state, store, _) = await NewSubscribedAsync();
        try
        {
            var (hidden, tiled, conn, leaseId) = await SeedOccupiedAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowParams(hidden, leaseId, direction: "right", target: tiled),
                conn,
                CancellationToken.None);
            var before = state.GetPane(new PaneId(hidden))!;
            var runtime = cp.PeekRuntime(hidden);

            var hiddenResult = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                HideParams(hidden, leaseId),
                conn,
                CancellationToken.None);

            Assert.True(hiddenResult.GetProperty("changed").GetBoolean());
            Assert.Equal("hidden", hiddenResult.GetProperty("placement").GetString());
            Assert.True(hiddenResult.GetProperty("hidden").GetBoolean());
            Assert.Equal(before.Cols, hiddenResult.GetProperty("cols").GetInt32());
            Assert.Equal(before.Rows, hiddenResult.GetProperty("rows").GetInt32());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            var pane = state.GetPane(new PaneId(hidden))!;
            Assert.Equal(before.OccupantGeneration, pane.OccupantGeneration);
            Assert.Equal(before.Cols, pane.Cols);
            Assert.Equal(before.Rows, pane.Rows);

            var loaded = (await store.TryLoadAsync("pane-vis")).Value!;
            Assert.Equal(PanePlacement.Hidden, loaded.Panes[hidden].Placement);
            var tab = loaded.Tabs[pane.TabId.Value];
            Assert.Contains(tab.HiddenPaneIds, p => p.Value == hidden);
            Assert.DoesNotContain(tab.PaneIds, p => p.Value == hidden);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_lease_and_overlay_mode_leave_graph_unchanged()
    {
        var (cp, state, _, _) = await NewSubscribedAsync();
        try
        {
            var (hidden, tiled, conn, leaseId) = await SeedOccupiedAsync(cp);
            var before = state.Snapshot();

            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowParams(hidden, "lease_missing", direction: "right", target: tiled),
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, missing.Code);
            AssertUnchangedHidden(state, before, hidden);

            var overlay = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowParams(hidden, leaseId, direction: "right", target: tiled, mode: "overlay"),
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, overlay.Code);
            Assert.Contains("overlay", overlay.Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchangedHidden(state, before, hidden);

            var unknown = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowParams(hidden, leaseId, direction: "right", target: tiled, mode: "float"),
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, unknown.Code);
            AssertUnchangedHidden(state, before, hidden);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_target_rolls_back_and_stale_show_does_not_emit()
    {
        var (cp, state, _, sink) = await NewSubscribedAsync();
        try
        {
            var (hidden, tiled, conn, leaseId) = await SeedOccupiedAsync(cp);
            var before = state.Snapshot();

            var invalid = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowParams(hidden, leaseId, direction: "right", target: "missing"),
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, invalid.Code);
            AssertUnchangedHidden(state, before, hidden);

            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowParams(hidden, leaseId, direction: "right", target: tiled),
                conn,
                CancellationToken.None);
            sink.Lines.Clear();

            var again = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowParams(hidden, leaseId, direction: "right", target: tiled),
                conn,
                CancellationToken.None);
            Assert.False(again.GetProperty("changed").GetBoolean());
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
            Assert.DoesNotContain(ProtocolEventTypes.LayoutUpdated, EventTypes(sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cross_workspace_show_persists_new_owner()
    {
        var (cp, _, store, _) = await NewSubscribedAsync();
        try
        {
            var (hidden, _, conn, leaseId) = await SeedOccupiedAsync(cp);
            var other = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = false,
                    ["label"] = "other",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var destTab = other.GetProperty("focused_tab_id").GetString()!;
            var destWs = other.GetProperty("workspace_id").GetString()!;

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowParams(hidden, leaseId, tabId: destTab),
                conn,
                CancellationToken.None);

            Assert.Equal(destTab, shown.GetProperty("tab_id").GetString());
            Assert.Equal(destWs, shown.GetProperty("workspace_id").GetString());
            var loaded = (await store.TryLoadAsync("pane-vis")).Value!;
            Assert.Equal(destTab, loaded.Panes[hidden].TabId.Value);
            Assert.Equal(destWs, loaded.Panes[hidden].WorkspaceId.Value);
            Assert.Contains(loaded.Tabs[destTab].PaneIds, p => p.Value == hidden);
            Assert.DoesNotContain(
                loaded.Tabs.Values.SelectMany(t => t.HiddenPaneIds),
                p => p.Value == hidden);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(string Hidden, string Tiled, FakeConnection Conn, string LeaseId)>
        SeedOccupiedAsync(ControlPlaneService cp)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/echo",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var tiled = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        var workspaceId = ws.GetProperty("workspace_id").GetString()!;
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var conn = new FakeConnection("conn_vis");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
            conn,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());
        return (hidden, tiled, conn, claim.GetProperty("lease_id").GetString()!);
    }

    private async Task<(ControlPlaneService Cp, AppState State, SqliteRuntimeSessionStore Store, CapturingSink Sink)>
        NewSubscribedAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-vis"));
        state.UpdateSession(s => s with { Name = "pane-vis", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        var sink = new CapturingSink();
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = new JsonArray(
                    ProtocolEventTypes.PanePlacementChanged,
                    ProtocolEventTypes.LayoutUpdated),
                ["live"] = true,
            }.ToJsonString()).RootElement,
            sink,
            CancellationToken.None);
        await cp.CompleteEventsSubscribeAsync(
            sub.GetProperty("subscription_id").GetString()!,
            [],
            sink,
            CancellationToken.None);
        return (cp, state, store, sink);
    }

    [Fact]
    public async Task Subscribe_ReturnsAttachClientIdDistinctFromSubscription()
    {
        var (cp, _, _, sink) = await NewSubscribedAsync();
        try
        {
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                JsonDocument.Parse(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["live"] = true,
                }.ToJsonString()).RootElement,
                sink,
                CancellationToken.None);
            var subscriptionId = sub.GetProperty("subscription_id").GetString();
            var attachClientId = sub.GetProperty("attach_client_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(subscriptionId));
            Assert.Equal("c_vis", attachClientId);
            Assert.NotEqual(subscriptionId, attachClientId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static JsonElement ShowParams(
        string paneId,
        string leaseId,
        string? direction = null,
        string? target = null,
        string? mode = null,
        string? tabId = null)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
        };
        if (direction is not null)
            obj["direction"] = direction;
        if (target is not null)
            obj["target_pane_id"] = target;
        if (mode is not null)
            obj["mode"] = mode;
        if (tabId is not null)
            obj["tab_id"] = tabId;
        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement HideParams(string paneId, string leaseId) =>
        JsonDocument.Parse(new JsonObject
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
        }.ToJsonString()).RootElement.Clone();

    private static List<string> EventTypes(CapturingSink sink) =>
        sink.Parsed
            .Where(ev => ev.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            .Select(ev => ev.GetProperty("params").GetProperty("type").GetString()!)
            .ToList();

    private static void AssertUnchangedHidden(AppState state, SessionState before, string hidden)
    {
        var after = state.Snapshot();
        Assert.Equal(PanePlacement.Hidden, after.Panes[hidden].Placement);
        Assert.Equal(before.Panes[hidden].TabId.Value, after.Panes[hidden].TabId.Value);
        foreach (var (id, tab) in before.Tabs)
        {
            Assert.Equal(tab.PaneIds.Select(p => p.Value), after.Tabs[id].PaneIds.Select(p => p.Value));
            Assert.Equal(tab.HiddenPaneIds.Select(p => p.Value), after.Tabs[id].HiddenPaneIds.Select(p => p.Value));
        }
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_vis";
        public List<string> Lines { get; } = [];

        public IReadOnlyList<JsonElement> Parsed
        {
            get
            {
                lock (_gate)
                    return Lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }
}
