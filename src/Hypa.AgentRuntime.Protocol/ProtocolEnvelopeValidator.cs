using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Envelopes;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Pure envelope/params validation for golden fixtures and clients.
/// Enforces missing required fields; tolerates unknown extra JSON properties
/// (forward compatibility is handled by System.Text.Json ignore-unknown).
/// </summary>
public static class ProtocolEnvelopeValidator
{
    public static bool TryValidateRequest(RpcRequest? request, out string? error)
    {
        if (request is null)
        {
            error = "request is null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            error = "method is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateEvent(RpcEvent? envelope, out string? error)
    {
        if (envelope is null)
        {
            error = "event envelope is null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(envelope.Event))
        {
            error = "event is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateRuntimeEventParams(RuntimeEventParams? p, out string? error)
    {
        if (p is null)
        {
            error = "runtime event params are null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.Type))
        {
            error = "type is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateEventsUnsubscribe(EventsUnsubscribeParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.SubscriptionId))
        {
            error = "subscription_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateLeaseRelease(LeaseReleaseParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.LeaseId))
        {
            error = "lease_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateLeaseRenew(LeaseRenewParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.LeaseId))
        {
            error = "lease_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateTerminalVisibleSet(TerminalVisibleSetParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.SubscriptionId))
        {
            error = "subscription_id is required";
            return false;
        }

        if (p.PaneIds is null)
        {
            error = "pane_ids is required";
            return false;
        }

        foreach (var id in p.PaneIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "pane_id must be a non-empty string";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryValidateTerminalObserve(TerminalObserveParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.SubscriptionId))
        {
            error = "subscription_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateTerminalControl(TerminalControlParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.LeaseId))
        {
            error = "lease_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.SubscriptionId))
        {
            error = "subscription_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidatePaneSendKeys(PaneSendKeysParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.Data))
        {
            error = "data is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateLeaseClaim(LeaseClaimParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.Scope))
        {
            error = "scope is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateAgentWait(AgentWaitParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateAgentPrompt(AgentPromptParams? p, out string? error)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.PaneId))
        {
            error = "pane_id is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.Message))
        {
            error = "message is required";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validate method params for a known P0 method when a typed model is available.
    /// Returns true for methods with empty/optional params.
    /// </summary>
    public static bool TryValidateMethodParams(string method, JsonElement? parameters, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(method))
        {
            error = "method is required";
            return false;
        }

        // Methods with empty params always succeed when present or absent.
        if (method is ProtocolMethods.RuntimeHealth
            or ProtocolMethods.SessionSnapshot
            or ProtocolMethods.RuntimeBindingGet
            or ProtocolMethods.TabList
            or ProtocolMethods.PaneZoom
            or ProtocolMethods.PaneLayout
            or ProtocolMethods.LayoutExport
            or ProtocolMethods.PaneCurrent
            or ProtocolMethods.PaneEdges
            or ProtocolMethods.PaneProcessInfo
            or ProtocolMethods.ServerStop
            or ProtocolMethods.ServerLiveHandoff
            or ProtocolMethods.ServerReloadConfig
            or ProtocolMethods.ServerAgentManifests
            or ProtocolMethods.ServerReloadAgentManifests
            or ProtocolMethods.AgentViewClear
            or ProtocolMethods.ClientWindowTitleClear
            or ProtocolMethods.PopupClose
            or ProtocolMethods.IntegrationList)
        {
            return true;
        }

        // Empty merge is a no-op, not an error.
        if (method is ProtocolMethods.ClientHostThemeSet
            && (parameters is null || parameters.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return true;
        }

        // Without params object, reject methods that require fields.
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "params are required";
            return false;
        }

        var p = parameters.Value;
        return method switch
        {
            ProtocolMethods.EventsUnsubscribe =>
                RequireString(p, "subscription_id", out error),
            ProtocolMethods.RuntimeLeaseClaim =>
                RequireString(p, "pane_id", out error) && RequireString(p, "scope", out error),
            ProtocolMethods.RuntimeLeaseRelease =>
                RequireString(p, "lease_id", out error),
            ProtocolMethods.RuntimeLeaseRenew =>
                RequireString(p, "lease_id", out error),
            ProtocolMethods.TerminalObserve =>
                RequireString(p, "pane_id", out error) && RequireString(p, "subscription_id", out error),
            ProtocolMethods.TerminalVisibleSet =>
                RequireString(p, "subscription_id", out error) && RequireArray(p, "pane_ids", out error),
            ProtocolMethods.TerminalControl =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "lease_id", out error)
                && RequireString(p, "subscription_id", out error),
            ProtocolMethods.PaneSendKeys =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "data", out error),
            ProtocolMethods.RuntimeBindingSet =>
                p.TryGetProperty("binding", out _) || Fail("binding is required", out error),
            ProtocolMethods.AgentWait =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.AgentPrompt =>
                RequireString(p, "pane_id", out error) && RequireString(p, "message", out error),
            ProtocolMethods.WorkspaceMove =>
                RequireString(p, "workspace_id", out error) && RequireInt(p, "insert_index", out error),
            ProtocolMethods.WorkspaceMoveBlock =>
                RequireArray(p, "workspace_ids", out error),
            ProtocolMethods.WorkspaceReportMetadata =>
                RequireString(p, "workspace_id", out error)
                && RequireString(p, "source", out error)
                && RequireObject(p, "tokens", out error),
            ProtocolMethods.PaneReportAgent =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "source", out error)
                && RequireString(p, "agent", out error)
                && RequireString(p, "state", out error)
                && (!HasNonEmptyString(p, "agent_session_id")
                    || !HasNonEmptyString(p, "agent_session_path")
                    || Fail("provide agent_session_id or agent_session_path", out error)),
            ProtocolMethods.PaneReportAgentSession =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "source", out error)
                && RequireString(p, "agent", out error)
                && (HasNonEmptyString(p, "agent_session_id") || HasNonEmptyString(p, "agent_session_path")
                    || Fail("agent_session_id or agent_session_path is required", out error)),
            ProtocolMethods.PaneReportMetadata =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "source", out error)
                && RequireObject(p, "tokens", out error),
            ProtocolMethods.PaneReleaseAgent =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "source", out error)
                && RequireString(p, "agent", out error),
            ProtocolMethods.PaneClearAgentAuthority =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.EventsSubscribe => true,
            ProtocolMethods.EventsWait =>
                RequireEventsWaitMatch(p, out error),
            ProtocolMethods.PaneWaitForOutput =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.RuntimeHandoffExport =>
                RequireString(p, "pane_id", out error) && RequireString(p, "lease_id", out error),
            ProtocolMethods.RuntimeHandoffAdopt =>
                RequireString(p, "pane_id", out error)
                && RequireString(p, "lease_id", out error)
                && RequireString(p, "handoff_path", out error)
                && RequireString(p, "nonce", out error),
            ProtocolMethods.WorkspaceFocus =>
                RequireString(p, "workspace_id", out error),
            ProtocolMethods.WorkspaceRename =>
                RequireString(p, "workspace_id", out error) && RequireString(p, "label", out error),
            ProtocolMethods.WorkspaceClose =>
                RequireString(p, "workspace_id", out error),
            ProtocolMethods.TabCreate =>
                RequireString(p, "workspace_id", out error),
            ProtocolMethods.TabList => true,
            ProtocolMethods.TabGet =>
                RequireString(p, "tab_id", out error),
            ProtocolMethods.TabFocus =>
                RequireString(p, "tab_id", out error),
            ProtocolMethods.TabRename =>
                RequireString(p, "tab_id", out error) && RequireString(p, "label", out error),
            ProtocolMethods.TabMove =>
                RequireString(p, "tab_id", out error) && RequireInt(p, "index", out error),
            ProtocolMethods.TabClose =>
                RequireString(p, "tab_id", out error),
            ProtocolMethods.PaneSplit =>
                RequireString(p, "pane_id", out error) && RequireString(p, "direction", out error),
            ProtocolMethods.PaneMove =>
                RequireString(p, "pane_id", out error) && RequireString(p, "destination", out error),
            ProtocolMethods.PaneZoom => true,
            ProtocolMethods.PaneFocusDirection =>
                RequireString(p, "direction", out error),
            ProtocolMethods.PaneLayout => true,
            ProtocolMethods.LayoutExport => true,
            ProtocolMethods.LayoutApply =>
                RequireString(p, "workspace_id", out error) && RequireObject(p, "root", out error),
            ProtocolMethods.LayoutSetSplitRatio =>
                RequireString(p, "tab_id", out error)
                && RequireArray(p, "path", out error)
                && RequireNumber(p, "ratio", out error)
                && RequireString(p, "lease_id", out error),
            ProtocolMethods.AgentStart =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.PaneRename =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.PaneCurrent => true,
            ProtocolMethods.PaneFocus =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.PaneNeighbor =>
                RequireString(p, "direction", out error),
            ProtocolMethods.PaneEdges => true,
            ProtocolMethods.PaneProcessInfo => true,
            ProtocolMethods.PaneSendInput =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.PaneRead =>
                RequireString(p, "pane_id", out error),
            ProtocolMethods.PaneInputSet =>
                RequireString(p, "pane_id", out error) && RequireString(p, "right_click", out error),
            ProtocolMethods.PaneSwap =>
                HasNonEmptyString(p, "direction") || HasNonEmptyString(p, "target_pane_id")
                    || Fail("direction or target_pane_id is required", out error),
            ProtocolMethods.PaneShow =>
                RequireString(p, "pane_id", out error) && RequirePlacementCredential(p, out error),
            ProtocolMethods.PaneHide =>
                RequireString(p, "pane_id", out error) && RequireHideCredential(p, out error),
            ProtocolMethods.UiClientMode =>
                RequireString(p, "client_mode", out error),
            ProtocolMethods.NotificationShow =>
                RequireString(p, "title", out error),
            ProtocolMethods.PaneScroll =>
                RequireString(p, "pane_id", out error) && RequireNumber(p, "offset", out error),
            ProtocolMethods.PaneLinkActivate =>
                RequireString(p, "pane_id", out error)
                && RequireNumber(p, "viewport_row", out error)
                && RequireNumber(p, "col", out error),
            ProtocolMethods.ClientWindowTitleSet =>
                RequireString(p, "title", out error),
            ProtocolMethods.ClientWindowTitleClear => true,
            ProtocolMethods.ClientHostThemeSet =>
                OptionalHostThemeRgb(p, "fg", out error)
                && OptionalHostThemeRgb(p, "bg", out error)
                && OptionalHostAppearance(p, out error)
                && OptionalHostThemePalette(p, out error),
            ProtocolMethods.PopupClose => true,
            ProtocolMethods.PopupOpen =>
                RequireString(p, "command", out error)
                && OptionalPopupSize(p, "width", out error)
                && OptionalPopupSize(p, "height", out error),
            ProtocolMethods.PopupSendKeys =>
                RequireString(p, "lease_id", out error) && RequireString(p, "data", out error),
            ProtocolMethods.PopupResize =>
                RequireInt(p, "area_cols", out error) && RequireInt(p, "area_rows", out error),
            ProtocolMethods.AgentExplain or ProtocolMethods.AgentFocus or ProtocolMethods.AgentRename =>
                HasNonEmptyString(p, "pane_id") || HasNonEmptyString(p, "agent_id")
                    || Fail("pane_id is required", out error),
            ProtocolMethods.AgentSendKeys =>
                (HasNonEmptyString(p, "pane_id") || HasNonEmptyString(p, "agent_id")
                    || Fail("pane_id is required", out error))
                && RequireArray(p, "keys", out error),
            ProtocolMethods.AgentViewSet =>
                RequireString(p, "source", out error),
            ProtocolMethods.AgentViewClear => true,
            ProtocolMethods.IntegrationInstall =>
                OptionalBool(p, "plan", out error)
                && RequireIntegrationTargetOrDetected(p, out error),
            ProtocolMethods.IntegrationUninstall =>
                RejectUninstallPlan(p, out error)
                && RequireIntegrationTargetOrDetected(p, out error),
            ProtocolMethods.IntegrationList => true,
            ProtocolMethods.PluginPaneSendText =>
                RequireString(p, "pane_id", out error) && RequireString(p, "text", out error),
            _ => true,
        };
    }

    private static bool HasNonEmptyString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(prop.GetString());

    private static bool OptionalBool(JsonElement p, string name, out string? error)
    {
        error = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var prop))
            return true;
        if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return true;
        error = name + " must be a boolean";
        return false;
    }

    /// <summary><c>plan</c> previews an install. Uninstall must not accept it.</summary>
    private static bool RejectUninstallPlan(JsonElement p, out string? error)
    {
        if (!OptionalBool(p, "plan", out error))
            return false;
        if (p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("plan", out var plan)
            && plan.ValueKind == JsonValueKind.True)
        {
            error = "plan applies to install";
            return false;
        }

        return true;
    }

    private static bool RequireIntegrationTargetOrDetected(JsonElement p, out string? error)
    {
        var detected = p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("detected", out var flag)
            && flag.ValueKind == JsonValueKind.True;
        var hasTarget = HasNonEmptyString(p, "target");
        if (detected && hasTarget)
        {
            error = "target and detected are mutually exclusive";
            return false;
        }

        if (detected)
        {
            error = null;
            return true;
        }

        return RequireString(p, "target", out error);
    }

    private static bool RequireString(JsonElement p, string name, out string? error)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(prop.GetString()))
        {
            error = $"{name} is required";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RequirePlacementCredential(JsonElement p, out string? error)
    {
        if (HasNonEmptyString(p, "lease_id")
            || HasNonEmptyString(p, "occupant_token")
            || HasNonEmptyString(p, "parent_capability"))
        {
            error = null;
            return true;
        }

        error = "lease_id, occupant_token, or parent_capability is required";
        return false;
    }

    /// <summary>
    /// Hide may use placement credentials or an owner cancel
    /// (<c>attach_client_id</c> plus <c>overlay_generation</c>).
    /// The control plane still matches the live connection. It does
    /// not treat the wire owner id as a credential.
    /// </summary>
    private static bool RequireHideCredential(JsonElement p, out string? error)
    {
        if (RequirePlacementCredential(p, out error))
            return true;
        if (HasNonEmptyString(p, "attach_client_id") && HasOverlayGeneration(p))
        {
            error = null;
            return true;
        }

        error = "lease_id, occupant_token, parent_capability, or owner overlay generation is required";
        return false;
    }

    private static bool HasOverlayGeneration(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty("overlay_generation", out var generation)
        && generation.ValueKind == JsonValueKind.Number;

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static bool RequireInt(JsonElement p, string name, out string? error)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Number)
        {
            error = $"{name} is required";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RequireNumber(JsonElement p, string name, out string? error) =>
        RequireInt(p, name, out error);

    private static bool RequireEventsWaitMatch(JsonElement p, out string? error)
    {
        if (!RequireObject(p, "match_event", out error))
            return false;

        var match = p.GetProperty("match_event");
        return RequireString(match, "event", out error)
            && RequireString(match, "pane_id", out error)
            && RequireString(match, "agent_status", out error);
    }

    private static bool RequireObject(JsonElement p, string name, out string? error)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} is required";
            return false;
        }

        error = null;
        return true;
    }

    private static bool OptionalHostThemeRgb(JsonElement p, string name, out string? error)
    {
        error = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var prop))
            return true;
        if (prop.ValueKind is JsonValueKind.Null)
            return true;
        if (prop.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} must be an object with r, g, b";
            return false;
        }

        return RequireRgbByte(prop, "r", name, out error)
            && RequireRgbByte(prop, "g", name, out error)
            && RequireRgbByte(prop, "b", name, out error);
    }

    private static bool RequireRgbByte(JsonElement rgb, string component, string owner, out string? error)
    {
        if (!rgb.TryGetProperty(component, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            error = $"{owner}.{component} is required";
            return false;
        }

        if (!prop.TryGetInt32(out var value) || value is < 0 or > 255)
        {
            error = $"{owner}.{component} must be 0-255";
            return false;
        }

        error = null;
        return true;
    }

    private static bool OptionalHostThemePalette(JsonElement p, out string? error)
    {
        error = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("palette", out var prop))
            return true;
        if (prop.ValueKind is JsonValueKind.Null)
            return true;
        if (prop.ValueKind != JsonValueKind.Array)
        {
            error = "palette must be an array";
            return false;
        }

        if (prop.GetArrayLength() > HostThemeSetParams.MaxPaletteEntries)
        {
            error = $"palette must have at most {HostThemeSetParams.MaxPaletteEntries} entries";
            return false;
        }

        foreach (var entry in prop.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                error = "palette entries must be objects with i, r, g, b";
                return false;
            }

            if (!RequirePaletteIndex(entry, out error)
                || !RequireRgbByte(entry, "r", "palette", out error)
                || !RequireRgbByte(entry, "g", "palette", out error)
                || !RequireRgbByte(entry, "b", "palette", out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RequirePaletteIndex(JsonElement entry, out string? error)
    {
        if (!entry.TryGetProperty("i", out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            error = "palette.i is required";
            return false;
        }

        if (!prop.TryGetInt32(out var value) || value is < 0 or > 255)
        {
            error = "palette.i must be 0-255";
            return false;
        }

        error = null;
        return true;
    }

    private static bool OptionalHostAppearance(JsonElement p, out string? error)
    {
        error = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("appearance", out var prop))
            return true;
        if (prop.ValueKind is JsonValueKind.Null)
            return true;
        if (prop.ValueKind != JsonValueKind.String)
        {
            error = "appearance must be dark or light";
            return false;
        }

        var raw = prop.GetString();
        if (string.Equals(raw, "dark", StringComparison.Ordinal)
            || string.Equals(raw, "light", StringComparison.Ordinal))
        {
            return true;
        }

        error = "appearance must be dark or light";
        return false;
    }

    private static bool OptionalPopupSize(JsonElement p, string name, out string? error)
    {
        error = null;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var prop))
            return true;

        if (prop.ValueKind == JsonValueKind.Number)
            return true;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = $"{name} must be cells or a percent string";
                return false;
            }

            raw = raw.Trim();
            if (raw.EndsWith('%'))
            {
                var body = raw[..^1].Trim();
                if (!int.TryParse(body, out var percent) || percent < 1 || percent > 100)
                {
                    error = $"{name} percent must be 1-100";
                    return false;
                }

                return true;
            }

            if (!int.TryParse(raw, out _))
            {
                error = $"{name} must be cells or a percent string";
                return false;
            }

            return true;
        }

        error = $"{name} must be cells or a percent string";
        return false;
    }

    private static bool RequireArray(JsonElement p, string name, out string? error)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var prop)
            || prop.ValueKind != JsonValueKind.Array)
        {
            error = $"{name} is required";
            return false;
        }

        error = null;
        return true;
    }
}
