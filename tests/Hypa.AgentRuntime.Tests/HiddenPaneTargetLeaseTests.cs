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

public sealed class HiddenPaneTargetLeaseTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public HiddenPaneTargetLeaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-tgt-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public void Registry_RejectsCurrentTiledLeaseForHiddenTarget()
    {
        var leases = new InMemoryLeaseRegistry();
        var current = leases.Claim("p1", LeaseScopes.Resize, "conn_a", takeover: false, reason: null, ttlMs: 30_000);
        Assert.Equal(LeaseOutcomes.Granted, current.Outcome);
        var auth = leases.TryAuthorize("p2", LeaseScopes.Resize, current.Lease!.LeaseId, "conn_a");
        Assert.Equal(LeaseAuthorizeStatus.Missing, auth.Status);
        Assert.Equal(
            LeaseAuthorizeStatus.Authorized,
            leases.TryAuthorize("p1", LeaseScopes.Resize, current.Lease.LeaseId, "conn_a").Status);
    }

    [Fact]
    public async Task Current_tiled_lease_cannot_show_hidden_target()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, tiledLease, _) = await SeedPanesAsync(cp, conn);
            await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowParams(hidden, tiledLease!, direction: "right", target: tiled),
                    conn,
                    CancellationToken.None));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task New_tab_claims_target_lease_and_focuses()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, tiledLease, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var port = new ControlPlanePort(cp, conn);
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.ShowNewTab, hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                port,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.FocusTabId));
            Assert.Equal(workspaceId, result.FocusWorkspaceId);
            Assert.NotEqual(tiledLease, result.TargetLeaseId);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(hidden, state.Snapshot().Tabs[result.FocusTabId!].PaneIds.Single().Value);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(hidden, LeaseScopes.Resize, result.TargetLeaseId, conn.ConnectionId).Status);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);
            Assert.Contains(port.Calls, c =>
                c.Method == ProtocolMethods.RuntimeLeaseClaim
                && c.Params?["pane_id"]?.GetValue<string>() == hidden
                && c.Params?["scope"]?.GetValue<string>() == LeaseScopes.Resize);
            Assert.DoesNotContain(port.Calls, c =>
                c.Method == ProtocolMethods.PaneShow
                && c.Params?["lease_id"]?.GetValue<string>() == tiledLease);
            Assert.DoesNotContain(port.Calls, c => c.Method == ProtocolMethods.PaneCreate);
            Assert.Contains(port.Calls, c =>
                c.Method == ProtocolMethods.TabCreate
                && c.Params?["create_pane"]?.GetValue<bool>() == false);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Split_and_hide_use_target_lease()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, tiledLease, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var port = new ControlPlanePort(cp, conn);
            var shown = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.SplitRight, hidden, tiledTab, workspaceId, tiled),
                Catalog(state, hidden, tiled, workspaceId),
                port,
                CancellationToken.None);
            Assert.True(shown.Succeeded);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.NotEqual(tiledLease, shown.TargetLeaseId);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);

            var hiddenAfter = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.Hide, hidden, tiledTab, workspaceId),
                [
                    Record(hidden, tiledTab, workspaceId, hidden: false),
                    Record(tiled, tiledTab, workspaceId, hidden: false),
                ],
                port,
                CancellationToken.None);
            Assert.True(hiddenAfter.Succeeded);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Null(leases.GetActive(hidden, LeaseScopes.Resize));
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task No_current_tiled_lease_can_reveal_hidden_target()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, _, workspaceId) = await SeedPanesAsync(cp, conn, claimTiled: false);
            Assert.Null(leases.GetActive(tiled, LeaseScopes.Resize));
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.ShowNewTab, hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new ControlPlanePort(cp, conn),
                CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.False(string.IsNullOrWhiteSpace(result.TargetLeaseId));
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(hidden, LeaseScopes.Resize, result.TargetLeaseId, conn.ConnectionId).Status);
            Assert.Null(leases.GetActive(tiled, LeaseScopes.Resize));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_show_releases_new_lease_and_restores_focus()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, tiledLease, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var tabsBefore = state.Snapshot().Tabs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var inner = new ControlPlanePort(cp, conn);
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.ShowNewTab, hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailShowPort(inner),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(tiledTab, result.RestoredTabId);
            Assert.Equal(workspaceId, result.RestoredWorkspaceId);
            Assert.False(string.IsNullOrWhiteSpace(result.CleanedTabId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(
                tabsBefore,
                state.Snapshot().Tabs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Null(leases.GetActive(hidden, LeaseScopes.Resize));
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);
            Assert.DoesNotContain(inner.Calls, c => c.Method == ProtocolMethods.TabGet);
            Assert.Contains(inner.Calls, c =>
                c.Method == ProtocolMethods.TabClose
                && c.Params?["tab_id"]?.GetValue<string>() == result.CleanedTabId
                && c.Params?["if_empty"]?.GetValue<bool>() == true);
            Assert.NotNull(cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.NotNull(cp.PeekRuntime(tiled));
            Assert.True(cp.PeekRuntime(tiled)!.IsAlive);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_focus_after_show_keeps_child_runtime()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, tiledLease, workspaceId) = await SeedPanesAsync(cp, conn);
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var hiddenRuntime = cp.PeekRuntime(hidden);
            Assert.NotNull(hiddenRuntime);
            Assert.True(hiddenRuntime.IsAlive);
            var inner = new ControlPlanePort(cp, conn);
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.ShowNewTab, hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailFocusPort(inner),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.FocusTabId));
            Assert.Null(result.CleanedTabId);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(hidden, state.Snapshot().Tabs[result.FocusTabId!].PaneIds.Single().Value);
            Assert.Same(hiddenRuntime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.Contains(hidden, state.Snapshot().Panes.Keys.Select(id => id.ToString()));
            Assert.DoesNotContain(inner.Calls, c => c.Method == ProtocolMethods.TabClose);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(hidden, LeaseScopes.Resize, result.TargetLeaseId, conn.ConnectionId).Status);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(tiled, LeaseScopes.Resize, tiledLease, conn.ConnectionId).Status);
            Assert.Contains(inner.Calls, c =>
                c.Method == ProtocolMethods.RuntimeLeaseClaim
                && c.Params?["pane_id"]?.GetValue<string>() == hidden
                && c.Params?["scope"]?.GetValue<string>() == LeaseScopes.Resize);
            Assert.Equal("conn_tgt", conn.ConnectionId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Already_held_target_lease_is_not_released_on_failure()
    {
        var (cp, state, leases, conn) = await StartAsync();
        try
        {
            var (hidden, tiled, _, workspaceId) = await SeedPanesAsync(cp, conn, claimTiled: false);
            var prior = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["scope"] = LeaseScopes.Resize,
                }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            var priorId = prior.GetProperty("lease_id").GetString()!;
            var tiledTab = state.GetPane(new PaneId(tiled))!.TabId.Value;
            var result = await HiddenPaneActionExecutor.RunAsync(
                Req(HiddenPaneAction.ShowNewTab, hidden, tiledTab, workspaceId),
                Catalog(state, hidden, tiled, workspaceId),
                new FailShowPort(new ControlPlanePort(cp, conn)),
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(
                LeaseAuthorizeStatus.Authorized,
                leases.TryAuthorize(hidden, LeaseScopes.Resize, priorId, conn.ConnectionId).Status);
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
        var state = new AppState(SessionId.New("tgt-lease"));
        state.UpdateSession(s => s with { Name = "tgt-lease", LifecycleState = SessionLifecycle.Ready });
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
        var conn = new FakeConnection("conn_tgt");
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
        Assert.Equal("conn_tgt", attachClientId);
        Assert.NotEqual(subscriptionId, attachClientId);
        await cp.CompleteEventsSubscribeAsync(subscriptionId!, [], conn, CancellationToken.None);
        return (cp, state, leases, conn);
    }

    private async Task<(string Hidden, string Tiled, string? TiledLease, string WorkspaceId)>
        SeedPanesAsync(ControlPlaneService cp, IClientConnection conn, bool claimTiled = true)
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
        string? tiledLease = null;
        if (claimTiled)
        {
            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["scope"] = LeaseScopes.Resize,
                }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());
            tiledLease = claim.GetProperty("lease_id").GetString()!;
        }

        return (hidden, tiled, tiledLease, workspaceId);
    }

    private static HiddenPaneActionRequest Req(
        HiddenPaneAction action,
        string paneId,
        string tabId,
        string workspaceId,
        string? visibleTarget = null) =>
        new()
        {
            Action = action,
            PaneId = paneId,
            CurrentTabId = tabId,
            WorkspaceId = workspaceId,
            PriorTabId = tabId,
            PriorWorkspaceId = workspaceId,
            VisibleTargetPaneId = visibleTarget,
            AttachClientId = "conn_tgt",
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
            Record(hidden, hiddenTab, workspaceId, hidden: true),
            Record(tiled, tiledTab, workspaceId, hidden: false),
        ];
    }

    private static HiddenPaneRecord Record(string paneId, string tabId, string workspaceId, bool hidden) =>
        new()
        {
            PaneId = paneId,
            TabId = tabId,
            WorkspaceId = workspaceId,
            Label = paneId,
            Hidden = hidden,
            Placement = hidden ? PanePlacementWire.Hidden : PanePlacementWire.Tiled,
        };

    private static JsonElement ShowParams(
        string paneId,
        string leaseId,
        string? direction = null,
        string? target = null)
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
        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
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

    private sealed class FailShowPort(ControlPlanePort inner) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.PaneShow, StringComparison.Ordinal))
                throw new InvalidOperationException("show failed");
            return inner.CallAsync(method, parameters, ct);
        }
    }

    private sealed class FailFocusPort(ControlPlanePort inner) : IAttachCommandPort
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

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
