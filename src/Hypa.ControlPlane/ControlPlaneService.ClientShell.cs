using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    private readonly object _shellPaneSizeGate = new();

    /// <summary>Tab resizes that a key interaction queues. They run one at a time.</summary>
    private readonly SerialWorkQueue _shellGeometryWork;
    private readonly Dictionary<string, Dictionary<string, (int Cols, int Rows)>> _publishedShellPaneSizes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// ClientShell mode and <c>shell_surface_active</c>.
    /// </summary>
    private bool IsActiveShellClient(IClientConnection? connection)
    {
        if (connection is null)
            return false;
        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        return snapshot is { SurfaceActive: true };
    }

    /// <summary>
    /// <c>shell_client_views_pane</c>.
    /// </summary>
    private bool ShellClientViewsPane(IClientConnection connection, string paneId)
    {
        var view = _attachClientViews.Get(connection.ConnectionId);
        if (view is null)
            return false;

        var tabId = view.FocusedTabId();
        if (string.IsNullOrWhiteSpace(tabId))
            return false;

        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null)
            return false;
        if (!string.Equals(pane.TabId.Value, tabId, StringComparison.Ordinal))
            return false;

        var tab = _state.GetTab(pane.TabId);
        if (tab is null)
            return false;
        if (tab.Zoomed)
        {
            var focused = tab.ZoomedPaneId ?? tab.FocusedPaneId;
            return focused is { } focusedPane
                && string.Equals(focusedPane.Value, paneId, StringComparison.Ordinal);
        }

        foreach (var id in tab.PaneIds)
        {
            if (string.Equals(id.Value, paneId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// admits keys with no exclusive pane lease.
    /// </summary>
    private bool TryAuthorizeClientShellPaneInput(string paneId, IClientConnection? connection) =>
        connection is not null
        && IsActiveShellClient(connection)
        && ShellClientViewsPane(connection, paneId);

    private string? FocusedTabIdForConnection(string connectionId) =>
        _attachClientViews.Get(connectionId)?.FocusedTabId();

    private int CountActiveShellClients()
    {
        var count = 0;
        foreach (var connectionId in _attachSurfaceInterest.ListConnectionIds())
        {
            if (_attachSurfaceInterest.IsSurfaceActive(connectionId))
                count++;
        }

        return count;
    }

    /// <summary>
    /// ClientShell interaction.
    /// </summary>
    private Task ClaimShellTabGeometryOnInteractAsync(
        IClientConnection? connection,
        CancellationToken ct) =>
        TryClaimShellTabGeometryOnInteract(connection, out var tabId)
            ? ApplyShellTabGeometryAsync(connection!.ConnectionId, tabId, ct)
            : Task.CompletedTask;

    /// <summary>
    /// ClientShell interaction on the key path. The claim is inline. The tab
    /// resize runs later as an internal work item, so the key does not wait
    /// for it. The work item checks again under the binding gate that this
    /// connection still controls the tab.
    /// </summary>
    private void QueueShellTabGeometryClaimOnInteract(IClientConnection? connection)
    {
        if (!TryClaimShellTabGeometryOnInteract(connection, out var tabId))
            return;
        var connectionId = connection!.ConnectionId;
        _shellGeometryWork.TryPost(ct => ApplyShellTabGeometryAsync(connectionId, tabId, ct));
    }

    private bool TryClaimShellTabGeometryOnInteract(IClientConnection? connection, out string tabId)
    {
        tabId = "";
        if (connection is null || !IsActiveShellClient(connection))
            return false;
        var focusedTab = FocusedTabIdForConnection(connection.ConnectionId);
        if (string.IsNullOrWhiteSpace(focusedTab))
            return false;
        if (!_tabGeometry.Claim(connection.ConnectionId, focusedTab))
            return false;
        tabId = focusedTab;
        return true;
    }

    /// <summary>Completes when no queued shell tab geometry work is left.</summary>
    internal Task ShellGeometryWorkIdleAsync() => _shellGeometryWork.WhenIdleAsync();

    /// <summary>
    /// The active request of <paramref name="connection"/> has applied its
    /// change. Keys that it fences can go before its reply.
    /// </summary>
    private static void MarkRequestApplied(IClientConnection? connection)
    {
        if (connection is ClientConnection client)
            client.MarkRequestApplied();
    }

    /// <summary>
    /// </summary>
    private Task ResizeShellTabIfControllerAsync(
        IClientConnection connection,
        CancellationToken ct,
        Action? onApplied = null)
    {
        if (!IsActiveShellClient(connection))
            return Task.CompletedTask;
        var tabId = FocusedTabIdForConnection(connection.ConnectionId);
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.CompletedTask;
        if (!_tabGeometry.IsController(connection.ConnectionId, tabId))
            return Task.CompletedTask;
        return ApplyShellTabGeometryAsync(connection.ConnectionId, tabId, ct, onApplied);
    }

    /// <summary>
    /// <c>client_views.rs:710-729</c> <c>claim_unowned_shell_tab_geometry</c>.
    /// </summary>
    private Task ClaimUnownedShellTabGeometryAsync(
        IClientConnection connection,
        CancellationToken ct)
    {
        if (!IsActiveShellClient(connection))
            return Task.CompletedTask;
        var tabId = FocusedTabIdForConnection(connection.ConnectionId);
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.CompletedTask;
        if (!_tabGeometry.ClaimUnowned(connection.ConnectionId, tabId)
            && !_tabGeometry.IsController(connection.ConnectionId, tabId))
        {
            return Task.CompletedTask;
        }

        return ApplyShellTabGeometryAsync(connection.ConnectionId, tabId, ct);
    }

    /// <summary>
    /// <c>apply_shell_tab_geometry_to_target</c>. Sole ClientShell client
    /// resizes every tab; otherwise only the owned tab.
    /// allocates a rectangle per pane. Hypa uses
    /// <see cref="LayoutRectAllocator"/>.
    /// </summary>
    private async Task ApplyShellTabGeometryAsync(
        string connectionId,
        string tabId,
        CancellationToken ct,
        Action? onApplied = null)
    {
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
            return;
        if (_attachSurfaceInterest.GetSnapshot(connectionId) is not { SurfaceActive: true })
            return;

        // The runtime resizes run outside the binding gate (ADR 0019). The
        // geometry gate keeps a queued apply from interleaving with a newer
        // apply. Lock order: geometry → pane admit → binding.
        var resized = new List<string>();
        await _paneGeometryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targets = new List<ShellPaneTarget>();
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                    return;

                // Read the owner and the size under the gate. A queued apply can
                // run after a newer apply or after this client left: it must not
                // write an old size or the size of a former controller.
                if (!_tabGeometry.IsController(connectionId, tabId))
                    return;
                var snapshot = _attachSurfaceInterest.GetSnapshot(connectionId);
                if (snapshot is null || !snapshot.SurfaceActive)
                    return;
                var cols = snapshot.Columns;
                var rows = snapshot.Rows;
                if (!VtFloorDefaults.TryValidateDimensions(cols, rows, out _))
                    return;

                IReadOnlyList<TabState> tabs;
                if (CountActiveShellClients() == 1)
                {
                    tabs = _state.ListTabs();
                }
                else
                {
                    var tab = _state.GetTab(new TabId(tabId));
                    if (tab is null)
                        return;
                    tabs = [tab];
                }

                foreach (var tab in tabs)
                {
                    foreach (var (paneId, paneCols, paneRows) in PaneSizesForShellTab(tab, cols, rows, connectionId))
                    {
                        targets.Add(new ShellPaneTarget(paneId, paneCols, paneRows));
                    }
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            foreach (var target in targets)
            {
                if (await ResizeShellPaneAsync(target, ct).ConfigureAwait(false))
                    resized.Add(target.PaneId);
            }
        }
        finally
        {
            _paneGeometryGate.Release();
        }

        // Each pane has its new size. The snapshots below do not change it.
        onApplied?.Invoke();
        foreach (var paneId in resized)
            await ForcePaneSnapshotAsync(paneId, ct).ConfigureAwait(false);
    }

    /// <summary>One pane of a shell tab apply, with the size that the apply read.</summary>
    private sealed record ShellPaneTarget(string PaneId, int Cols, int Rows);

    /// <summary>
    /// One pane of a shell tab apply. The caller holds the geometry gate.
    /// The apply uses the sizes that it read under the binding gate. A layout,
    /// placement or overlay change that runs during the apply thus has the
    /// result that it has after the apply (ADR 0019, decision 9). The client
    /// sends new pane sizes after that change. Returns false when the pane
    /// closed or has no runtime.
    /// </summary>
    private async Task<bool> ResizeShellPaneAsync(ShellPaneTarget target, CancellationToken ct)
    {
        var paneId = target.PaneId;
        SemaphoreSlim admit;
        try
        {
            admit = await AcquirePaneAdmitAsync(paneId, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
        {
            return false;
        }

        try
        {
            IPaneRuntime? runtime;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state.GetPane(new PaneId(paneId)) is null
                    || !TryGetRuntime(paneId, out runtime)
                    || runtime is null)
                {
                    return false;
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            return await ResizeRuntimeThenCommitAsync(paneId, runtime, target.Cols, target.Rows, commit: null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            SafeReleaseAdmit(admit);
        }
    }

    /// <summary>
    /// <c>src/ui/tab_surface.rs:94-116</c>: host area, then per-pane rects.
    /// to the inner rect. Hypa keeps that inner size on the client
    /// <c>pane.resize</c> call. Prefer the published size when present.
    /// </summary>
    private IEnumerable<(string PaneId, int Cols, int Rows)> PaneSizesForShellTab(
        TabState tab,
        int hostCols,
        int hostRows,
        string connectionId)
    {
        var allocated = LayoutRectAllocator.Allocate(
            tab.LayoutRoot,
            hostCols,
            hostRows,
            tab.Zoomed,
            tab.ZoomedPaneId?.Value);
        if (allocated.Panes.Count > 0)
        {
            foreach (var pane in allocated.Panes)
            {
                if (TryGetPublishedShellPaneSize(connectionId, pane.PaneId, out var publishedCols, out var publishedRows))
                {
                    yield return (pane.PaneId, publishedCols, publishedRows);
                    continue;
                }

                if (!VtFloorDefaults.TryValidateDimensions(pane.Rect.Cols, pane.Rect.Rows, out _))
                    continue;
                yield return (pane.PaneId, pane.Rect.Cols, pane.Rect.Rows);
            }

            yield break;
        }

        if (tab.PaneIds.Count != 1)
            yield break;
        var only = tab.PaneIds[0].Value;
        if (TryGetPublishedShellPaneSize(connectionId, only, out var onlyCols, out var onlyRows))
        {
            yield return (only, onlyCols, onlyRows);
            yield break;
        }

        yield return (only, hostCols, hostRows);
    }

    private void RememberPublishedShellPaneSize(string connectionId, string paneId, int cols, int rows)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_shellPaneSizeGate)
        {
            if (!_publishedShellPaneSizes.TryGetValue(connectionId, out var panes))
            {
                panes = new Dictionary<string, (int Cols, int Rows)>(StringComparer.Ordinal);
                _publishedShellPaneSizes[connectionId] = panes;
            }

            panes[paneId] = (cols, rows);
        }
    }

    private bool TryGetPublishedShellPaneSize(
        string connectionId,
        string paneId,
        out int cols,
        out int rows)
    {
        lock (_shellPaneSizeGate)
        {
            if (_publishedShellPaneSizes.TryGetValue(connectionId, out var panes)
                && panes.TryGetValue(paneId, out var size))
            {
                cols = size.Cols;
                rows = size.Rows;
                return true;
            }
        }

        cols = 0;
        rows = 0;
        return false;
    }

    private void ForgetPublishedShellPaneSizes(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;
        lock (_shellPaneSizeGate)
            _publishedShellPaneSizes.Remove(connectionId);
    }

    private async Task ForcePaneSnapshotAsync(string paneId, CancellationToken ct)
    {
        var paneGate = GetPaneEmitGate(paneId);
        await paneGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ForgetPostedFull(paneId);
            _renderCoalescer.Cancel(paneId);
            if (!TryPostVtSnapshotCore(paneId, attachPath: false, force: true))
                FailClosedSnapshotPaintOrByteFallback(paneId, () => WriteAttachByteFallback(paneId));
        }
        finally
        {
            paneGate.Release();
        }
    }

    /// <summary>
    /// <c>reapply_controlled_shell_tab_geometry</c> after a viewer leaves.
    /// </summary>
    private void ReapplyShellTabGeometryAfterDisconnect(string connectionId)
    {
        var released = _tabGeometry.Release(connectionId);
        if (released.Count == 0 && CountActiveShellClients() != 1)
            return;

        var tabs = released.Count > 0
            ? released
            : CollectViewedTabIds();
        foreach (var tabId in tabs)
        {
            var remaining = RemainingShellViewer(tabId);
            if (remaining is null)
                continue;
            if (!_tabGeometry.ClaimUnowned(remaining, tabId)
                && !_tabGeometry.IsController(remaining, tabId))
            {
                continue;
            }

            ApplyShellTabGeometryAsync(remaining, tabId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private IReadOnlyList<string> CollectViewedTabIds()
    {
        var tabs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var connectionId in _attachClientViews.ListConnectionIds())
        {
            if (!_attachSurfaceInterest.IsSurfaceActive(connectionId))
                continue;
            var tabId = FocusedTabIdForConnection(connectionId);
            if (!string.IsNullOrWhiteSpace(tabId))
                tabs.Add(tabId);
        }

        return [.. tabs];
    }

    private string? RemainingShellViewer(string tabId)
    {
        foreach (var connectionId in _attachClientViews.ListConnectionIds())
        {
            if (!_attachSurfaceInterest.IsSurfaceActive(connectionId))
                continue;
            if (string.Equals(FocusedTabIdForConnection(connectionId), tabId, StringComparison.Ordinal))
                return connectionId;
        }

        return null;
    }

    internal AttachTabGeometryPublication TabGeometry => _tabGeometry;

    internal static JsonObject PaneResizeResult(
        string paneId,
        int cols,
        int rows,
        string? geometryOwner = null)
    {
        var result = new JsonObject
        {
            ["ok"] = true,
            ["pane_id"] = paneId,
            ["cols"] = cols,
            ["rows"] = rows,
        };
        if (geometryOwner is not null)
            result["geometry_owner"] = geometryOwner;
        return result;
    }

    private bool CallerOwnsOverlay(IClientConnection connection, PaneOverlayOwner owner)
    {
        if (string.Equals(owner.AttachClientId, connection.ConnectionId, StringComparison.Ordinal))
            return true;
        var clientId = _attachClientViews.Get(connection.ConnectionId)?.ClientId;
        return !string.IsNullOrWhiteSpace(clientId)
            && string.Equals(owner.AttachClientId, clientId, StringComparison.Ordinal);
    }
}
