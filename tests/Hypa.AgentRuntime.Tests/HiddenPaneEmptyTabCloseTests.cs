using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Sidebar;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class HiddenPaneEmptyTabCloseTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public HiddenPaneEmptyTabCloseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-empty-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task If_empty_close_keeps_hidden_pane_added_before_cleanup()
    {
        var (cp, state, _, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var hiddenRuntime = cp.PeekRuntime(hidden);
            Assert.NotNull(hiddenRuntime);
            var inner = new ControlPlanePort(cp, conn);
            string? planted = null;
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailShowPort(new PlantPaneBeforeEmptyClosePort(
                    inner,
                    workspaceId,
                    PanePlacementWire.Hidden,
                    id => planted = id)),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.CleanedTabId);
            Assert.False(string.IsNullOrWhiteSpace(planted));
            var plantedPane = state.GetPane(new PaneId(planted!));
            Assert.NotNull(plantedPane);
            Assert.Equal(PanePlacement.Hidden, plantedPane.Placement);
            Assert.Same(hiddenRuntime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.NotNull(cp.PeekRuntime(planted!));
            Assert.True(cp.PeekRuntime(planted!)!.IsAlive);
            Assert.Contains(planted!, state.GetTab(plantedPane.TabId)!.HiddenPaneIds.Select(id => id.Value));
            Assert.True(state.Snapshot().Tabs.ContainsKey(plantedPane.TabId.Value));
            Assert.Contains(inner.Calls, c =>
                c.Method == ProtocolMethods.TabClose
                && c.Params?["if_empty"]?.GetValue<bool>() == true);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task If_empty_close_keeps_tiled_pane_planted_before_close()
    {
        var (cp, state, _, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var inner = new ControlPlanePort(cp, conn);
            string? planted = null;
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailShowPort(new PlantPaneBeforeEmptyClosePort(
                    inner,
                    workspaceId,
                    PanePlacementWire.Tiled,
                    id => planted = id)),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.CleanedTabId);
            Assert.False(string.IsNullOrWhiteSpace(planted));
            var plantedPane = state.GetPane(new PaneId(planted!));
            Assert.NotNull(plantedPane);
            Assert.Equal(PanePlacement.Tiled, plantedPane.Placement);
            Assert.NotNull(cp.PeekRuntime(planted!));
            Assert.True(cp.PeekRuntime(planted!)!.IsAlive);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.True(state.Snapshot().Tabs.ContainsKey(plantedPane.TabId.Value));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task If_empty_close_removes_genuinely_empty_tab()
    {
        var (cp, state, _, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var tabsBefore = state.Snapshot().Tabs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var inner = new ControlPlanePort(cp, conn);
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailShowPort(inner),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.CleanedTabId));
            Assert.Equal(
                tabsBefore,
                state.Snapshot().Tabs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.False(state.Snapshot().Tabs.ContainsKey(result.CleanedTabId!));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.True(cp.PeekRuntime(tiled)!.IsAlive);
            Assert.Contains(inner.Calls, c =>
                c.Method == ProtocolMethods.TabClose
                && c.Params?["if_empty"]?.GetValue<bool>() == true);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ordinary_tab_close_still_disposes_occupants()
    {
        var (cp, state, _, conn) = await StartAsync();
        try
        {
            var (_, tiled, workspaceId) = await SeedPanesAsync(cp, conn);
            var extra = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            var extraTab = extra.GetProperty("tab_id").GetString()!;
            var extraTiled = extra.GetProperty("pane").GetProperty("pane_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.TabFocus,
                JsonDocument.Parse(new JsonObject { ["tab_id"] = extraTab }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            var extraHidden = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            var hiddenId = extraHidden.GetProperty("pane_id").GetString()!;
            Assert.True(cp.PeekRuntime(extraTiled)!.IsAlive);
            Assert.True(cp.PeekRuntime(hiddenId)!.IsAlive);

            var closed = await cp.DispatchAsync(
                ProtocolMethods.TabClose,
                JsonDocument.Parse(new JsonObject { ["tab_id"] = extraTab }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            Assert.True(closed.GetProperty("ok").GetBoolean());
            Assert.False(closed.TryGetProperty("closed", out _));
            Assert.False(state.Snapshot().Tabs.ContainsKey(extraTab));
            Assert.Null(state.GetPane(new PaneId(extraTiled)));
            Assert.Null(state.GetPane(new PaneId(hiddenId)));
            Assert.True(cp.PeekRuntime(extraTiled) is null || !cp.PeekRuntime(extraTiled)!.IsAlive);
            Assert.True(cp.PeekRuntime(hiddenId) is null || !cp.PeekRuntime(hiddenId)!.IsAlive);
            Assert.NotNull(state.GetPane(new PaneId(tiled)));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_focus_after_show_still_keeps_child()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var hiddenRuntime = cp.PeekRuntime(hidden);
            var inner = new ControlPlanePort(cp, conn);
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailFocusPort(inner),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.CleanedTabId);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Same(hiddenRuntime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.DoesNotContain(inner.Calls, c => c.Method == ProtocolMethods.TabClose);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(hidden, LeaseScopes.Resize, result.TargetLeaseId, conn.ConnectionId).Status);
            Assert.Equal("conn_empty", conn.ConnectionId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Cp, AppState State, InMemoryLeaseRegistry Leases, FakeConnection Conn)>
        StartAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("empty-close"));
        state.UpdateSession(s => s with { Name = "empty-close", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var leases = new InMemoryLeaseRegistry();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            leases: leases);
        var conn = new FakeConnection("conn_empty");
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
            }.ToJsonString()).RootElement,
            conn,
            CancellationToken.None);
        var subscriptionId = sub.GetProperty("subscription_id").GetString();
        var attachClientId = sub.GetProperty("attach_client_id").GetString();
        Assert.Equal("conn_empty", attachClientId);
        Assert.NotEqual(subscriptionId, attachClientId);
        await cp.CompleteEventsSubscribeAsync(subscriptionId!, [], conn, CancellationToken.None);
        return (cp, state, leases, conn);
    }

    private async Task<(string Hidden, string Tiled, string WorkspaceId)>
        SeedPanesAsync(ControlPlaneService cp, IClientConnection conn)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = _dir,
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
        await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = tiled,
                ["scope"] = LeaseScopes.Resize,
            }.ToJsonString()).RootElement,
            conn,
            CancellationToken.None);
        return (hidden, tiled, workspaceId);
    }

    private static HiddenPaneActionRequest Req(string paneId, string tabId, string workspaceId) =>
        new()
        {
            Action = HiddenPaneAction.ShowNewTab,
            PaneId = paneId,
            CurrentTabId = tabId,
            WorkspaceId = workspaceId,
            PriorTabId = tabId,
            PriorWorkspaceId = workspaceId,
            AttachClientId = "conn_empty",
        };

    private static IReadOnlyList<HiddenPaneRecord> Catalog(
        AppState state,
        string hidden,
        string tiled,
        string workspaceId)
    {
        var hiddenTab = state.GetPane(new PaneId(hidden))!.TabId.Value;
        var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
        return
        [
            new HiddenPaneRecord
            {
                PaneId = hidden,
                TabId = hiddenTab,
                WorkspaceId = workspaceId,
                Label = hidden,
                Hidden = true,
                Placement = PanePlacementWire.Hidden,
            },
            new HiddenPaneRecord
            {
                PaneId = tiled,
                TabId = tiledTab,
                WorkspaceId = workspaceId,
                Label = tiled,
                Hidden = false,
                Placement = PanePlacementWire.Tiled,
            },
        ];
    }

    private sealed class ControlPlanePort(ControlPlaneService cp, IClientConnection connection) : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            using var doc = JsonDocument.Parse(parameters?.ToJsonString() ?? "{}");
            return await cp.DispatchAsync(method, doc.RootElement.Clone(), connection, ct)
                .ConfigureAwait(false);
        }
    }

    private sealed class FailShowPort(IAttachCommandPort inner) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.PaneShow, StringComparison.Ordinal))
                throw new InvalidOperationException("show failed");
            return inner.CallAsync(method, parameters, ct);
        }
    }

    private sealed class FailFocusPort(IAttachCommandPort inner) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.TabFocus, StringComparison.Ordinal)
                || string.Equals(method, ProtocolMethods.WorkspaceFocus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("focus failed");
            }

            return inner.CallAsync(method, parameters, ct);
        }
    }

    private sealed class PlantPaneBeforeEmptyClosePort(
        IAttachCommandPort inner,
        string workspaceId,
        string placement,
        Action<string> onPlanted) : IAttachCommandPort
    {
        private bool _planted;

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (!_planted
                && string.Equals(method, ProtocolMethods.TabClose, StringComparison.Ordinal)
                && parameters?["if_empty"]?.GetValue<bool>() == true)
            {
                _planted = true;
                var tabId = parameters["tab_id"]!.GetValue<string>();
                await inner.CallAsync(
                        ProtocolMethods.TabFocus,
                        new JsonObject { ["tab_id"] = tabId },
                        ct)
                    .ConfigureAwait(false);
                var created = await inner.CallAsync(
                        ProtocolMethods.PaneCreate,
                        new JsonObject
                        {
                            ["workspace_id"] = workspaceId,
                            ["command"] = "/bin/echo",
                            ["placement"] = placement,
                        },
                        ct)
                    .ConfigureAwait(false);
                onPlanted(created.GetProperty("pane_id").GetString()!);
            }

            return await inner.CallAsync(method, parameters, ct).ConfigureAwait(false);
        }
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
