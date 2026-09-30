using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.App;

internal enum TabPlacement
{
    Top,
    Side,
}

internal sealed class WorkspaceModel
{
    public required string WorkspaceId { get; init; }
    public required string Label { get; set; }
    public bool IsFocused { get; set; }
}

internal sealed class TabModel
{
    public required string TabId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Label { get; set; }
    public bool IsFocused { get; set; }
}

internal sealed class AppSession : IAsyncDisposable
{
    private readonly ControlPlaneClient _client;
    private readonly CellGrid _grid = new();
    private string _paneId = "";
    private string _focusedTabId = "";
    private string _focusedWorkspaceId = "";
    private string _subscriptionId = "";
    private string _inputLease = "";
    private string _resizeLease = "";
    private int _cols = 80;
    private int _rows = 24;
    private long _lastLeaseRenewMs;
    private int _scrollOffset;
    private int _scrollMax;
    private string _cwdLabel = "~";
    private string _workspaceLabel = "Space";

    public AppSession(ControlPlaneClient client) => _client = client;

    public CellGrid Grid => _grid;
    public string PaneId => _paneId;
    public string FocusedTabId => _focusedTabId;
    public string FocusedWorkspaceId => _focusedWorkspaceId;
    public string WorkspaceLabel => _workspaceLabel;
    public string CwdLabel => _cwdLabel;
    public int ScrollOffset => _scrollOffset;
    public int ScrollMax => _scrollMax;
    public ObservableCollection<WorkspaceModel> Workspaces { get; } = [];
    public ObservableCollection<TabModel> Tabs { get; } = [];
    public TabPlacement TabPlacement { get; set; } = TabPlacement.Side;
    public event Action? Changed;

    public async Task AttachAsync(CancellationToken ct)
    {
        var ping = await _client.CallAsync(ProtocolMethods.Ping, ct: ct).ConfigureAwait(false);
        if (!ping.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("ping failed");

        await EnsurePaneAsync(ct).ConfigureAwait(false);
        await RefreshLayoutAsync(ct).ConfigureAwait(false);
        Console.Error.WriteLine($"hypa-app: pane_id={_paneId}");

        var subParams = new JsonObject
        {
            ["from_seq"] = 0,
            ["types"] = new JsonArray("control", "lifecycle", "render"),
            ["replay_budget"] = 0,
            ["live"] = true,
        };
        var sub = await _client.CallAsync(ProtocolMethods.EventsSubscribe, subParams, ct)
            .ConfigureAwait(false);
        _subscriptionId = sub.GetProperty("subscription_id").GetString()
            ?? throw new InvalidOperationException("missing subscription_id");

        await ReclaimLeasesAsync(ct).ConfigureAwait(false);
        await ControlAndObserveAsync(ct).ConfigureAwait(false);
        await RefreshScrollAsync(ct).ConfigureAwait(false);
        _lastLeaseRenewMs = Environment.TickCount64;
        RaiseChanged();
    }

    public async Task SetSizeAsync(int cols, int rows, CancellationToken ct)
    {
        _cols = Math.Max(20, cols);
        _rows = Math.Max(8, rows);
        if (string.IsNullOrEmpty(_paneId) || string.IsNullOrEmpty(_resizeLease))
            return;
        var parameters = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["cols"] = _cols,
            ["rows"] = _rows,
            ["lease_id"] = _resizeLease,
        };
        try
        {
            await _client.CallAsync(ProtocolMethods.PaneResize, parameters, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: resize failed: {ex.Message}");
        }
    }

    public async Task SendKeysAsync(byte[] data, CancellationToken ct)
    {
        if (data.Length == 0 || string.IsNullOrEmpty(_inputLease) || string.IsNullOrEmpty(_paneId))
            return;
        var parameters = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["lease_id"] = _inputLease,
            ["encoding"] = "base64",
            ["data"] = Convert.ToBase64String(data),
        };
        await _client.NotifyAsync(ProtocolMethods.PaneSendKeys, parameters, ct).ConfigureAwait(false);
    }

    public async Task FocusWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workspaceId)
            || string.Equals(workspaceId, _focusedWorkspaceId, StringComparison.Ordinal))
        {
            return;
        }

        await _client.CallAsync(
                ProtocolMethods.WorkspaceFocus,
                new JsonObject { ["workspace_id"] = workspaceId },
                ct)
            .ConfigureAwait(false);
        await AfterFocusChangeAsync(ct).ConfigureAwait(false);
    }

    public async Task FocusTabAsync(string tabId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tabId))
            return;
        await _client.CallAsync(
                ProtocolMethods.TabFocus,
                new JsonObject { ["tab_id"] = tabId },
                ct)
            .ConfigureAwait(false);
        await AfterFocusChangeAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateWorkspaceAsync(CancellationToken ct, string? label = null)
    {
        var parameters = new JsonObject
        {
            ["create_pane"] = true,
            ["cwd"] = Environment.CurrentDirectory,
        };
        if (!string.IsNullOrWhiteSpace(label))
            parameters["label"] = label;
        await _client.CallAsync(ProtocolMethods.WorkspaceCreate, parameters, ct).ConfigureAwait(false);
        await AfterFocusChangeAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateTabAsync(CancellationToken ct, string? label = null)
    {
        var workspaceId = _focusedWorkspaceId;
        if (string.IsNullOrEmpty(workspaceId))
            workspaceId = await FocusedWorkspaceIdAsync(ct).ConfigureAwait(false) ?? "";

        if (string.IsNullOrEmpty(workspaceId))
        {
            await CreateWorkspaceAsync(ct, label).ConfigureAwait(false);
            return;
        }

        var parameters = new JsonObject
        {
            ["workspace_id"] = workspaceId,
            ["create_pane"] = true,
            ["focus"] = true,
        };
        if (!string.IsNullOrWhiteSpace(label))
            parameters["label"] = label;

        await _client.CallAsync(ProtocolMethods.TabCreate, parameters, ct).ConfigureAwait(false);
        await AfterFocusChangeAsync(ct).ConfigureAwait(false);
    }

    public async Task CloseTabAsync(string tabId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tabId))
            return;
        try
        {
            await _client.CallAsync(
                    ProtocolMethods.TabClose,
                    new JsonObject { ["tab_id"] = tabId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: close tab failed: {ex.Message}");
            return;
        }

        await AfterFocusChangeAsync(ct).ConfigureAwait(false);
    }

    public async Task ScrollToAsync(int offset, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_paneId))
            return;
        offset = Math.Clamp(offset, 0, Math.Max(0, _scrollMax));
        var parameters = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["offset"] = offset,
        };
        var reply = await _client.CallAsync(ProtocolMethods.PaneScroll, parameters, ct)
            .ConfigureAwait(false);
        ApplyScrollReply(reply);
        RaiseChanged();
    }

    public async Task PumpAsync(CancellationToken ct)
    {
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(8);
            JsonElement ev;
            try
            {
                ev = await _client.ReadEventAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }

            if (!ev.TryGetProperty("params", out var parms) || parms.ValueKind != JsonValueKind.Object)
                continue;
            if (!parms.TryGetProperty("type", out var type) || type.GetString() is not { } typeName)
                continue;
            if (!parms.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                continue;

            if (string.Equals(typeName, "terminal.render", StringComparison.Ordinal))
            {
                if (payload.TryGetProperty("pane_id", out var pid)
                    && pid.ValueKind == JsonValueKind.String
                    && pid.GetString() is { } pane
                    && !string.Equals(pane, _paneId, StringComparison.Ordinal))
                {
                    continue;
                }

                _grid.ApplyPayload(payload);
                RaiseChanged();
            }
            else if (string.Equals(typeName, "pane.scroll_changed", StringComparison.Ordinal))
            {
                ApplyScrollPayload(payload);
                RaiseChanged();
            }
            else if (typeName is "tab.lifecycle" or "workspace.lifecycle" or "layout.updated" or "pane.lifecycle")
            {
                var previousPane = _paneId;
                await RefreshLayoutAsync(ct).ConfigureAwait(false);
                await EnsureFocusedPaneAsync(ct).ConfigureAwait(false);
                if (!string.Equals(previousPane, _paneId, StringComparison.Ordinal))
                {
                    await ReclaimLeasesAsync(ct).ConfigureAwait(false);
                    await ControlAndObserveAsync(ct).ConfigureAwait(false);
                    await RefreshScrollAsync(ct).ConfigureAwait(false);
                }

                RaiseChanged();
            }
        }

        if (Environment.TickCount64 - _lastLeaseRenewMs > 15_000)
        {
            await RenewLeasesAsync(ct).ConfigureAwait(false);
            _lastLeaseRenewMs = Environment.TickCount64;
        }
    }

    private async Task AfterFocusChangeAsync(CancellationToken ct)
    {
        await RefreshLayoutAsync(ct).ConfigureAwait(false);
        await EnsureFocusedPaneAsync(ct).ConfigureAwait(false);
        try
        {
            await ReclaimLeasesAsync(ct).ConfigureAwait(false);
            await ControlAndObserveAsync(ct).ConfigureAwait(false);
            await RefreshScrollAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: rebind pane failed: {ex.Message}");
            // Layout chrome still updated; try once more for leases.
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                await ReclaimLeasesAsync(ct).ConfigureAwait(false);
                await ControlAndObserveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                Console.Error.WriteLine($"hypa-app: rebind retry failed: {retryEx.Message}");
            }
        }

        RaiseChanged();
    }

    private async Task ReclaimLeasesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_paneId))
            return;
        _inputLease = await ClaimLeaseAsync("input", ct).ConfigureAwait(false);
        _resizeLease = await ClaimLeaseAsync("resize", ct).ConfigureAwait(false);
        _lastLeaseRenewMs = Environment.TickCount64;
    }

    private async Task ControlAndObserveAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_paneId) || string.IsNullOrEmpty(_subscriptionId))
            return;
        var ctrl = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["lease_id"] = _inputLease,
            ["subscription_id"] = _subscriptionId,
            ["replace"] = true,
        };
        await _client.CallAsync(ProtocolMethods.TerminalControl, ctrl, ct).ConfigureAwait(false);
        var obs = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["subscription_id"] = _subscriptionId,
            ["replace"] = true,
        };
        await _client.CallAsync(ProtocolMethods.TerminalObserve, obs, ct).ConfigureAwait(false);
    }

    private async Task EnsurePaneAsync(CancellationToken ct)
    {
        var snap = await _client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
        ApplySnapshotMeta(snap);
        if (TryFocusedPane(snap, out _paneId) || TryFirstAlivePane(snap, out _paneId))
            return;

        var wsId = _focusedWorkspaceId;
        if (string.IsNullOrEmpty(wsId))
            wsId = await FocusedWorkspaceIdAsync(ct).ConfigureAwait(false) ?? "";

        if (!string.IsNullOrEmpty(wsId))
        {
            try
            {
                var created = await _client.CallAsync(
                        ProtocolMethods.PaneCreate,
                        new JsonObject
                        {
                            ["workspace_id"] = wsId,
                            ["command"] = "/bin/bash",
                        },
                        ct)
                    .ConfigureAwait(false);
                if (created.TryGetProperty("pane_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                    _paneId = pid.GetString() ?? "";
            }
            catch
            {
                // Focused tab may already have a pane; prefer tab.create path below.
                await _client.CallAsync(
                        ProtocolMethods.TabCreate,
                        new JsonObject
                        {
                            ["workspace_id"] = wsId,
                            ["create_pane"] = true,
                            ["focus"] = true,
                        },
                        ct)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await _client.CallAsync(
                    ProtocolMethods.WorkspaceCreate,
                    new JsonObject
                    {
                        ["create_pane"] = true,
                        ["cwd"] = Environment.CurrentDirectory,
                    },
                    ct)
                .ConfigureAwait(false);
        }

        snap = await _client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct).ConfigureAwait(false);
        ApplySnapshotMeta(snap);
        if (!TryFocusedPane(snap, out _paneId) && !TryFirstAlivePane(snap, out _paneId))
            throw new InvalidOperationException("no alive pane after create");
    }

    private async Task EnsureFocusedPaneAsync(CancellationToken ct)
    {
        var snap = await _client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
        ApplySnapshotMeta(snap);
        if (TryFocusedPane(snap, out var pane) || TryFirstAlivePaneInFocusedTab(snap, out pane)
            || TryFirstAlivePane(snap, out pane))
        {
            _paneId = pane;
        }
    }

    private async Task RefreshLayoutAsync(CancellationToken ct)
    {
        var snap = await _client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
        ApplySnapshotMeta(snap);

        Workspaces.Clear();
        if (snap.TryGetProperty("workspaces", out var workspaces) && workspaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ws in workspaces.EnumerateArray())
            {
                if (!ws.TryGetProperty("workspace_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    continue;
                var id = idEl.GetString() ?? "";
                var label = "Space";
                if (ws.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String
                    && labelEl.GetString() is { Length: > 0 } s)
                {
                    label = s;
                }
                else
                {
                    label = ShortLabel(id, "Space");
                }

                var focused = string.Equals(id, _focusedWorkspaceId, StringComparison.Ordinal);
                if (focused)
                    _workspaceLabel = label;
                Workspaces.Add(new WorkspaceModel
                {
                    WorkspaceId = id,
                    Label = label,
                    IsFocused = focused,
                });
            }
        }

        Tabs.Clear();
        if (snap.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (!tab.TryGetProperty("tab_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    continue;
                var id = idEl.GetString() ?? "";
                var workspaceId = "";
                if (tab.TryGetProperty("workspace_id", out var wsEl) && wsEl.ValueKind == JsonValueKind.String)
                    workspaceId = wsEl.GetString() ?? "";

                // Scope tabs to the focused workspace (attach behavior).
                if (!string.IsNullOrEmpty(_focusedWorkspaceId)
                    && !string.Equals(workspaceId, _focusedWorkspaceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var label = "Tab";
                if (tab.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String
                    && labelEl.GetString() is { Length: > 0 } s)
                {
                    label = s;
                }
                else
                {
                    label = ShortLabel(id, "Tab");
                }

                Tabs.Add(new TabModel
                {
                    TabId = id,
                    WorkspaceId = workspaceId,
                    Label = label,
                    IsFocused = string.Equals(id, _focusedTabId, StringComparison.Ordinal),
                });
            }
        }

        if (TryReadPaneCwd(snap, _paneId, out var cwd))
            _cwdLabel = ShortenPath(cwd);
    }

    private async Task RefreshScrollAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_paneId))
            return;
        try
        {
            var reply = await _client.CallAsync(
                    ProtocolMethods.PaneScroll,
                    new JsonObject { ["pane_id"] = _paneId, ["offset"] = _scrollOffset },
                    ct)
                .ConfigureAwait(false);
            ApplyScrollReply(reply);
        }
        catch
        {
            // Pane may not expose scroll yet.
        }
    }

    private void ApplySnapshotMeta(JsonElement snap)
    {
        if (snap.TryGetProperty("focused_workspace_id", out var ws) && ws.ValueKind == JsonValueKind.String)
            _focusedWorkspaceId = ws.GetString() ?? _focusedWorkspaceId;
        if (snap.TryGetProperty("focused_tab_id", out var tab) && tab.ValueKind == JsonValueKind.String)
            _focusedTabId = tab.GetString() ?? _focusedTabId;
    }

    private void ApplyScrollReply(JsonElement reply)
    {
        if (reply.TryGetProperty("offset", out var o) && o.TryGetInt32(out var offset))
            _scrollOffset = Math.Max(0, offset);
        if (reply.TryGetProperty("max_offset", out var m) && m.TryGetInt32(out var max))
            _scrollMax = Math.Max(0, max);
    }

    private void ApplyScrollPayload(JsonElement payload)
    {
        if (payload.TryGetProperty("pane_id", out var pid)
            && pid.ValueKind == JsonValueKind.String
            && pid.GetString() is { } pane
            && !string.Equals(pane, _paneId, StringComparison.Ordinal))
        {
            return;
        }

        ApplyScrollReply(payload);
    }

    private async Task<string?> FocusedWorkspaceIdAsync(CancellationToken ct)
    {
        var snap = await _client.CallAsync(ProtocolMethods.SessionSnapshot, ct: ct)
            .ConfigureAwait(false);
        ApplySnapshotMeta(snap);
        if (!string.IsNullOrEmpty(_focusedWorkspaceId))
            return _focusedWorkspaceId;
        if (snap.TryGetProperty("workspaces", out var ws)
            && ws.ValueKind == JsonValueKind.Array
            && ws.GetArrayLength() > 0
            && ws[0].TryGetProperty("workspace_id", out var w)
            && w.ValueKind == JsonValueKind.String)
        {
            return w.GetString();
        }

        return null;
    }

    private static bool TryFirstAlivePane(JsonElement snap, out string paneId)
    {
        paneId = "";
        if (!snap.TryGetProperty("panes", out var panes) || panes.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var pane in panes.EnumerateArray())
        {
            if (!pane.TryGetProperty("alive", out var alive) || alive.ValueKind != JsonValueKind.True)
                continue;
            if (pane.TryGetProperty("pane_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                paneId = id.GetString() ?? "";
                return paneId.Length > 0;
            }
        }

        return false;
    }

    private bool TryFirstAlivePaneInFocusedTab(JsonElement snap, out string paneId)
    {
        paneId = "";
        if (string.IsNullOrEmpty(_focusedTabId)
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (!pane.TryGetProperty("tab_id", out var tab) || tab.GetString() != _focusedTabId)
                continue;
            if (!pane.TryGetProperty("alive", out var alive) || alive.ValueKind != JsonValueKind.True)
                continue;
            if (pane.TryGetProperty("pane_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                paneId = id.GetString() ?? "";
                return paneId.Length > 0;
            }
        }

        return false;
    }

    private bool TryFocusedPane(JsonElement snap, out string paneId)
    {
        paneId = "";
        if (string.IsNullOrEmpty(_focusedTabId)
            || !snap.TryGetProperty("tabs", out var tabs)
            || tabs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tab in tabs.EnumerateArray())
        {
            if (!tab.TryGetProperty("tab_id", out var id) || id.GetString() != _focusedTabId)
                continue;
            if (tab.TryGetProperty("focused_pane_id", out var pane)
                && pane.ValueKind == JsonValueKind.String
                && pane.GetString() is { Length: > 0 } p)
            {
                paneId = p;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadPaneCwd(JsonElement snap, string paneId, out string cwd)
    {
        cwd = "";
        if (string.IsNullOrEmpty(paneId)
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pane in panes.EnumerateArray())
        {
            if (!pane.TryGetProperty("pane_id", out var id) || id.GetString() != paneId)
                continue;
            if (pane.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
            {
                cwd = cwdEl.GetString() ?? "";
                return cwd.Length > 0;
            }
        }

        return false;
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
            return "~" + path[home.Length..];
        return path;
    }

    private static string ShortLabel(string id, string fallback)
    {
        if (string.IsNullOrEmpty(id))
            return fallback;
        return id.Length <= 8 ? id : id[..8];
    }

    private async Task<string> ClaimLeaseAsync(string scope, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["pane_id"] = _paneId,
            ["scope"] = scope,
            ["takeover"] = true,
            ["ttl_ms"] = 30000,
        };
        var result = await _client.CallAsync(ProtocolMethods.RuntimeLeaseClaim, parameters, ct)
            .ConfigureAwait(false);
        if (!result.TryGetProperty("outcome", out var outcome)
            || outcome.GetString() is not ("granted" or "already_held")
            || !result.TryGetProperty("lease_id", out var lease)
            || lease.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(lease.GetString()))
        {
            throw new InvalidOperationException($"lease {scope} not granted ({outcome.GetString() ?? "missing"})");
        }

        return lease.GetString()!;
    }

    private async Task RenewLeasesAsync(CancellationToken ct)
    {
        foreach (var lease in new[] { _inputLease, _resizeLease })
        {
            if (string.IsNullOrEmpty(lease))
                continue;
            try
            {
                await _client.CallAsync(
                        ProtocolMethods.RuntimeLeaseRenew,
                        new JsonObject { ["lease_id"] = lease, ["ttl_ms"] = 30000 },
                        ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
