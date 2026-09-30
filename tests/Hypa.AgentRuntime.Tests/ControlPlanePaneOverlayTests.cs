using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Overlay;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlanePaneOverlayTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public ControlPlanePaneOverlayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-ov-t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Overlay_show_wire_and_unique_owner()
    {
        var (cp, _, sink) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);

            Assert.True(shown.GetProperty("ok").GetBoolean());
            Assert.Equal("overlay", shown.GetProperty("mode").GetString());
            Assert.Equal("hidden", shown.GetProperty("placement").GetString());
            Assert.True(shown.GetProperty("hidden").GetBoolean());
            Assert.Equal(owner.ConnectionId, shown.GetProperty("attach_client_id").GetString());
            Assert.True(shown.GetProperty("overlay_generation").GetInt64() > 0);
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            Assert.Equal(geometry.InnerCols, shown.GetProperty("cols").GetInt32());
            Assert.Equal(geometry.InnerRows, shown.GetProperty("rows").GetInt32());
            Assert.Equal(hidden, cp.Overlay.OwnerOf(new PaneId(hidden))!.PaneId.Value);
            var hook = Assert.IsType<OverlayVisibleSetHook>(cp.OverlayVisibleSet);
            Assert.Equal(hidden, hook.OverlayPaneId(owner.ConnectionId));

            var other = await SubscribeAsync(cp, "conn_other");
            await PublishModeAsync(cp, other, "terminal");
            var second = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShow(hidden, leaseId, other.ConnectionId, 100, 30),
                    other,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, second.Code);
            Assert.Equal("ui_busy", second.Message);
            Assert.Equal(owner.ConnectionId, cp.Overlay.OwnerOf(new PaneId(hidden))!.AttachClientId);

            var types = EventTypes(sink);
            Assert.Contains(ProtocolEventTypes.PanePlacementChanged, types);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_lease_and_failed_tiled_show_keep_overlay()
    {
        var (cp, state, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            var badLease = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowTiled(hidden, "lease_missing", direction: "right", target: "missing"),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, badLease.Code);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);

            var badTarget = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    ShowTiled(hidden, leaseId, direction: "right", target: "missing"),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, badTarget.Code);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_invalid_and_stale_seq_leave_owner_unchanged()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var occupant = RequireOccupant(factory, hidden);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            var generation = cp.Overlay.TryGet(owner.ConnectionId)!.Generation;
            var placement = state.GetPane(new PaneId(hidden))!.Placement;

            var forged = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShowOccupant(hidden, "occ_forged", owner.ConnectionId, 1, leaseId),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, forged.Code);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(generation, cp.Overlay.TryGet(owner.ConnectionId)!.Generation);
            Assert.Equal(placement, state.GetPane(new PaneId(hidden))!.Placement);

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                HideOverlay(hidden, leaseId, owner.ConnectionId, generation),
                owner,
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));

            await PublishModeAsync(cp, owner, "terminal");
            var first = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShowOccupant(hidden, occupant, owner.ConnectionId, 3, leaseId),
                owner,
                CancellationToken.None);
            Assert.True(first.GetProperty("changed").GetBoolean());
            var accepted = first.GetProperty("overlay_generation").GetInt64();

            var stale = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                OverlayHideOccupant(hidden, occupant, owner.ConnectionId, 3, accepted, leaseId),
                owner,
                CancellationToken.None);
            Assert.False(stale.GetProperty("changed").GetBoolean());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(accepted, cp.Overlay.TryGet(owner.ConnectionId)!.Generation);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_persist_failure_keeps_owner_and_retries()
    {
        var sqlite = await OpenStoreAsync();
        var store = new ControllableOverlayStore(sqlite);
        var factory = new RecordingResizeFactory();
        var (cp, state, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var prior = state.GetPane(new PaneId(hidden))!;
            var priorCols = prior.Cols;
            var priorRows = prior.Rows;
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            store.FailSaves = true;
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(priorCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(priorRows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Contains((geometry.InnerCols, geometry.InnerRows), factory.ResizesFor(hidden));
            Assert.Equal((priorCols, priorRows), factory.ResizesFor(hidden)[^1]);

            store.FailSaves = false;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.True(cp.VisibleSets.MayCapture(hidden, DateTimeOffset.UtcNow));
            Assert.Equal(geometry.InnerCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(geometry.InnerRows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal((geometry.InnerCols, geometry.InnerRows), factory.ResizesFor(hidden)[^1]);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_blocked_show_persist_keeps_same_pane_lease_resize()
    {
        var sqlite = await OpenStoreAsync();
        var store = new ControllableOverlayStore(sqlite);
        var factory = new RecordingResizeFactory();
        var (cp, state, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            store.BlockNextSave();
            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = leaseId,
                    ["cols"] = 40,
                    ["rows"] = 12,
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            Assert.True(resized.GetProperty("ok").GetBoolean());
            store.ReleaseBlockedSave();
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => show);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(40, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(12, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal((40, 12), factory.ResizesFor(hidden)[^1]);
        }
        finally
        {
            store.ReleaseBlockedSave();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_blocked_rollback_resize_serializes_later_lease_resize()
    {
        var sqlite = await OpenStoreAsync();
        var store = new ControllableOverlayStore(sqlite);
        var factory = new RecordingResizeFactory();
        var (cp, state, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var prior = state.GetPane(new PaneId(hidden))!;
            var priorCols = prior.Cols;
            var priorRows = prior.Rows;
            Assert.True(priorCols > 0);
            Assert.True(priorRows > 0);
            factory.BlockResize(priorCols, priorRows);
            store.FailSaves = true;
            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            await factory.ResizeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var resizeTask = cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = leaseId,
                    ["cols"] = 40,
                    ["rows"] = 12,
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                resizeTask.WaitAsync(TimeSpan.FromMilliseconds(300)));
            Assert.False(resizeTask.IsCompleted);
            factory.ReleaseBlockedResize();
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => show);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            var resized = await resizeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(resized.GetProperty("ok").GetBoolean());
            Assert.Equal(40, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(12, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal((40, 12), factory.ResizesFor(hidden)[^1]);
        }
        finally
        {
            store.FailSaves = false;
            factory.ReleaseBlockedResize();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_cancelled_persist_failure_restores_runtime_and_retries()
    {
        var sqlite = await OpenStoreAsync();
        var store = new ControllableOverlayStore(sqlite);
        var factory = new RecordingResizeFactory();
        var (cp, state, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var occupant = factory.Occupant(hidden);
            var prior = state.GetPane(new PaneId(hidden))!;
            var priorCols = prior.Cols;
            var priorRows = prior.Rows;
            Assert.True(priorCols > 0);
            Assert.True(priorRows > 0);
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            using var cts = new CancellationTokenSource();
            store.CancelOnSave = cts;
            store.FailSaves = true;
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShowOccupant(hidden, occupant, owner.ConnectionId, 11, leaseId, 80, 24),
                    owner,
                    cts.Token));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            Assert.Equal(priorCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(priorRows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Contains((geometry.InnerCols, geometry.InnerRows), factory.ResizesFor(hidden));
            Assert.Equal((priorCols, priorRows), factory.ResizesFor(hidden)[^1]);
            Assert.False(factory.LastResizeWasCancelled(hidden));

            store.FailSaves = false;
            store.CancelOnSave = null;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShowOccupant(hidden, occupant, owner.ConnectionId, 11, leaseId, 80, 24),
                owner,
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(geometry.InnerCols, state.GetPane(new PaneId(hidden))!.Cols);
            Assert.Equal(geometry.InnerRows, state.GetPane(new PaneId(hidden))!.Rows);
            Assert.Equal((geometry.InnerCols, geometry.InnerRows), factory.ResizesFor(hidden)[^1]);
        }
        finally
        {
            store.FailSaves = false;
            store.CancelOnSave = null;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_blocked_hide_persist_keeps_later_owner_and_resize()
    {
        var sqlite = await OpenStoreAsync();
        var store = new ControllableOverlayStore(sqlite);
        var factory = new RecordingResizeFactory();
        var (cp, state, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var (first, owner, firstLease) = await SeedHiddenAsync(cp);
            var (second, secondLease) = await SeedHiddenForOwnerAsync(cp, first, owner);
            var shownFirst = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(first, firstLease, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            var firstGeneration = shownFirst.GetProperty("overlay_generation").GetInt64();
            var firstAfterShow = state.GetPane(new PaneId(first))!;
            var firstCols = firstAfterShow.Cols;
            var firstRows = firstAfterShow.Rows;
            Assert.True(firstCols > 0);
            Assert.True(firstRows > 0);

            store.SaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.HoldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.FailNextSave = true;
            var hideTask = cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                HideOverlay(first, firstLease, owner.ConnectionId, firstGeneration),
                owner,
                CancellationToken.None);
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var showTask = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(second, secondLease, owner.ConnectionId, 100, 30),
                owner,
                CancellationToken.None);
            await WaitUntilAsync(
                () => cp.Overlay.OwnerOf(new PaneId(second)) is not null,
                TimeSpan.FromSeconds(5));
            var laterGeneration = cp.Overlay.OwnerOf(new PaneId(second))!.Generation;

            store.HoldSave.TrySetResult();
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => hideTask);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);

            var shownSecond = await showTask;
            Assert.True(shownSecond.GetProperty("changed").GetBoolean());
            var ownerAfter = cp.Overlay.TryGet(owner.ConnectionId);
            Assert.NotNull(ownerAfter);
            Assert.Equal(second, ownerAfter.PaneId.Value);
            Assert.Equal(laterGeneration, ownerAfter.Generation);
            Assert.True(ownerAfter.Generation > firstGeneration);
            Assert.Null(cp.Overlay.OwnerOf(new PaneId(first)));
            Assert.Equal(firstCols, state.GetPane(new PaneId(first))!.Cols);
            Assert.Equal(firstRows, state.GetPane(new PaneId(first))!.Rows);

            var secondGeometry = PopupGeometry.TryResolve(100, 30)!;
            Assert.Equal(secondGeometry.InnerCols, state.GetPane(new PaneId(second))!.Cols);
            Assert.Equal(secondGeometry.InnerRows, state.GetPane(new PaneId(second))!.Rows);

            var resizeClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = second,
                    ["scope"] = "resize",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var later = PopupGeometry.TryResolve(90, 28)!;
            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = second,
                    ["lease_id"] = resizeClaim.GetProperty("lease_id").GetString(),
                    ["cols"] = later.InnerCols,
                    ["rows"] = later.InnerRows,
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            Assert.True(resized.GetProperty("ok").GetBoolean());
            Assert.Equal(second, cp.Overlay.TryGet(owner.ConnectionId)!.PaneId.Value);
            Assert.Equal(laterGeneration, cp.Overlay.TryGet(owner.ConnectionId)!.Generation);
            Assert.Equal(later.InnerCols, state.GetPane(new PaneId(second))!.Cols);
            Assert.Equal(later.InnerRows, state.GetPane(new PaneId(second))!.Rows);
            Assert.Equal((later.InnerCols, later.InnerRows), factory.ResizesFor(second)[^1]);
            Assert.Equal(firstCols, state.GetPane(new PaneId(first))!.Cols);
            Assert.Equal(firstRows, state.GetPane(new PaneId(first))!.Rows);
        }
        finally
        {
            store.HoldSave?.TrySetResult();
            store.FailSaves = false;
            store.FailNextSave = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Two_panes_two_owners_enter_visible_union()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (firstPane, first, firstLease) = await SeedHiddenAsync(cp, "conn_a");
            var (secondPane, second, secondLease) = await SeedSecondHiddenAsync(cp, firstPane, "conn_b");
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(firstPane, firstLease, first.ConnectionId, 80, 24),
                first,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(secondPane, secondLease, second.ConnectionId, 100, 30),
                second,
                CancellationToken.None);
            var hook = Assert.IsType<OverlayVisibleSetHook>(cp.OverlayVisibleSet);
            var union = hook.UnionOverlayPaneIds();
            Assert.Contains(firstPane, union);
            Assert.Contains(secondPane, union);
            Assert.Equal(2, union.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Published_mode_and_popup_are_reverse_busy()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.UiClientMode,
                JsonDocument.Parse("""{"client_mode":"settings"}""").RootElement,
                owner,
                CancellationToken.None);

            var busy = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShow(hidden, leaseId, owner.ConnectionId),
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", busy.Message);

            await cp.DispatchAsync(
                ProtocolMethods.UiClientMode,
                JsonDocument.Parse("""{"client_mode":"copy"}""").RootElement,
                owner,
                CancellationToken.None);
            var copyBusy = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShow(hidden, leaseId, owner.ConnectionId),
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", copyBusy.Message);

            await cp.DispatchAsync(
                ProtocolMethods.UiClientMode,
                JsonDocument.Parse("""{"client_mode":"terminal"}""").RootElement,
                owner,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);

            var popup = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PopupOpen,
                    JsonDocument.Parse("""{"command":"/bin/true","client_mode":"terminal"}""").RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", popup.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reserved_owner_refuses_busy_mode_and_keeps_overlay_publication()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            foreach (var busy in new[] { "worktree", "settings", "copy", "hiddenlist", "globalmenu" })
            {
                var rejected = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                    cp.DispatchAsync(
                        ProtocolMethods.UiClientMode,
                        JsonDocument.Parse(new JsonObject { ["client_mode"] = busy }.ToJsonString()).RootElement,
                        owner,
                        CancellationToken.None));
                Assert.Equal("ui_busy", rejected.Message);
                Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));
            }

            var overlayMode = await cp.DispatchAsync(
                ProtocolMethods.UiClientMode,
                JsonDocument.Parse("""{"client_mode":"overlay"}""").RootElement,
                owner,
                CancellationToken.None);
            Assert.Equal("overlay", overlayMode.GetProperty("client_mode").GetString());
            var terminalMode = await cp.DispatchAsync(
                ProtocolMethods.UiClientMode,
                JsonDocument.Parse("""{"client_mode":"terminal"}""").RootElement,
                owner,
                CancellationToken.None);
            Assert.Equal("terminal", terminalMode.GetProperty("client_mode").GetString());
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            var renewBusy = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.RuntimeLeaseRenew,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["lease_id"] = leaseId,
                        ["ttl_ms"] = 30_000,
                        ["client_mode"] = "worktree",
                    }.ToJsonString()).RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", renewBusy.Message);
            Assert.True(shown.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Owner_cancel_hide_releases_and_rejects_foreign_or_stale()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            var generation = shown.GetProperty("overlay_generation").GetInt64();
            var foreign = await SubscribeAsync(cp, "conn_foreign");
            await PublishModeAsync(cp, foreign, "terminal");

            var missingConnection = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneHide,
                    OwnerCancelHide(hidden, owner.ConnectionId, generation),
                    connection: null,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, missingConnection.Code);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            var foreignHide = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneHide,
                    OwnerCancelHide(hidden, owner.ConnectionId, generation),
                    foreign,
                    CancellationToken.None));
            Assert.Equal("ui_busy", foreignHide.Message);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            var stale = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneHide,
                    OwnerCancelHide(hidden, owner.ConnectionId, generation - 1),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.Fenced, stale.Code);
            Assert.True(cp.Overlay.HasReservation(owner.ConnectionId));

            var cancelled = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                OwnerCancelHide(hidden, owner.ConnectionId, generation),
                owner,
                CancellationToken.None);
            Assert.True(cancelled.GetProperty("ok").GetBoolean());
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Subscribe_lists_owner_without_tiled_lease()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var listed = await SubscribeAsync(cp, "conn_empty");
            var snap = await cp.DispatchAsync(
                ProtocolMethods.SessionSnapshot,
                JsonDocument.Parse("{}").RootElement,
                CancellationToken.None);
            var ids = snap.GetProperty("attach_client_ids").EnumerateArray()
                .Select(x => x.GetString())
                .ToList();
            Assert.Contains(listed.ConnectionId, ids);
            Assert.Contains(
                snap.GetProperty("attach_clients").EnumerateArray(),
                c => c.GetProperty("attach_client_id").GetString() == listed.ConnectionId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hide_fences_input_and_disconnect_releases()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            var generation = shown.GetProperty("overlay_generation").GetInt64();

            await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                HideOverlay(hidden, leaseId, owner.ConnectionId, generation),
                owner,
                CancellationToken.None);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));

            var stale = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneSendKeys,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = leaseId,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("x"u8.ToArray()),
                        ["overlay_generation"] = generation,
                    }.ToJsonString()).RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.Fenced, stale.Code);

            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            cp.OnClientDisconnected(owner);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_exit_releases_overlay()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, _, _) = await NewAsync(factory);
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            var runtime = Assert.IsType<TestPaneFactories.ScriptedPaneRuntime>(cp.PeekRuntime(hidden));
            runtime.FireExited(0);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(0, runtime.ExitCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_published_mode_rejects_overlay()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp, publishMode: false);
            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    OverlayShow(hidden, leaseId, owner.ConnectionId),
                    owner,
                    CancellationToken.None));
            Assert.Equal("ui_busy", missing.Message);

            await PublishModeAsync(cp, owner, "terminal");
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            Assert.True(shown.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_show_without_tiled_pane()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, leaseId) = await SeedHiddenAsync(cp);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            Assert.True(shown.GetProperty("ok").GetBoolean());
            Assert.Equal(hidden, cp.Overlay.OwnerOf(new PaneId(hidden))!.PaneId.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_ops_require_hidden_pane_leases_not_tiled()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, tiled, owner, hiddenLease) = await SeedOccupiedAsync(cp);
            var tiledClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["scope"] = "resize",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var tiledResize = tiledClaim.GetProperty("lease_id").GetString()!;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, hiddenLease, owner.ConnectionId, 80, 24),
                owner,
                CancellationToken.None);
            var generation = shown.GetProperty("overlay_generation").GetInt64();
            var geometry = PopupGeometry.TryResolve(80, 24)!;

            var wrongResize = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneResize,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = tiledResize,
                        ["cols"] = geometry.InnerCols,
                        ["rows"] = geometry.InnerRows,
                    }.ToJsonString()).RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, wrongResize.Code);

            var hiddenResizeClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["scope"] = "resize",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var hiddenResize = hiddenResizeClaim.GetProperty("lease_id").GetString()!;
            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = hiddenResize,
                    ["cols"] = geometry.InnerCols,
                    ["rows"] = geometry.InnerRows,
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            Assert.True(resized.GetProperty("ok").GetBoolean());

            var tiledInput = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["scope"] = "input",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var wrongKeys = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneSendKeys,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = tiledInput.GetProperty("lease_id").GetString(),
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("x"u8.ToArray()),
                        ["overlay_generation"] = generation,
                    }.ToJsonString()).RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, wrongKeys.Code);

            var okKeys = await cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = hiddenLease,
                    ["encoding"] = "base64",
                    ["data"] = Convert.ToBase64String("x"u8.ToArray()),
                    ["overlay_generation"] = generation,
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            Assert.True(okKeys.GetProperty("accepted_bytes").GetInt32() > 0);

            var hidden2 = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                HideOverlay(hidden, hiddenLease, owner.ConnectionId, generation),
                owner,
                CancellationToken.None);
            Assert.True(hidden2.GetProperty("overlay_generation").GetInt64() > generation);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Already_held_input_survives_failed_resize_claim()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, hiddenLease) = await SeedHiddenAsync(cp);
            var other = await SubscribeAsync(cp, "conn_other");
            await PublishModeAsync(cp, other, "terminal");
            await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["scope"] = "resize",
                }.ToJsonString()).RootElement,
                other,
                CancellationToken.None);
            var port = new OwnerPort(cp, owner);
            var pair = await TargetPaneLeasePair.TryClaimAsync(
                port, hidden, CancellationToken.None, takeover: false);
            Assert.Null(pair);
            var keys = await cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = hidden,
                    ["lease_id"] = hiddenLease,
                    ["encoding"] = "base64",
                    ["data"] = Convert.ToBase64String("x"u8.ToArray()),
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            Assert.True(keys.GetProperty("accepted_bytes").GetInt32() > 0);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Newly_granted_input_is_released_when_resize_claim_throws()
    {
        var (cp, _, _) = await NewAsync();
        try
        {
            var (hidden, owner, hiddenLease) = await SeedHiddenAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseRelease,
                JsonDocument.Parse(new JsonObject { ["lease_id"] = hiddenLease }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var inner = new OwnerPort(cp, owner);
            var throwing = new ThrowOnResizeClaim(inner);
            await Assert.ThrowsAsync<ControlPlaneException>(() =>
                TargetPaneLeasePair.TryClaimAsync(throwing, hidden, CancellationToken.None));
            Assert.False(string.IsNullOrWhiteSpace(throwing.GrantedInput));
            var stale = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneSendKeys,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = hidden,
                        ["lease_id"] = throwing.GrantedInput,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("x"u8.ToArray()),
                    }.ToJsonString()).RootElement,
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, stale.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Successful_tiled_show_clears_overlay()
    {
        var (cp, state, _) = await NewAsync();
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                OverlayShow(hidden, leaseId, owner.ConnectionId),
                owner,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                ShowTiled(hidden, leaseId, direction: "right", target: tiled),
                owner,
                CancellationToken.None);
            Assert.False(cp.Overlay.HasReservation(owner.ConnectionId));
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(hidden))!.Placement);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(string Hidden, FakeConnection Owner, string LeaseId)> SeedHiddenAsync(
        ControlPlaneService cp,
        string ownerId = "conn_owner",
        bool publishMode = true)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = false,
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = ws.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, ownerId);
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
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
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/echo",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var tiled = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = ws.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, "conn_owner");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
            owner,
            CancellationToken.None);
        await PublishModeAsync(cp, owner, "terminal");
        return (hidden, tiled, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private async Task<(string Hidden, string LeaseId)> SeedHiddenForOwnerAsync(
        ControlPlaneService cp,
        string firstPaneId,
        FakeConnection owner)
    {
        var pane = await cp.DispatchAsync(
            ProtocolMethods.PaneGet,
            JsonDocument.Parse(new JsonObject { ["pane_id"] = firstPaneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = pane.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
            owner,
            CancellationToken.None);
        return (hidden, claim.GetProperty("lease_id").GetString()!);
    }

    private static async Task WaitUntilAsync(Func<bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("overlay interleave did not reach the later owner");
            await Task.Delay(10);
        }
    }

    private async Task<(string Hidden, FakeConnection Owner, string LeaseId)> SeedSecondHiddenAsync(
        ControlPlaneService cp,
        string firstPaneId,
        string ownerId)
    {
        var pane = await cp.DispatchAsync(
            ProtocolMethods.PaneGet,
            JsonDocument.Parse(new JsonObject { ["pane_id"] = firstPaneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = pane.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = await SubscribeAsync(cp, ownerId);
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
            owner,
            CancellationToken.None);
        await PublishModeAsync(cp, owner, "terminal");
        return (hidden, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private async Task<SqliteRuntimeSessionStore> OpenStoreAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        return new SqliteRuntimeSessionStore(_paths);
    }

    private static string RequireOccupant(TestPaneFactories.CapturingPaneFactory factory, string paneId)
    {
        var options = factory.Options.Last(o => o.Id.Value == paneId);
        Assert.True(options.Env!.TryGetValue(PaneIdEnvironment.HypaPaneToken, out var token));
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    private async Task<(ControlPlaneService Cp, AppState State, CapturingSink Sink)> NewAsync(
        IPaneRuntimeFactory? factory = null,
        IRuntimeSessionStore? persistStore = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-ov"));
        state.UpdateSession(s => s with { Name = "pane-ov", LifecycleState = SessionLifecycle.Ready });
        var store = persistStore as SqliteRuntimeSessionStore
            ?? (persistStore as ControllableOverlayStore)?.Inner
            ?? await OpenStoreAsync();
        var usedStore = persistStore ?? store;
        Assert.True((await usedStore.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            factory ?? TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: usedStore,
            journal: journal,
            subscriptions: hub);
        var sink = new CapturingSink();
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = new JsonArray(ProtocolEventTypes.PanePlacementChanged),
                ["live"] = true,
            }.ToJsonString()).RootElement,
            sink,
            CancellationToken.None);
        await cp.CompleteEventsSubscribeAsync(
            sub.GetProperty("subscription_id").GetString()!,
            [],
            sink,
            CancellationToken.None);
        return (cp, state, sink);
    }

    private static async Task<FakeConnection> SubscribeAsync(ControlPlaneService cp, string id)
    {
        var conn = new FakeConnection(id);
        await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
            }.ToJsonString()).RootElement,
            conn,
            CancellationToken.None);
        return conn;
    }

    private static async Task PublishModeAsync(
        ControlPlaneService cp,
        FakeConnection owner,
        string mode)
    {
        await cp.DispatchAsync(
            ProtocolMethods.UiClientMode,
            JsonDocument.Parse(new JsonObject { ["client_mode"] = mode }.ToJsonString()).RootElement,
            owner,
            CancellationToken.None);
    }

    private static JsonElement OverlayShowOccupant(
        string paneId,
        string occupantToken,
        string attachClientId,
        long seq,
        string leaseId,
        int? areaCols = null,
        int? areaRows = null)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = paneId,
            ["occupant_token"] = occupantToken,
            ["lease_id"] = leaseId,
            ["mode"] = "overlay",
            ["attach_client_id"] = attachClientId,
            ["seq"] = seq,
        };
        if (areaCols is not null)
            obj["area_cols"] = areaCols;
        if (areaRows is not null)
            obj["area_rows"] = areaRows;
        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement OverlayHideOccupant(
        string paneId,
        string occupantToken,
        string attachClientId,
        long seq,
        long generation,
        string leaseId) =>
        JsonDocument.Parse(new JsonObject
        {
            ["pane_id"] = paneId,
            ["occupant_token"] = occupantToken,
            ["lease_id"] = leaseId,
            ["attach_client_id"] = attachClientId,
            ["seq"] = seq,
            ["overlay_generation"] = generation,
        }.ToJsonString()).RootElement.Clone();

    private static JsonElement OverlayShow(
        string paneId,
        string leaseId,
        string attachClientId,
        int? areaCols = null,
        int? areaRows = null)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
            ["mode"] = "overlay",
            ["attach_client_id"] = attachClientId,
        };
        if (areaCols is not null)
            obj["area_cols"] = areaCols;
        if (areaRows is not null)
            obj["area_rows"] = areaRows;
        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement ShowTiled(
        string paneId,
        string leaseId,
        string? direction = null,
        string? target = null)
    {
        var obj = new JsonObject
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
            ["mode"] = "tiled",
        };
        if (direction is not null)
            obj["direction"] = direction;
        if (target is not null)
            obj["target_pane_id"] = target;
        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement OwnerCancelHide(
        string paneId,
        string attachClientId,
        long generation) =>
        JsonDocument.Parse(new JsonObject
        {
            ["pane_id"] = paneId,
            ["attach_client_id"] = attachClientId,
            ["overlay_generation"] = generation,
        }.ToJsonString()).RootElement.Clone();

    private static JsonElement HideOverlay(
        string paneId,
        string leaseId,
        string attachClientId,
        long generation) =>
        JsonDocument.Parse(new JsonObject
        {
            ["pane_id"] = paneId,
            ["lease_id"] = leaseId,
            ["attach_client_id"] = attachClientId,
            ["overlay_generation"] = generation,
        }.ToJsonString()).RootElement.Clone();

    private static List<string> EventTypes(CapturingSink sink) =>
        sink.Parsed
            .Where(ev => ev.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            .Select(ev => ev.GetProperty("params").GetProperty("type").GetString()!)
            .ToList();

    private sealed class OwnerPort(ControlPlaneService cp, IClientConnection connection) : IAttachCommandPort
    {
        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(parameters?.ToJsonString() ?? "{}");
            return await cp.DispatchAsync(method, doc.RootElement.Clone(), connection, ct);
        }
    }

    private sealed class ThrowOnResizeClaim(IAttachCommandPort inner) : IAttachCommandPort
    {
        public string? GrantedInput { get; private set; }

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.RuntimeLeaseClaim, StringComparison.Ordinal)
                && parameters is not null
                && parameters["scope"]?.GetValue<string>() == "resize")
            {
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "injected resize claim fail");
            }

            var result = await inner.CallAsync(method, parameters, ct);
            if (string.Equals(method, ProtocolMethods.RuntimeLeaseClaim, StringComparison.Ordinal)
                && parameters is not null
                && parameters["scope"]?.GetValue<string>() == "input"
                && result.TryGetProperty("lease_id", out var id))
            {
                GrantedInput = id.GetString();
            }

            return result;
        }
    }

    private sealed class RecordingResizeFactory : IPaneRuntimeFactory
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<(int Cols, int Rows)>> _resizes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<bool>> _cancelled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _occupants = new(StringComparer.Ordinal);
        public TaskCompletionSource ResizeEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _releaseResize;
        private int _blockCols;
        private int _blockRows;
        private int _block;

        public void BlockResize(int cols, int rows)
        {
            ResizeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseResize = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockCols = cols;
            _blockRows = rows;
            Volatile.Write(ref _block, 1);
        }

        public void ReleaseBlockedResize() => _releaseResize?.TrySetResult();

        public IReadOnlyList<(int Cols, int Rows)> ResizesFor(string paneId)
        {
            lock (_gate)
                return _resizes.TryGetValue(paneId, out var list) ? [.. list] : [];
        }

        public bool LastResizeWasCancelled(string paneId)
        {
            lock (_gate)
                return _cancelled.TryGetValue(paneId, out var list) && list.Count > 0 && list[^1];
        }

        public string Occupant(string paneId)
        {
            lock (_gate)
            {
                Assert.True(_occupants.TryGetValue(paneId, out var token));
                Assert.False(string.IsNullOrWhiteSpace(token));
                return token;
            }
        }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            if (options.Env is not null
                && options.Env.TryGetValue(PaneIdEnvironment.HypaPaneToken, out var token)
                && !string.IsNullOrWhiteSpace(token))
            {
                lock (_gate)
                    _occupants[options.Id.Value] = token;
            }

            return new RecordingResizeRuntime(this, options.Id);
        }

        internal void Note(string paneId, int cols, int rows, bool cancelled)
        {
            lock (_gate)
            {
                if (!_resizes.TryGetValue(paneId, out var list))
                {
                    list = [];
                    _resizes[paneId] = list;
                }

                list.Add((cols, rows));
                if (!_cancelled.TryGetValue(paneId, out var flags))
                {
                    flags = [];
                    _cancelled[paneId] = flags;
                }

                flags.Add(cancelled);
            }
        }

        private sealed class RecordingResizeRuntime(RecordingResizeFactory owner, PaneId id) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive { get; private set; } = true;
            public int? ExitCode { get; private set; }
            public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
            public Task StartAsync(CancellationToken ct)
            {
                IsAlive = true;
                return Task.CompletedTask;
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
                ValueTask.CompletedTask;

            public ValueTask WriteTextAsync(string text, CancellationToken ct) =>
                ValueTask.CompletedTask;

            public async ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
            {
                owner.Note(Id.Value, cols, rows, ct.IsCancellationRequested);
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref owner._block) == 1
                    && cols == owner._blockCols
                    && rows == owner._blockRows)
                {
                    owner.ResizeEntered.TrySetResult();
                    if (owner._releaseResize is { } hold)
                        await hold.Task.ConfigureAwait(false);
                }
            }

            public string ReadVisibleText() => "";
            public string ReadRecentText(int maxLines) => "";
            public string ReadRecentUnwrappedText(int maxLines) => "";
            public string ReadDetectionText() => "";
            public ValueTask DisposeAsync()
            {
                IsAlive = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ControllableOverlayStore(SqliteRuntimeSessionStore inner) : IRuntimeSessionStore
    {
        public SqliteRuntimeSessionStore Inner { get; } = inner;
        public bool FailSaves { get; set; }
        public bool FailNextSave { get; set; }
        public CancellationTokenSource? CancelOnSave { get; set; }
        public TaskCompletionSource SaveEntered { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HoldSave { get; set; }
        public TaskCompletionSource Entered => SaveEntered;

        public void BlockNextSave()
        {
            SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HoldSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FailNextSave = true;
        }

        public void ReleaseBlockedSave() => HoldSave?.TrySetResult();

        public Task<RuntimeResult<SessionState?>> TryLoadAsync(
            string sessionName,
            CancellationToken ct = default) =>
            Inner.TryLoadAsync(sessionName, ct);

        public async Task<RuntimeResult<RuntimeUnit>> SaveAsync(
            SessionState state,
            CancellationToken ct = default,
            bool removeMissingPanes = false)
        {
            CancelOnSave?.Cancel();
            SaveEntered.TrySetResult();
            if (HoldSave is not null)
                await HoldSave.Task.ConfigureAwait(false);
            if (FailNextSave || FailSaves)
            {
                FailNextSave = false;
                return RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected store failure"));
            }

            return await Inner.SaveAsync(state, ct, removeMissingPanes).ConfigureAwait(false);
        }

        public Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(
            PaneId paneId,
            CancellationToken ct = default) =>
            Inner.DeletePaneAsync(paneId, ct);

        public Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default) =>
            Inner.GetRuntimeSessionIdAsync(ct);
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_ov";
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
