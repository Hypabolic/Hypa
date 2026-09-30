using System.Text;
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

/// <summary>
/// Runtime conformance for hidden, tiled, and overlay placements on one
// / pane id.
// / paint.
/// the client stamps one host frame.
/// </summary>
public sealed class HiddenPaneLifecycleMatrixTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public HiddenPaneLifecycleMatrixTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Same_runtime_survives_hidden_tiled_overlay_hide_and_close()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, state, store, sink, journal) = await NewAsync(factory);
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));
            runtime.FireOutput("HIDDEN-MARK-1\n");
            Assert.Contains("HIDDEN-MARK-1", await ReadPaneAsync(cp, hidden));
            await AssertNoJournalTerminalOutputAsync(journal, hidden);
            var beforeCols = state.GetPane(new PaneId(hidden))!.Cols;
            var beforeRows = state.GetPane(new PaneId(hidden))!.Rows;
            var beforeGen = state.GetPane(new PaneId(hidden))!.OccupantGeneration;
            sink.Clear();

            var shown = await ShowTiledAsync(cp, hidden, leaseId, tiled, owner);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(runtime.IsAlive);
            Assert.Null(cp.PeekPendingRuntime(hidden));
            Assert.Equal(1, CountPlacement(state, hidden));
            Assert.Equal(beforeCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(beforeRows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal(beforeGen, state.GetPane(new PaneId(hidden))!.OccupantGeneration);
            Assert.Contains("HIDDEN-MARK-1", await ReadPaneAsync(cp, hidden));

            var hiddenAgain = await HideAsync(cp, hidden, leaseId, owner);
            Assert.Equal("hidden", hiddenAgain.GetProperty("placement").GetString());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(runtime.IsAlive);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.False(LayoutTreeOperations.ContainsPane(
                state.GetTab(state.GetPane(new PaneId(hidden))!.TabId)!.LayoutRoot,
                new PaneId(hidden)));

            var overlay = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            Assert.Equal("overlay", overlay.GetProperty("mode").GetString());
            Assert.Equal("hidden", overlay.GetProperty("placement").GetString());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.Equal(1, CountPlacement(state, hidden));
            Assert.Equal(owner.ConnectionId, cp.Overlay.OwnerOf(new PaneId(hidden))!.AttachClientId);
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            Assert.Equal(geometry.InnerCols, overlay.GetProperty("cols").GetInt32());
            Assert.Equal(geometry.InnerRows, overlay.GetProperty("rows").GetInt32());

            runtime.FireOutput("HIDDEN-MARK-2\n");
            Assert.Contains("HIDDEN-MARK-2", await ReadPaneAsync(cp, hidden));
            await AssertNoJournalTerminalOutputAsync(journal, hidden);

            var overlayHide = await HideOverlayAsync(
                cp, hidden, leaseId, owner, overlay.GetProperty("overlay_generation").GetInt64());
            Assert.Equal("hidden", overlayHide.GetProperty("placement").GetString());
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(runtime.IsAlive);

            var types = EventTypes(sink);
            Assert.Contains(ProtocolEventTypes.PanePlacementChanged, types);
            Assert.Contains(ProtocolEventTypes.LayoutUpdated, types);

            await cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None);
            Assert.Null(cp.PeekRuntime(hidden));
            Assert.Null(state.GetPane(new PaneId(hidden)));
            Assert.False((await store.TryLoadAsync("pane-life")).Value!.Panes.ContainsKey(hidden));
            Assert.Contains(ProtocolEventTypes.PaneLifecycle, EventTypes(sink));
            Assert.Equal(1, CountLifecycleClosed(sink, hidden));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hidden_zero_resize_is_rejected_and_zoom_does_not_reveal()
    {
        var (cp, state, _, _, _) = await NewAsync();
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            var pane = state.GetPane(new PaneId(hidden))!;
            Assert.True(pane.Cols > 0);
            Assert.True(pane.Rows > 0);

            var zero = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneResize,
                    Json(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = leaseId,
                        ["cols"] = 0,
                        ["rows"] = 0,
                    }),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, zero.Code);
            Assert.Equal(pane.Cols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(pane.Rows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);

            var other = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = pane.WorkspaceId.Value,
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                    ["focus"] = false,
                    ["label"] = "zoom-tab",
                }),
                CancellationToken.None);
            var otherTab = new TabId(other.GetProperty("tab_id").GetString()!);
            var zoomedPane = new PaneId(other.GetProperty("pane").GetProperty("pane_id").GetString()!);
            var zoomed = state.SetTabLayout(
                otherTab,
                state.GetTab(otherTab)!.LayoutRoot,
                zoomed: true,
                zoomedPaneId: zoomedPane,
                focusedPaneId: zoomedPane);
            Assert.NotNull(zoomed);

            var neighbourResize = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = tiled, ["scope"] = "resize" }),
                owner,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Json(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["lease_id"] = neighbourResize.GetProperty("lease_id").GetString(),
                    ["cols"] = 100,
                    ["rows"] = 30,
                }),
                owner,
                CancellationToken.None);

            var hiddenPane = state.GetPane(new PaneId(hidden))!;
            Assert.Equal(PanePlacement.Hidden, hiddenPane.Placement);
            Assert.True(hiddenPane.Cols > 0);
            Assert.True(hiddenPane.Rows > 0);
            Assert.False(LayoutTreeOperations.ContainsPane(
                state.GetTab(hiddenPane.TabId)!.LayoutRoot, new PaneId(hidden)));
            Assert.False(LayoutTreeOperations.ContainsPane(
                state.GetTab(otherTab)!.LayoutRoot, new PaneId(hidden)));
            Assert.True(state.GetTab(otherTab)!.Zoomed);
            Assert.Equal(zoomedPane.Value, state.GetTab(otherTab)!.ZoomedPaneId?.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_overlay_resize_keeps_owner_and_retry_succeeds()
    {
        var (cp, state, _, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var generation = shown.GetProperty("overlay_generation").GetInt64();
            var priorCols = state.GetPane(new PaneId(hidden))!.Cols;
            var priorRows = state.GetPane(new PaneId(hidden))!.Rows;

            var bad = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneResize,
                    Json(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = "lease_missing",
                        ["cols"] = 40,
                        ["rows"] = 12,
                    }),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, bad.Code);
            Assert.Equal(owner.ConnectionId, cp.Overlay.OwnerOf(new PaneId(hidden))!.AttachClientId);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(priorCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(priorRows, state.GetPane(new PaneId(hidden))!.Rows);

            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = hidden, ["scope"] = "resize" }),
                owner,
                CancellationToken.None);
            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Json(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                    ["cols"] = priorCols,
                    ["rows"] = priorRows,
                }),
                owner,
                CancellationToken.None);
            Assert.True(resized.GetProperty("ok").GetBoolean());
            Assert.Equal(generation, cp.Overlay.OwnerOf(new PaneId(hidden))!.Generation);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_target_ratio_and_foreign_tab_leave_graph()
    {
        var (cp, state, _, sink, _) = await NewAsync();
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            var runtime = cp.PeekRuntime(hidden);
            var otherWs = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Json(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                    ["label"] = "foreign",
                }),
                CancellationToken.None);
            var foreignTab = otherWs.GetProperty("focused_tab_id").GetString()!;
            var before = state.Snapshot();
            sink.Clear();

            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                ShowTiledAsync(cp, hidden, leaseId, "missing", owner));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, missing.Code);

            var ratio = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = leaseId,
                        ["mode"] = "tiled",
                        ["target_pane_id"] = tiled,
                        ["direction"] = "right",
                        ["ratio"] = 1.5,
                    }),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ratio.Code);

            var foreign = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = leaseId,
                        ["mode"] = "tiled",
                        ["tab_id"] = foreignTab,
                        ["target_pane_id"] = tiled,
                        ["direction"] = "right",
                    }),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, foreign.Code);

            AssertUnchangedHidden(state, before, hidden);
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Persist_failure_leaves_hidden_graph_and_retry_succeeds()
    {
        var sqlite = new SqliteRuntimeSessionStore(_paths);
        var store = new FailingStore(sqlite);
        var (cp, state, _, sink, _) = await NewAsync(store: store);
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            var runtime = cp.PeekRuntime(hidden);
            var before = state.Snapshot();
            sink.Clear();
            store.FailSaves = true;

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                ShowTiledAsync(cp, hidden, leaseId, tiled, owner));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(before.Panes[hidden].TabId.Value, state.GetPane(new PaneId(hidden))!.TabId.Value);
            Assert.False(LayoutTreeOperations.ContainsPane(
                state.GetTab(state.GetPane(new PaneId(hidden))!.TabId)!.LayoutRoot,
                new PaneId(hidden)));
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);

            store.FailSaves = false;
            var shown = await ShowTiledAsync(cp, hidden, leaseId, tiled, owner);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cross_workspace_show_updates_owner_and_durable_restore()
    {
        var (cp, state, store, _, _) = await NewAsync();
        try
        {
            var (hidden, _, owner, leaseId) = await SeedOccupiedAsync(cp);
            var sourceTab = state.GetPane(new PaneId(hidden))!.TabId.Value;
            var dest = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Json(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = false,
                    ["label"] = "dest",
                }),
                CancellationToken.None);
            var destTab = dest.GetProperty("focused_tab_id").GetString()!;
            var destWs = dest.GetProperty("workspace_id").GetString()!;

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = leaseId,
                    ["mode"] = "tiled",
                    ["tab_id"] = destTab,
                }),
                owner,
                CancellationToken.None);
            Assert.Equal(destTab, shown.GetProperty("tab_id").GetString());
            Assert.Equal(destWs, shown.GetProperty("workspace_id").GetString());
            Assert.DoesNotContain(state.GetTab(new TabId(sourceTab))!.PaneIds, p => p.Value == hidden);
            Assert.Contains(state.GetTab(new TabId(destTab))!.PaneIds, p => p.Value == hidden);

            var loaded = (await store.TryLoadAsync("pane-life")).Value!;
            Assert.Equal(PanePlacement.Tiled, loaded.Panes[hidden].Placement);
            Assert.Equal(destTab, loaded.Panes[hidden].TabId.Value);
            Assert.Equal(destWs, loaded.Panes[hidden].WorkspaceId.Value);

            await HideAsync(cp, hidden, leaseId, owner);
            var hiddenLoad = (await store.TryLoadAsync("pane-life")).Value!;
            Assert.Equal(PanePlacement.Hidden, hiddenLoad.Panes[hidden].Placement);
            Assert.Contains(hiddenLoad.Tabs[destTab].HiddenPaneIds, p => p.Value == hidden);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Empty_owner_tab_stays_usable_after_last_leaf_hide()
    {
        var (cp, state, _, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var tabId = state.GetPane(new PaneId(hidden))!.TabId;
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = leaseId,
                    ["mode"] = "tiled",
                    ["tab_id"] = tabId.Value,
                }),
                owner,
                CancellationToken.None);
            await HideAsync(cp, hidden, leaseId, owner);

            var tab = state.GetTab(tabId)!;
            Assert.Empty(tab.PaneIds);
            Assert.Null(tab.LayoutRoot);
            Assert.Contains(tab.HiddenPaneIds, p => p.Value == hidden);
            Assert.NotNull(state.GetTab(tabId));

            var again = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = leaseId,
                    ["mode"] = "tiled",
                    ["tab_id"] = tabId.Value,
                }),
                owner,
                CancellationToken.None);
            Assert.True(again.GetProperty("changed").GetBoolean());
            Assert.Equal([new PaneId(hidden)], state.GetTab(tabId)!.PaneIds);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Tab_close_releases_hidden_runtime_once()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, state, _, sink, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var workspaceId = state.GetPane(new PaneId(hidden))!.WorkspaceId.Value;
            var ownerTab = state.GetPane(new PaneId(hidden))!.TabId.Value;
            await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                    ["label"] = "keep",
                }),
                CancellationToken.None);
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));
            await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            sink.Clear();

            await cp.DispatchAsync(
                ProtocolMethods.TabClose,
                Json(new JsonObject { ["tab_id"] = ownerTab }),
                CancellationToken.None);

            Assert.Null(cp.PeekRuntime(hidden));
            Assert.False(runtime.IsAlive);
            Assert.Null(state.GetPane(new PaneId(hidden)));
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(1, CountLifecycleClosed(sink, hidden));

            var closedAgain = await cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None);
            Assert.True(closedAgain.GetProperty("ok").GetBoolean());
            Assert.Equal(1, CountLifecycleClosed(sink, hidden));

            var nextHidden = await CreateHiddenOnWorkspaceAsync(cp, workspaceId);
            var nextLease = await ClaimInputAsync(cp, nextHidden, owner);
            var next = await ShowOverlayAsync(cp, nextHidden, nextLease, owner, 80, 24);
            Assert.True(next.GetProperty("changed").GetBoolean());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Workspace_close_releases_hidden_runtime_once()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, state, _, sink, _) = await NewAsync(factory);
        try
        {
            await SeedHiddenAsync(cp);
            var extra = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Json(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = false,
                    ["label"] = "extra",
                }),
                CancellationToken.None);
            var hiddenPane = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = extra.GetProperty("workspace_id").GetString(),
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                }),
                CancellationToken.None);
            var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));
            sink.Clear();

            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                Json(new JsonObject { ["workspace_id"] = extra.GetProperty("workspace_id").GetString() }),
                CancellationToken.None);

            Assert.Null(cp.PeekRuntime(hidden));
            Assert.False(runtime.IsAlive);
            Assert.Null(state.GetPane(new PaneId(hidden)));
            Assert.Equal(1, CountLifecycleClosed(sink, hidden));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_exit_closes_overlay_and_emits_lifecycle()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, _, _, sink, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));
            sink.Clear();
            runtime.FireExited(3);

            await WaitUntilAsync(() => !cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(3, runtime.ExitCode);
            // The exit lifecycle is emitted after the output flush, off the exit
            // callback, so wait for it rather than read it at once.
            await WaitUntilAsync(() => EventTypes(sink).Contains(ProtocolEventTypes.PaneLifecycle));
            Assert.Contains(ProtocolEventTypes.PaneLifecycle, EventTypes(sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Close_and_hide_fence_stale_keys_and_reject_disposed_writes()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, _, _, _, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var workspaceId = (await cp.DispatchAsync(
                ProtocolMethods.PaneGet,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None)).GetProperty("workspace_id").GetString()!;
            var nextHidden = await CreateHiddenOnWorkspaceAsync(cp, workspaceId);
            var nextLease = await ClaimInputAsync(cp, nextHidden, owner);
            var shown = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var generation = shown.GetProperty("overlay_generation").GetInt64();
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));

            await HideOverlayAsync(cp, hidden, leaseId, owner, generation);
            Assert.True(runtime.IsAlive);
            var staleHide = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                SendOverlayKeysAsync(cp, hidden, leaseId, owner, generation, "x"));
            Assert.Equal(ProtocolErrorCodes.Fenced, staleHide.Code);

            var shownAgain = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            await cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None);
            Assert.Null(cp.PeekRuntime(hidden));
            Assert.False(runtime.IsAlive);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                runtime.WriteTextAsync("late", CancellationToken.None).AsTask());

            var staleClose = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                SendOverlayKeysAsync(
                    cp, hidden, leaseId, owner, shownAgain.GetProperty("overlay_generation").GetInt64(), "y"));
            Assert.True(
                staleClose.Code is ProtocolErrorCodes.NotFound or ProtocolErrorCodes.Fenced
                    or ProtocolErrorCodes.LeaseRequired);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));

            var next = await ShowOverlayAsync(cp, nextHidden, nextLease, owner, 80, 24);
            Assert.True(next.GetProperty("changed").GetBoolean());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Two_clients_keep_separate_overlay_geometry_and_union()
    {
        var (cp, _, _, _, _) = await NewAsync();
        try
        {
            var (first, ownerA, leaseA) = await SeedHiddenAsync(cp, "conn_a");
            var (second, ownerB, leaseB) = await SeedSecondHiddenAsync(cp, first, "conn_b");
            var firstShow = await ShowOverlayAsync(cp, first, leaseA, ownerA, 80, 24);
            var secondShow = await ShowOverlayAsync(cp, second, leaseB, ownerB, 100, 30);
            var geoA = PopupGeometry.TryResolve(80, 24)!;
            var geoB = PopupGeometry.TryResolve(100, 30)!;
            Assert.NotEqual(geoA.InnerCols, geoB.InnerCols);
            Assert.Equal(geoA.InnerCols, firstShow.GetProperty("cols").GetInt32());
            Assert.Equal(geoB.InnerCols, secondShow.GetProperty("cols").GetInt32());
            Assert.Equal(ownerA.ConnectionId, cp.Overlay.OwnerOf(new PaneId(first))!.AttachClientId);
            Assert.Equal(ownerB.ConnectionId, cp.Overlay.OwnerOf(new PaneId(second))!.AttachClientId);

            var stolen = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                ShowOverlayAsync(cp, first, leaseA, ownerB, 100, 30));
            Assert.Equal("ui_busy", stolen.Message);

            var foreignKeys = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                SendOverlayKeysAsync(
                    cp, first, leaseA, ownerB, firstShow.GetProperty("overlay_generation").GetInt64(), "z"));
            Assert.True(
                foreignKeys.Code is ProtocolErrorCodes.LeaseRequired or ProtocolErrorCodes.Fenced
                    or ProtocolErrorCodes.InvalidState);

            var hook = Assert.IsType<OverlayVisibleSetHook>(cp.OverlayVisibleSet);
            var union = hook.UnionOverlayPaneIds();
            Assert.Contains(first, union);
            Assert.Contains(second, union);
            Assert.Equal(2, union.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Busy_modes_reject_overlay_and_hide_keeps_runtime()
    {
        var (cp, _, _, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await PublishModeAsync(cp, owner, "copy");
            var copyBusy = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24));
            Assert.Equal("ui_busy", copyBusy.Message);

            await PublishModeAsync(cp, owner, "settings");
            var settingsBusy = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24));
            Assert.Equal("ui_busy", settingsBusy.Message);

            await PublishModeAsync(cp, owner, "terminal");
            var shown = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var popup = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PopupOpen,
                    Json(new JsonObject { ["command"] = "/bin/true", ["client_mode"] = "terminal" }),
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", popup.Message);

            var runtime = cp.PeekRuntime(hidden);
            await HideOverlayAsync(
                cp, hidden, leaseId, owner, shown.GetProperty("overlay_generation").GetInt64());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disconnect_clears_overlay_and_reload_does_not_restore_it()
    {
        var (cp, state, store, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            cp.OnClientDisconnected(owner);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.True(cp.PeekRuntime(hidden)!.IsAlive);

            var loaded = (await store.TryLoadAsync("pane-life")).Value!;
            Assert.Equal(PanePlacement.Hidden, loaded.Panes[hidden].Placement);
            var restored = new AppState(SessionId.New("pane-life-restore"));
            restored.Replace(loaded);
            Assert.Equal(PanePlacement.Hidden, restored.GetPane(new PaneId(hidden))!.Placement);
            Assert.True(new PaneOverlayService(restored).OwnerOf(new PaneId(hidden)) is null);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Concurrent_hide_and_close_leave_no_stuck_owner()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, _, _, _, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var generation = shown.GetProperty("overlay_generation").GetInt64();

            var hide = HideOverlayAsync(cp, hidden, leaseId, owner, generation);
            var close = cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None);
            try
            {
                await hide;
            }
            catch (ControlPlaneException)
            {
                // Close can win the race and remove the pane first.
            }

            try
            {
                await close;
            }
            catch (ControlPlaneException)
            {
                // Hide can win and close still succeeds, or close already ran.
            }

            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Null(cp.PeekRuntime(hidden));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Cp, AppState State, IRuntimeSessionStore Store, CapturingSink Sink, FileRuntimeEventJournal Journal)>
        NewAsync(IPaneRuntimeFactory? factory = null, IRuntimeSessionStore? store = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-life"));
        state.UpdateSession(s => s with { Name = "pane-life", LifecycleState = SessionLifecycle.Ready });
        store ??= new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            factory ?? TestPaneFactories.Scripted(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        var sink = new CapturingSink();
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = new JsonArray(
                    "lifecycle",
                    "output",
                    "control",
                    ProtocolEventTypes.PanePlacementChanged,
                    ProtocolEventTypes.LayoutUpdated),
                ["live"] = true,
            }),
            sink,
            CancellationToken.None);
        await cp.CompleteEventsSubscribeAsync(
            sub.GetProperty("subscription_id").GetString()!,
            [],
            sink,
            CancellationToken.None);
        return (cp, state, store, sink, journal);
    }

    private async Task<(string Hidden, FakeConnection Owner, string LeaseId)> SeedHiddenAsync(
        ControlPlaneService cp,
        string ownerId = "conn_owner",
        bool publishMode = true)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Json(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = false,
            }),
            CancellationToken.None);
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = ws.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }),
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, ownerId);
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = hidden, ["scope"] = "input" }),
            owner,
            CancellationToken.None);
        if (publishMode)
            await PublishModeAsync(cp, owner, "terminal");
        return (hidden, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private async Task<(string Hidden, string Tiled, FakeConnection Owner, string LeaseId)>
        SeedOccupiedAsync(ControlPlaneService cp)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Json(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/echo",
            }),
            CancellationToken.None);
        var tiled = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = ws.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }),
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, "conn_owner");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = hidden, ["scope"] = "input" }),
            owner,
            CancellationToken.None);
        await PublishModeAsync(cp, owner, "terminal");
        return (hidden, tiled, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private static async Task<string> CreateHiddenOnWorkspaceAsync(
        ControlPlaneService cp,
        string workspaceId)
    {
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }),
            CancellationToken.None);
        return hiddenPane.GetProperty("pane_id").GetString()!;
    }

    private static async Task<string> ClaimInputAsync(
        ControlPlaneService cp,
        string paneId,
        FakeConnection owner)
    {
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = paneId, ["scope"] = "input" }),
            owner,
            CancellationToken.None);
        return claim.GetProperty("lease_id").GetString()!;
    }

    private async Task<(string Hidden, FakeConnection Owner, string LeaseId)> SeedSecondHiddenAsync(
        ControlPlaneService cp,
        string firstPaneId,
        string ownerId)
    {
        var pane = await cp.DispatchAsync(
            ProtocolMethods.PaneGet,
            Json(new JsonObject { ["pane_id"] = firstPaneId }),
            CancellationToken.None);
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = pane.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }),
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, ownerId);
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = hidden, ["scope"] = "input" }),
            owner,
            CancellationToken.None);
        await PublishModeAsync(cp, owner, "terminal");
        return (hidden, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private static async Task<FakeConnection> SubscribeAsync(ControlPlaneService cp, string id)
    {
        var conn = new FakeConnection(id);
        await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject { ["from_seq"] = 0, ["live"] = true }),
            conn,
            CancellationToken.None);
        return conn;
    }

    private static Task PublishModeAsync(ControlPlaneService cp, FakeConnection owner, string mode) =>
        cp.DispatchAsync(
            ProtocolMethods.UiClientMode,
            Json(new JsonObject { ["client_mode"] = mode }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> ShowTiledAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        string target,
        FakeConnection owner) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneShow,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["mode"] = "tiled",
                ["direction"] = "right",
                ["target_pane_id"] = target,
            }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> ShowOverlayAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner,
        int areaCols,
        int areaRows) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneShow,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["mode"] = "overlay",
                ["attach_client_id"] = owner.ConnectionId,
                ["area_cols"] = areaCols,
                ["area_rows"] = areaRows,
            }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> HideAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneHide,
            Json(new JsonObject { ["pane_id"] = paneId, ["lease_id"] = leaseId }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> HideOverlayAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner,
        long generation) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneHide,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["attach_client_id"] = owner.ConnectionId,
                ["overlay_generation"] = generation,
            }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> SendOverlayKeysAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner,
        long generation,
        string text) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["encoding"] = "base64",
                ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                ["overlay_generation"] = generation,
            }),
            owner,
            CancellationToken.None);

    private static async Task<string> ReadPaneAsync(ControlPlaneService cp, string paneId)
    {
        var read = await cp.DispatchAsync(
            ProtocolMethods.PaneRead,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = ProtocolPaneReadSources.Visible,
            }),
            CancellationToken.None);
        return read.GetProperty("text").GetString() ?? "";
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();

    private static List<string> EventTypes(CapturingSink sink) =>
        sink.Parsed
            .Where(ev => ev.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            .Select(ev => ev.GetProperty("params").GetProperty("type").GetString()!)
            .ToList();

    private static int CountLifecycleClosed(CapturingSink sink, string paneId) =>
        sink.Parsed.Count(ev =>
            ev.TryGetProperty("params", out var p)
            && p.TryGetProperty("type", out var type)
            && type.GetString() == ProtocolEventTypes.PaneLifecycle
            && p.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("pane_id", out var id)
            && id.GetString() == paneId
            && payload.TryGetProperty("state", out var state)
            && string.Equals(state.GetString(), "closed", StringComparison.OrdinalIgnoreCase));

    private static int CountPlacement(AppState state, string paneId)
    {
        var pane = state.GetPane(new PaneId(paneId));
        if (pane is null)
            return 0;
        var n = 0;
        foreach (var tab in state.Snapshot().Tabs.Values)
        {
            if (tab.PaneIds.Any(p => p.Value == paneId))
                n++;
            if (tab.HiddenPaneIds.Any(p => p.Value == paneId))
                n++;
        }

        if (pane.Placement == PanePlacement.Tiled)
            Assert.Equal(1, n);
        return n;
    }

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

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var start = DateTime.UtcNow;
        while (!ready())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
                throw new TimeoutException("condition was not met");
            await Task.Delay(10);
        }
    }

    private static async Task AssertNoJournalTerminalOutputAsync(
        FileRuntimeEventJournal journal, string paneId)
    {
        await Task.Delay(30);
        var range = await journal.ReadRangeAsync(
            0,
            new HashSet<EventClass> { EventClass.Output },
            budget: 50,
            CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.DoesNotContain(
            range.Value,
            rec => rec.Type == ProtocolEventTypes.TerminalOutput
                && rec.PayloadJson.Contains(paneId, StringComparison.Ordinal));
    }

    private sealed class FailingStore(IRuntimeSessionStore inner) : IRuntimeSessionStore
    {
        public bool FailSaves { get; set; }

        public Task<RuntimeResult<SessionState?>> TryLoadAsync(
            string sessionName,
            CancellationToken ct = default) =>
            inner.TryLoadAsync(sessionName, ct);

        public Task<RuntimeResult<RuntimeUnit>> SaveAsync(
            SessionState state,
            CancellationToken ct = default,
            bool removeMissingPanes = false) =>
            FailSaves
                ? Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected store failure")))
                : inner.SaveAsync(state, ct, removeMissingPanes);

        public Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(
            PaneId paneId,
            CancellationToken ct = default) =>
            inner.DeletePaneAsync(paneId, ct);

        public Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default) =>
            inner.GetRuntimeSessionIdAsync(ct);
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_life";
        public List<string> Lines { get; } = [];

        public IReadOnlyList<JsonElement> Parsed
        {
            get
            {
                lock (_gate)
                    return Lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            }
        }

        public void Clear()
        {
            lock (_gate)
                Lines.Clear();
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }
}
