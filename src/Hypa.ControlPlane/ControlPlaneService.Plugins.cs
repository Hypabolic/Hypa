using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal IPluginHost Plugins => _plugins;

    internal bool IsLivePluginPeer(int peerPid) => _plugins.IsLivePluginProcess(peerPid);

    internal Task<JsonElement> HandlePluginLinkAsync(PluginLinkParams p, CancellationToken ct)
    {
        _ = ct;
        var path = RequireField(p.Path, "path");
        var result = _plugins.Link(path, p.Enabled ?? true);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return Task.FromResult(OkTyped(ToLink(result.Value), ProtocolJsonContext.Default.PluginLinkResult));
    }

    internal Task<JsonElement> HandlePluginListAsync(PluginListParams p, CancellationToken ct)
    {
        _ = ct;
        var result = _plugins.List(p.PluginId);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return Task.FromResult(OkTyped(
            new PluginListResult { Plugins = result.Value.Select(ToDto).ToArray() },
            ProtocolJsonContext.Default.PluginListResult));
    }

    internal async Task<JsonElement> HandlePluginUnlinkAsync(PluginUnlinkParams p, CancellationToken ct)
    {
        var pluginId = RequireField(p.PluginId, "plugin_id");
        await CleanupPluginSessionAsync(
                pluginId,
                _plugins.OwnedPaneIds(pluginId),
                _plugins.OwnsPopup(pluginId),
                ct)
            .ConfigureAwait(false);
        var result = _plugins.Unlink(pluginId);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return OkTyped(
            new PluginUnlinkResultDto { PluginId = result.Value.PluginId, Removed = result.Value.Removed },
            ProtocolJsonContext.Default.PluginUnlinkResultDto);
    }

    internal Task<JsonElement> HandlePluginEnableAsync(PluginSetEnabledParams p, CancellationToken ct) =>
        SetPluginEnabledAsync(p, enabled: true, ct);

    internal Task<JsonElement> HandlePluginDisableAsync(PluginSetEnabledParams p, CancellationToken ct) =>
        SetPluginEnabledAsync(p, enabled: false, ct);

    internal Task<JsonElement> HandlePluginActionListAsync(PluginActionListParams p, CancellationToken ct)
    {
        _ = ct;
        var result = _plugins.ListActions(p.PluginId);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return Task.FromResult(OkTyped(
            new PluginActionListResult { Actions = result.Value.Select(ToActionDto).ToArray() },
            ProtocolJsonContext.Default.PluginActionListResult));
    }

    internal Task<JsonElement> HandlePluginActionInvokeAsync(PluginActionInvokeParams p, CancellationToken ct)
    {
        _ = ct;
        var actionId = RequireField(p.ActionId, "action_id");
        EnsureActionRevision(p);
        var context = FromDto(p.Context);
        var result = _plugins.InvokeAction(actionId, p.PluginId, context);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return Task.FromResult(OkTyped(ToInvoke(result.Value), ProtocolJsonContext.Default.PluginActionInvokeResultDto));
    }

    internal Task<JsonElement> HandlePluginLogListAsync(PluginLogListParams p, CancellationToken ct)
    {
        _ = ct;
        var result = _plugins.ListLogs(p.PluginId, p.Limit);
        if (!result.IsOk)
            throw PluginFault(result.Error);
        return Task.FromResult(OkTyped(
            new PluginLogListResult { Logs = result.Value.Select(ToLogDto).ToArray() },
            ProtocolJsonContext.Default.PluginLogListResult));
    }

    internal async Task<JsonElement> HandlePluginPaneOpenAsync(
        PluginPaneOpenParams p, IClientConnection? connection, CancellationToken ct)
    {
        var pluginId = RequireField(p.PluginId, "plugin_id");
        var entrypoint = RequireField(p.Entrypoint, "entrypoint");
        var context = CurrentPluginContext("plugin-pane");
        var plan = _plugins.PlanPaneOpen(pluginId, entrypoint, p.Placement, context, p.Env);
        if (!plan.IsOk)
            throw PluginFault(plan.Error);

        var deliveryTarget = p.TargetPaneId ?? CurrentPaneId();
        var opened = await OpenPlannedPluginPaneAsync(plan.Value, p, connection, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(deliveryTarget))
            _plugins.NoteDeliveryTarget(pluginId, deliveryTarget);
        return OkTyped(opened, ProtocolJsonContext.Default.PluginPaneOpenedResult);
    }

    internal async Task<JsonElement> HandlePluginPaneFocusAsync(PluginPaneFocusParams p, CancellationToken ct)
    {
        var paneId = RequireField(p.PaneId, "pane_id");
        if (!OwnsPane(paneId))
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "plugin pane not found");

        await DispatchAsync(
                ProtocolMethods.PaneFocus,
                JsonFrom(new JsonObject { ["pane_id"] = paneId }),
                connection: null,
                ct)
            .ConfigureAwait(false);
        var pane = RequirePane(paneId);
        return OkTyped(
            new PluginPaneFocusedResult
            {
                PluginPane = new PluginPaneInfoDto
                {
                    PluginId = OwnerPluginId(paneId),
                    PaneId = paneId,
                    Placement = PanePlacementWire.ToWire(pane.Placement),
                },
            },
            ProtocolJsonContext.Default.PluginPaneFocusedResult);
    }

    internal async Task<JsonElement> HandlePluginPaneCloseAsync(PluginPaneCloseParams p, CancellationToken ct)
    {
        var paneId = RequireField(p.PaneId, "pane_id");
        if (!OwnsPane(paneId))
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "plugin pane not found");
        await PaneCloseAsync(new PaneCloseParams { PaneId = paneId }, null, ct).ConfigureAwait(false);
        _plugins.ForgetOwnedPane(paneId);
        return OkTyped(
            new PluginPaneClosedResult { PaneId = paneId },
            ProtocolJsonContext.Default.PluginPaneClosedResult);
    }

    internal async Task<JsonElement> HandlePluginPaneSendTextAsync(
        PluginPaneSendTextParams p,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PluginPaneSendText);
        var id = RequireField(p.PaneId, "pane_id");
        var text = EmptyToNull(p.Text)
            ?? throw new ControlPlaneException(-32602, "text is required");
        var pluginId = _plugins.ResolveSendTextPluginId(id);
        if (pluginId is null || !_plugins.CanDeliverText(pluginId, id))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.CapabilityMissing,
                ProtocolErrors.CapabilityMissing);
        }

        // Hypa pane.send_text appends one newline when the text has no trailing \n or \r.
        if (!text.EndsWith('\n') && !text.EndsWith('\r'))
            text += "\n";

        EnsureWorkGenerationAllowsInput(id);
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PluginPaneSendText);
            EnsureWorkGenerationAllowsInput(id);
            var runtime = GetRuntime(id);
            await runtime.WriteTextAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        _plugins.RecordPaneTextDelivery(pluginId, id);
        return OkTyped(
            new PluginPaneSendTextResult { Ok = true, PaneId = id },
            ProtocolJsonContext.Default.PluginPaneSendTextResult);
    }

    /// <summary>
    /// One-shot. Does not wait. Must not block the mux.
    /// </summary>
    public Task FlushPluginStartupHooksAsync(CancellationToken ct)
    {
        _ = ct;
        if (IsShuttingDown)
            return Task.CompletedTask;
        try
        {
            _plugins.RunStartupHooks();
            KickDeclaredResourceRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "plugin startup hooks failed");
        }

        return Task.CompletedTask;
    }

    internal void NotifyPluginEvent(string eventType, string payloadJson)
    {
        if (IsShuttingDown)
            return;
        _plugins.HandleRuntimeEvent(eventType, payloadJson);
    }

    private async Task<JsonElement> SetPluginEnabledAsync(
        PluginSetEnabledParams p, bool enabled, CancellationToken ct)
    {
        var pluginId = RequireField(p.PluginId, "plugin_id");
        if (!enabled)
        {
            await CleanupPluginSessionAsync(
                    pluginId,
                    _plugins.OwnedPaneIds(pluginId),
                    _plugins.OwnsPopup(pluginId),
                    ct)
                .ConfigureAwait(false);
        }

        var result = _plugins.SetEnabled(pluginId, enabled);
        if (!result.IsOk)
            throw PluginFault(result.Error);

        return OkTyped(
            new PluginEnabledResult { Plugin = ToDto(result.Value.Plugin) },
            ProtocolJsonContext.Default.PluginEnabledResult);
    }

    private async Task CleanupPluginSessionAsync(
        string pluginId,
        IReadOnlyList<string> paneIds,
        bool closePopup,
        CancellationToken ct)
    {
        foreach (var paneId in paneIds)
        {
            try
            {
                await PaneCloseAsync(new PaneCloseParams { PaneId = paneId }, null, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException ex) when (IsAbsentPluginSurface(ex))
            {
            }

            _plugins.ForgetOwnedPane(paneId);
        }

        if (closePopup)
        {
            try
            {
                await PopupCloseAsync(null, ct).ConfigureAwait(false);
            }
            catch (ControlPlaneException ex) when (IsAbsentPluginSurface(ex))
            {
            }

            _plugins.ForgetOwnedPopup(pluginId);
        }

        ClearPluginReports(pluginId);
        DropPluginResources(pluginId);
    }

    internal static bool IsAbsentPluginSurface(ControlPlaneException ex) =>
        ex.Code == ProtocolErrorCodes.NotFound
        || string.Equals(ex.Message, PopupNotOpenMessage, StringComparison.Ordinal);

    private void ClearPluginReports(string pluginId)
    {
        var source = PluginIdentifiers.SourceOf(pluginId);
        _metadata.ClearSource(source);
        foreach (var pane in _state.ListPanes())
        {
            if (pane.AgentAuthority is { } held
                && string.Equals(held.Source, source, StringComparison.Ordinal))
            {
                _state.UpdatePane(pane.Id, current => current with
                {
                    AgentAuthority = null,
                    AgentStatus = AgentStatus.Unknown,
                    AgentKind = null,
                    AgentMessage = null,
                });
            }
        }

        lock (_agentViewGate)
        {
            if (_agentViewOverride is { } view
                && string.Equals(view.Source, source, StringComparison.Ordinal))
            {
                _agentViewOverride = null;
            }
        }
    }

    private async Task<PluginPaneOpenedResult> OpenPlannedPluginPaneAsync(
        PluginPaneOpenPlan plan,
        PluginPaneOpenParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        var program = plan.Command[0];
        var args = plan.Command.Skip(1).ToArray();
        var cwd = string.IsNullOrWhiteSpace(p.Cwd) ? plan.Cwd : p.Cwd;
        switch (plan.Placement)
        {
            case "popup":
                await OpenPluginPopupAsync(plan, program, args, cwd, p, connection, ct).ConfigureAwait(false);
                _plugins.NoteOwnedPopup(plan.PluginId);
                return new PluginPaneOpenedResult
                {
                    PluginPane = new PluginPaneInfoDto
                    {
                        PluginId = plan.PluginId,
                        Entrypoint = plan.Entrypoint,
                        Placement = "popup",
                    },
                };
            case "tab":
                {
                    var created = await DispatchAsync(
                            ProtocolMethods.TabCreate,
                            JsonFrom(TabCreateObject(p.WorkspaceId, plan.Title, program, args, cwd, plan.Environment, p.Focus)),
                            connection,
                            ct)
                        .ConfigureAwait(false);
                    var paneId = ReadPaneId(created);
                    if (paneId is not null)
                        _plugins.NoteOwnedPane(plan.PluginId, plan.Entrypoint, paneId);
                    return new PluginPaneOpenedResult
                    {
                        PluginPane = new PluginPaneInfoDto
                        {
                            PluginId = plan.PluginId,
                            Entrypoint = plan.Entrypoint,
                            PaneId = paneId,
                            Placement = "tab",
                        },
                    };
                }
            default:
                {
                    var target = p.TargetPaneId ?? CurrentPaneId();
                    if (string.IsNullOrWhiteSpace(target))
                        throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "no active pane");
                    var direction = string.IsNullOrWhiteSpace(p.Direction) ? "right" : p.Direction;
                    var split = new JsonObject
                    {
                        ["pane_id"] = target,
                        ["direction"] = plan.Placement == "overlay" ? "down" : direction,
                        ["command"] = program,
                        ["label"] = plan.Title,
                        ["cwd"] = cwd,
                    };
                    if (args.Length > 0)
                        split["args"] = ArgsArray(args);
                    if (plan.Environment.Count > 0)
                        split["env"] = EnvObject(plan.Environment);
                    if (plan.Placement == "overlay")
                        split["close_on_exit"] = true;
                    // Zoomed always
                    // takes focus. Split and overlay keep the previous pane when
                    // focus is false.
                    split["focus"] = plan.Placement == "zoomed" || p.Focus is not false;
                    var created = await DispatchAsync(
                            ProtocolMethods.PaneSplit,
                            JsonFrom(split),
                            connection,
                            ct)
                        .ConfigureAwait(false);
                    var paneId = created.TryGetProperty("pane_id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(paneId))
                        throw new ControlPlaneException(ProtocolErrorCodes.InternalError, "plugin_pane_open_failed");
                    _plugins.NoteOwnedPane(plan.PluginId, plan.Entrypoint, paneId);
                    if (plan.Placement is "overlay" or "zoomed")
                    {
                        await DispatchAsync(
                                ProtocolMethods.PaneZoom,
                                JsonFrom(new JsonObject { ["pane_id"] = paneId, ["mode"] = "on" }),
                                connection,
                                ct)
                            .ConfigureAwait(false);
                    }

                    // Overlay is
                    // not Zoomed. Keep the previous pane when focus is false.
                    if (plan.Placement == "overlay" && p.Focus is false)
                    {
                        await DispatchAsync(
                                ProtocolMethods.PaneFocus,
                                JsonFrom(new JsonObject { ["pane_id"] = target }),
                                connection,
                                ct)
                            .ConfigureAwait(false);
                    }

                    return new PluginPaneOpenedResult
                    {
                        PluginPane = new PluginPaneInfoDto
                        {
                            PluginId = plan.PluginId,
                            Entrypoint = plan.Entrypoint,
                            PaneId = paneId,
                            Placement = plan.Placement,
                        },
                    };
                }
        }
    }

    private async Task OpenPluginPopupAsync(
        PluginPaneOpenPlan plan,
        string program,
        string[] args,
        string? cwd,
        PluginPaneOpenParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        var (areaCols, areaRows) = PluginPopupHostArea(p.Width, p.Height, plan.Width, plan.Height);
        var open = new JsonObject
        {
            ["command"] = program,
            ["client_mode"] = "terminal",
            ["area_cols"] = areaCols,
            ["area_rows"] = areaRows,
        };
        if (args.Length > 0)
            open["args"] = ArgsArray(args);
        if (!string.IsNullOrWhiteSpace(cwd))
            open["cwd"] = cwd;
        if (plan.Environment.Count > 0)
            open["env"] = EnvObject(plan.Environment);
        if (p.Width is { } width)
            open["width"] = JsonNode.Parse(width.GetRawText());
        else if (plan.Width is not null)
            open["width"] = plan.Width;
        if (p.Height is { } height)
            open["height"] = JsonNode.Parse(height.GetRawText());
        else if (plan.Height is not null)
            open["height"] = plan.Height;
        await DispatchAsync(ProtocolMethods.PopupOpen, JsonFrom(open), connection, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    // / Host area for a plugin popup.
    /// uses the live terminal area. The control plane does not store that
    /// size, so this uses the VT floor and grows it so a cell-sized
    // / manifest request is not clamped.
    /// <c>src/app/api/plugins/panes.rs:25-33</c> takes width and height from
    /// the request or the manifest.
    /// </summary>
    internal static (int AreaCols, int AreaRows) PluginPopupHostArea(
        JsonElement? requestWidth,
        JsonElement? requestHeight,
        string? planWidth,
        string? planHeight)
    {
        return (
            Math.Max(VtFloorDefaults.DefaultCols, RequestedPopupCells(requestWidth, planWidth)),
            Math.Max(VtFloorDefaults.DefaultRows, RequestedPopupCells(requestHeight, planHeight)));
    }

    private static int RequestedPopupCells(JsonElement? request, string? plan)
    {
        if (request is { } requestElement
            && PopupSize.TryRead(requestElement, out var requestSize)
            && requestSize is { IsPercent: false, Value: var requestCells }
            && requestCells > 0)
        {
            return requestCells;
        }

        if (int.TryParse(plan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var planCells)
            && planCells > 0)
        {
            return planCells;
        }

        return 0;
    }

    internal PluginInvocationContext CurrentPluginContext(string correlationId)
    {
        var focused = _state.Snapshot();
        WorkspaceState? ws = null;
        if (TryId(focused.FocusedWorkspaceId, out var wsId))
            focused.Workspaces.TryGetValue(wsId, out ws);
        return PluginContextForWorkspace(ws, correlationId);
    }

    internal PluginInvocationContext PluginContextForEvent(
        string hookName,
        string eventJson,
        string correlationId)
    {
        if (PluginHookCatalog.IsWorktreeHook(hookName))
            return PluginContextForWorktreeEvent(hookName, eventJson, correlationId);

        var current = CurrentPluginContext(correlationId) with { InvocationSource = hookName };
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(eventJson) ? "{}" : eventJson);
            var root = doc.RootElement;
            string? Read(string name) =>
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out var el)
                && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;
            var eventWorkspaceId = Read("workspace_id");
            var worktree = current.Worktree;
            if (!string.IsNullOrEmpty(eventWorkspaceId)
                && !string.Equals(eventWorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
            {
                worktree = null;
            }

            return current with
            {
                WorkspaceId = eventWorkspaceId ?? current.WorkspaceId,
                Worktree = worktree,
                TabId = Read("tab_id") ?? current.TabId,
                FocusedPaneId = Read("pane_id") ?? current.FocusedPaneId,
                FocusedPaneAgent = Read("agent") ?? current.FocusedPaneAgent,
                FocusedPaneStatus = Read("agent_status") ?? current.FocusedPaneStatus,
            };
        }
        catch (JsonException)
        {
            return current;
        }
    }

    private PluginInvocationContext PluginContextForWorkspace(WorkspaceState? ws, string correlationId)
    {
        TabState? tab = null;
        if (TryId(ws?.FocusedTabId, out var tabId))
            tab = _state.GetTab(new TabId(tabId));
        PaneState? pane = null;
        if (TryId(tab?.FocusedPaneId, out var paneId))
            pane = _state.GetPane(new PaneId(paneId));
        return new PluginInvocationContext
        {
            WorkspaceId = ws?.Id.Value,
            WorkspaceLabel = ws?.Label,
            WorkspaceCwd = ws?.Cwd ?? pane?.Cwd,
            Worktree = PluginInvocationContextWorktree.FromMembership(ws?.Worktree),
            TabId = tab?.Id.Value ?? pane?.TabId.Value,
            TabLabel = tab?.Label,
            FocusedPaneId = pane?.Id.Value,
            FocusedPaneCwd = pane?.Cwd,
            FocusedPaneAgent = pane?.AgentKind ?? pane?.AgentName,
            FocusedPaneStatus = pane?.AgentStatus.ToString().ToLowerInvariant(),
            AgentSession = PluginInvocationContextSession.FromPane(pane),
            ProgramName = string.IsNullOrEmpty(pane?.Command) ? null : pane.Command,
            SelectedText = null,
            InvocationSource = "api",
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// </summary>
    private PluginInvocationContext PluginContextForWorktreeEvent(
        string hookName,
        string eventJson,
        string correlationId)
    {
        var workspaceId = PluginWorktreeEventJson.ReadWorkspaceId(eventJson);
        if (!string.IsNullOrWhiteSpace(workspaceId)
            && _state.GetWorkspace(new WorkspaceId(workspaceId)) is { } live)
        {
            return PluginContextForWorkspace(live, correlationId) with { InvocationSource = hookName };
        }

        var nested = PluginWorktreeEventJson.ReadNestedWorktree(eventJson);
        return new PluginInvocationContext
        {
            WorkspaceId = workspaceId,
            WorkspaceLabel = PluginWorktreeEventJson.ReadWorkspaceLabel(eventJson),
            WorkspaceCwd = nested?.CheckoutPath ?? PluginWorktreeEventJson.ReadWorktreePath(eventJson),
            Worktree = nested,
            InvocationSource = hookName,
            CorrelationId = correlationId,
            SelectedText = null,
        };
    }

    private string? CurrentPaneId()
    {
        var snap = _state.Snapshot();
        if (!TryId(snap.FocusedWorkspaceId, out var wsId)
            || !snap.Workspaces.TryGetValue(wsId, out var ws)
            || !TryId(ws.FocusedTabId, out var tabId)
            || !snap.Tabs.TryGetValue(tabId, out var tab)
            || !TryId(tab.FocusedPaneId, out var focusedPaneId))
        {
            return null;
        }

        return focusedPaneId;
    }

    private static bool TryId(WorkspaceId? id, out string value)
    {
        value = "";
        if (id is not { } live || string.IsNullOrEmpty(live.Value))
            return false;
        value = live.Value;
        return true;
    }

    private static bool TryId(TabId? id, out string value)
    {
        value = "";
        if (id is not { } live || string.IsNullOrEmpty(live.Value))
            return false;
        value = live.Value;
        return true;
    }

    private static bool TryId(PaneId? id, out string value)
    {
        value = "";
        if (id is not { } live || string.IsNullOrEmpty(live.Value))
            return false;
        value = live.Value;
        return true;
    }

    private bool OwnsPane(string paneId)
    {
        foreach (var plugin in _plugins.List(null).IsOk ? _plugins.List(null).Value : [])
        {
            if (_plugins.OwnedPaneIds(plugin.PluginId).Contains(paneId))
                return true;
        }

        return false;
    }

    private IEnumerable<string> AllOwnedPaneIds()
    {
        var listed = _plugins.List(null);
        if (!listed.IsOk)
            yield break;
        foreach (var plugin in listed.Value)
        {
            foreach (var id in _plugins.OwnedPaneIds(plugin.PluginId))
                yield return id;
        }
    }

    private string? OwnerPluginId(string paneId)
    {
        var listed = _plugins.List(null);
        if (!listed.IsOk)
            return null;
        foreach (var plugin in listed.Value)
        {
            if (_plugins.OwnedPaneIds(plugin.PluginId).Contains(paneId))
                return plugin.PluginId;
        }

        return null;
    }

    private void ApplyGrantToken(
        string method,
        JsonElement? parameters,
        string? grantToken,
        bool pluginConnection)
    {
        string? source = null;
        string? pluginId = null;
        string? actionId = null;
        string? paneId = null;
        if (parameters is { ValueKind: JsonValueKind.Object } p)
        {
            if (p.TryGetProperty("source", out var sourceEl) && sourceEl.ValueKind == JsonValueKind.String)
                source = sourceEl.GetString();
            if (p.TryGetProperty("plugin_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                pluginId = idEl.GetString();
            if (p.TryGetProperty("action_id", out var actionEl) && actionEl.ValueKind == JsonValueKind.String)
                actionId = actionEl.GetString();
            if (p.TryGetProperty("pane_id", out var paneEl) && paneEl.ValueKind == JsonValueKind.String)
                paneId = paneEl.GetString();
            if (p.TryGetProperty("resource_id", out var resourceEl) && resourceEl.ValueKind == JsonValueKind.String
                && PluginResourceId.TryParse(resourceEl.GetString(), out var resourceId))
            {
                pluginId = resourceId.PluginId;
            }
        }

        if (string.Equals(method, ProtocolMethods.PluginActionInvoke, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(actionId))
        {
            var owner = _plugins.ResolveActionOwnerPluginId(actionId, pluginId);
            if (owner is not null)
                pluginId = owner;
        }

        if ((string.Equals(method, ProtocolMethods.PluginPaneFocus, StringComparison.Ordinal)
                || string.Equals(method, ProtocolMethods.PluginPaneClose, StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(paneId))
        {
            var owner = OwnerPluginId(paneId);
            if (owner is not null)
                pluginId = owner;
        }

        if (string.Equals(method, ProtocolMethods.PluginPaneSendText, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(paneId))
        {
            pluginId = _plugins.ResolveSendTextPluginId(paneId);
        }

        var decision = _plugins.CheckGrant(grantToken, method, source, pluginId, pluginConnection);
        if (!decision.Allowed)
        {
            if (string.Equals(decision.ErrorCode, PluginError.SourceDenied, StringComparison.Ordinal))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    PluginError.SourceDenied,
                    PluginError.SourceDenied);
            }

            throw new ControlPlaneException(
                ProtocolErrorCodes.CapabilityInvalid,
                ProtocolErrors.CapabilityMissing,
                ProtocolErrors.CapabilityMissing);
        }

        if (string.Equals(method, ProtocolMethods.NotificationShow, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(source)
            && ControlPlaneService.IsPluginSource(source)
            && !pluginConnection)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "plugin source requires notification.request grant");
        }
    }

    private static ControlPlaneException PluginFault(PluginError error)
    {
        var code = error.Code switch
        {
            PluginError.NotFound or PluginError.ActionNotFound or PluginError.PaneNotFound
                or PluginError.ResourceNotFound =>
                ProtocolErrorCodes.NotFound,
            PluginError.CapabilityMissing => ProtocolErrorCodes.CapabilityInvalid,
            PluginError.Disabled => ProtocolErrorCodes.InvalidState,
            PluginError.CommandLimit => ProtocolErrorCodes.InvalidState,
            PluginError.StaleRevision => ProtocolErrorCodes.InvalidState,
            _ => ProtocolErrorCodes.InvalidParams,
        };
        return new ControlPlaneException(code, error.Message, error.Code);
    }

    private static JsonElement JsonFrom(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonArray ArgsArray(IReadOnlyList<string> args)
    {
        var nodes = new JsonNode?[args.Count];
        for (var i = 0; i < args.Count; i++)
            nodes[i] = args[i];
        return new JsonArray(nodes);
    }

    private static JsonObject EnvObject(IReadOnlyDictionary<string, string> env)
    {
        var obj = new JsonObject();
        foreach (var kv in env)
            obj[kv.Key] = kv.Value;
        return obj;
    }

    private static JsonObject TabCreateObject(
        string? workspaceId,
        string label,
        string command,
        string[] args,
        string? cwd,
        IReadOnlyDictionary<string, string> env,
        bool? focus)
    {
        var obj = new JsonObject
        {
            ["create_pane"] = true,
            ["label"] = label,
            ["command"] = command,
        };
        if (!string.IsNullOrWhiteSpace(workspaceId))
            obj["workspace_id"] = workspaceId;
        if (args.Length > 0)
            obj["args"] = ArgsArray(args);
        if (!string.IsNullOrWhiteSpace(cwd))
            obj["cwd"] = cwd;
        if (focus is { } f)
            obj["focus"] = f;
        if (env.Count > 0)
            obj["env"] = EnvObject(env);
        return obj;
    }

    private static string? ReadPaneId(JsonElement created)
    {
        if (created.ValueKind != JsonValueKind.Object)
            return null;
        if (created.TryGetProperty("pane_id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (created.TryGetProperty("pane", out var pane)
            && pane.ValueKind == JsonValueKind.Object
            && pane.TryGetProperty("pane_id", out var nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        return null;
    }

    private static Hypa.AgentRuntime.Protocol.Models.PluginLinkResult ToLink(
        AgentRuntime.Application.Plugins.PluginLinkResult result) =>
        new()
        {
            Plugin = ToDto(result.Plugin),
            TrustPreview = new PluginTrustPreviewDto
            {
                Commands = result.TrustPreview.Commands,
                Grants = result.TrustPreview.Grants,
            },
        };

    private static InstalledPluginDto ToDto(InstalledPlugin plugin) =>
        new()
        {
            PluginId = plugin.PluginId,
            Name = plugin.Name,
            Version = plugin.Version,
            MinHypaVersion = plugin.MinHypaVersion,
            Description = plugin.Description,
            ManifestPath = plugin.ManifestPath,
            PluginRoot = plugin.PluginRoot,
            Enabled = plugin.Enabled,
            Platforms = plugin.Platforms,
            Actions = plugin.Actions.Select(a => new PluginManifestActionDto
            {
                Id = a.Id,
                Title = a.Title,
                Description = a.Description,
                Contexts = a.Contexts,
                Platforms = a.Platforms,
                Command = a.Command,
                Palette = a.Palette,
                Key = a.SuggestedKey,
            }).ToArray(),
            Events = plugin.Events.Select(e => new PluginManifestEventDto
            {
                On = e.On,
                Platforms = e.Platforms,
                Command = e.Command,
            }).ToArray(),
            Panes = plugin.Panes.Select(pane => new PluginManifestPaneDto
            {
                Id = pane.Id,
                Title = pane.Title,
                Description = pane.Description,
                Platforms = pane.Platforms,
                Placement = pane.Placement,
                Command = pane.Command,
            }).ToArray(),
            LinkHandlers = plugin.LinkHandlers.Select(handler => new PluginManifestLinkHandlerDto
            {
                Id = handler.Id,
                Title = handler.Title,
                Pattern = handler.Pattern,
                Action = handler.Action,
                Platforms = handler.Platforms,
            }).ToArray(),
            Startup = plugin.Startup.Select(s => new PluginCommandSpecDto
            {
                Platforms = s.Platforms,
                Command = s.Command,
            }).ToArray(),
            Resources = plugin.Resources.Count == 0
                ? null
                : plugin.Resources.Select(r => new PluginManifestResourceDto
                {
                    Id = r.Id,
                    Kind = r.Kind,
                    Projection = r.Projection,
                    Title = r.Title,
                    Platforms = r.Platforms,
                    Command = r.Command,
                }).ToArray(),
            MenuItems = plugin.MenuItems.Count == 0
                ? null
                : plugin.MenuItems.Select(item => new PluginManifestMenuItemDto
                {
                    Id = item.Id,
                    Title = item.Title,
                    Contexts = item.Contexts,
                    Action = item.Action,
                }).ToArray(),
            SettingsFields = plugin.SettingsFields.Count == 0
                ? null
                : plugin.SettingsFields.Select(f => new PluginManifestSettingsFieldDto
                {
                    Key = f.Key,
                    Type = f.Type,
                    Title = f.Title,
                    Default = f.Default,
                    Choices = f.Choices.Count == 0 ? null : f.Choices,
                }).ToArray(),
            Grants = plugin.RequestedGrants,
            Warnings = plugin.Warnings,
        };

    private static PluginActionInfoDto ToActionDto(PluginActionInfo action) =>
        new()
        {
            PluginId = action.PluginId,
            ActionId = action.ActionId,
            Title = action.Title,
            Description = action.Description,
            Contexts = action.Contexts,
            Command = action.Command,
            Platforms = action.Platforms,
            Palette = action.Palette,
            Key = action.SuggestedKey,
        };

    private static PluginActionInvokeResultDto ToInvoke(
        AgentRuntime.Application.Plugins.PluginActionInvokeResult result) =>
        new()
        {
            Action = ToActionDto(result.Action),
            Context = ToContextDto(result.Context),
            Log = ToLogDto(result.Log),
        };

    private static PluginCommandLogDto ToLogDto(PluginCommandLog log) =>
        new()
        {
            LogId = log.LogId,
            PluginId = log.PluginId,
            ActionId = log.ActionId,
            Event = log.Event,
            Command = log.Command,
            Status = log.Status,
            StartedUnixMs = log.StartedUnixMs,
            FinishedUnixMs = log.FinishedUnixMs,
            ExitCode = log.ExitCode,
            Pid = log.Pid,
            Stdout = log.Stdout,
            Stderr = log.Stderr,
            Error = log.Error,
            Result = log.Result is null
                ? null
                : new PluginStructuredResultDto
                {
                    Status = log.Result.Status,
                    Message = log.Result.Message,
                    Data = log.Result.DataJson is null
                        ? null
                        : JsonDocument.Parse(log.Result.DataJson).RootElement.Clone(),
                },
        };

    private static PluginInvocationContextDto ToContextDto(PluginInvocationContext context) =>
        new()
        {
            WorkspaceId = context.WorkspaceId,
            WorkspaceLabel = context.WorkspaceLabel,
            WorkspaceCwd = context.WorkspaceCwd,
            Worktree = ToWorktreeDto(context.Worktree),
            TabId = context.TabId,
            TabLabel = context.TabLabel,
            FocusedPaneId = context.FocusedPaneId,
            FocusedPaneCwd = context.FocusedPaneCwd,
            FocusedPaneAgent = context.FocusedPaneAgent,
            FocusedPaneStatus = context.FocusedPaneStatus,
            AgentSession = ToSessionDto(context.AgentSession),
            SelectedText = null,
            InvocationSource = context.InvocationSource,
            CorrelationId = context.CorrelationId,
            ProgramName = context.ProgramName,
            ClickedUrl = context.ClickedUrl,
            LinkHandlerId = context.LinkHandlerId,
        };

    private static PluginWorktreeContextDto? ToWorktreeDto(PluginWorktreeContext? worktree) =>
        worktree is null
            ? null
            : new PluginWorktreeContextDto
            {
                RepoKey = worktree.RepoKey,
                RepoName = worktree.RepoName,
                RepoRoot = worktree.RepoRoot,
                CheckoutPath = worktree.CheckoutPath,
                IsLinkedWorktree = worktree.IsLinkedWorktree,
            };

    private static PluginWorktreeContext? FromWorktreeDto(PluginWorktreeContextDto? dto)
    {
        if (dto is null)
            return null;
        var mapped = new PluginWorktreeContext
        {
            RepoKey = dto.RepoKey ?? "",
            RepoName = dto.RepoName ?? "",
            RepoRoot = dto.RepoRoot ?? "",
            CheckoutPath = dto.CheckoutPath ?? "",
            IsLinkedWorktree = dto.IsLinkedWorktree,
        };
        return mapped;
    }

    private static NativeAgentSessionDto? ToSessionDto(NativeAgentSessionRef? session) =>
        session is null
            ? null
            : new NativeAgentSessionDto
            {
                Kind = session.Kind,
                Value = session.Value,
                Source = session.Source,
                Agent = session.Agent,
                SessionStartSource = session.SessionStartSource,
            };

    private static PluginInvocationContext? FromDto(PluginInvocationContextDto? dto) =>
        dto is null
            ? null
            : new PluginInvocationContext
            {
                WorkspaceId = dto.WorkspaceId,
                WorkspaceLabel = dto.WorkspaceLabel,
                WorkspaceCwd = dto.WorkspaceCwd,
                Worktree = FromWorktreeDto(dto.Worktree),
                TabId = dto.TabId,
                TabLabel = dto.TabLabel,
                FocusedPaneId = dto.FocusedPaneId,
                FocusedPaneCwd = dto.FocusedPaneCwd,
                FocusedPaneAgent = dto.FocusedPaneAgent,
                FocusedPaneStatus = dto.FocusedPaneStatus,
                AgentSession = null,
                SelectedText = null,
                InvocationSource = dto.InvocationSource,
                CorrelationId = dto.CorrelationId,
                ProgramName = dto.ProgramName,
                ClickedUrl = dto.ClickedUrl,
                LinkHandlerId = dto.LinkHandlerId,
            };

    private sealed class ControlPlanePluginContextSource : IPluginContextSource
    {
        private readonly ControlPlaneService _owner;

        public ControlPlanePluginContextSource(ControlPlaneService owner) => _owner = owner;

        public PluginInvocationContext Current(string correlationId) =>
            _owner.CurrentPluginContext(correlationId);

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId) =>
            _owner.PluginContextForEvent(hookName, eventJson, correlationId);
    }
}
