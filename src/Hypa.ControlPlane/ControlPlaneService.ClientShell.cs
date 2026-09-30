using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    private readonly object _shellPaneSizeGate = new();
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
        CancellationToken ct)
    {
        if (connection is null || !IsActiveShellClient(connection))
            return Task.CompletedTask;
        var tabId = FocusedTabIdForConnection(connection.ConnectionId);
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.CompletedTask;
        if (!_tabGeometry.Claim(connection.ConnectionId, tabId))
            return Task.CompletedTask;
        return ApplyShellTabGeometryAsync(connection.ConnectionId, tabId, ct);
    }

    /// <summary>
    /// </summary>
    private Task ResizeShellTabIfControllerAsync(
        IClientConnection connection,
        CancellationToken ct)
    {
        if (!IsActiveShellClient(connection))
            return Task.CompletedTask;
        var tabId = FocusedTabIdForConnection(connection.ConnectionId);
        if (string.IsNullOrWhiteSpace(tabId))
            return Task.CompletedTask;
        if (!_tabGeometry.IsController(connection.ConnectionId, tabId))
            return Task.CompletedTask;
        return ApplyShellTabGeometryAsync(connection.ConnectionId, tabId, ct);
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
        CancellationToken ct)
    {
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
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

        var resized = new List<string>();
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                return;
            foreach (var tab in tabs)
            {
                foreach (var (paneId, paneCols, paneRows) in PaneSizesForShellTab(
                             tab, cols, rows, connectionId))
                {
                    if (!TryGetRuntime(paneId, out var runtime) || runtime is null)
                        continue;
                    await runtime.ResizeAsync(paneCols, paneRows, ct).ConfigureAwait(false);
                    _state.UpdatePane(
                        new PaneId(paneId),
                        pane => pane with { Cols = paneCols, Rows = paneRows });
                    resized.Add(paneId);
                }
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        foreach (var paneId in resized)
            await ForcePaneSnapshotAsync(paneId, ct).ConfigureAwait(false);
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
