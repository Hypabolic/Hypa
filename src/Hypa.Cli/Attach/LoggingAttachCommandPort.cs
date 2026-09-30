using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>Logs tab, overlay, and rejected UI RPCs. Does not write settings applied.</summary>
internal sealed class LoggingAttachCommandPort : IAttachCommandPort
{
    private readonly IAttachCommandPort _inner;
    private readonly AttachLiveState _live;

    public LoggingAttachCommandPort(IAttachCommandPort inner, AttachLiveState live)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _live = live ?? throw new ArgumentNullException(nameof(live));
    }

    public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var tabAction = AttachProcessLog.TabAction(method);
        var previousTab = tabAction == "focus" ? CurrentTabId() : null;
        var tabId = ReadString(parameters, "tab_id") ?? previousTab;
        var overlaySurface = OverlaySurface(method, parameters);
        if (overlaySurface is not null)
        {
            AttachProcessLog.OverlayRequested(
                _live.ProcessLog,
                overlaySurface,
                _live.SessionName,
                _live.AttachClientId);
        }

        var tracked = await CallTrackedAsync(method, parameters, ct).ConfigureAwait(false);
        var requestId = tracked.RequestId;
        if (tracked.Error is not null)
        {
            if (tabAction.Length > 0)
            {
                AttachProcessLog.TabRequested(
                    _live.ProcessLog,
                    tabAction,
                    _live.SessionName,
                    _live.AttachClientId,
                    tabId,
                    _live.WorkspaceId,
                    previousTab,
                    tabAction == "focus" ? tabId : previousTab,
                    requestId,
                    _live.UiActionSource);
                AttachProcessLog.TabOutcome(
                    _live.ProcessLog,
                    tabAction,
                    ProcessLogEvents.OutcomeRejected,
                    _live.SessionName,
                    _live.AttachClientId,
                    tabId,
                    _live.WorkspaceId,
                    previousTab,
                    tabId,
                    requestId,
                    _live.UiActionSource);
            }

            if (overlaySurface is not null)
            {
                AttachProcessLog.OverlayOutcome(
                    _live.ProcessLog,
                    overlaySurface,
                    ProcessLogEvents.OutcomeRejected,
                    _live.SessionName,
                    _live.AttachClientId);
            }

            ExceptionDispatchInfo.Capture(tracked.Error).Throw();
            return default;
        }

        var result = tracked.Result;
        if (tabAction.Length > 0)
        {
            var current = tabAction == "focus"
                ? tabId
                : ReadResultString(result, "tab_id") ?? tabId;
            AttachProcessLog.TabRequested(
                _live.ProcessLog,
                tabAction,
                _live.SessionName,
                _live.AttachClientId,
                tabId,
                _live.WorkspaceId,
                previousTab,
                tabAction == "focus" ? tabId : previousTab,
                requestId,
                _live.UiActionSource);
            AttachProcessLog.TabOutcome(
                _live.ProcessLog,
                tabAction,
                ProcessLogEvents.OutcomeOk,
                _live.SessionName,
                _live.AttachClientId,
                current,
                _live.WorkspaceId,
                previousTab,
                current,
                requestId,
                _live.UiActionSource);
        }

        if (overlaySurface is not null)
        {
            var outcome = IsOverlayOpen(overlaySurface)
                ? ProcessLogEvents.OutcomeOpened
                : ProcessLogEvents.OutcomeClosed;
            AttachProcessLog.OverlayOutcome(
                _live.ProcessLog,
                overlaySurface,
                outcome,
                _live.SessionName,
                _live.AttachClientId);
        }

        return result;
    }

    private string? CurrentTabId() =>
        _live.TabId ?? _live.Dispatcher?.TabId;

    private async Task<ControlPlaneCallResult> CallTrackedAsync(
        string method,
        JsonObject? parameters,
        CancellationToken ct)
    {
        if (_inner is ControlPlaneAttachCommandPort port)
        {
            try
            {
                return await port.CallWithRequestIdAsync(method, parameters, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Pre-id failures throw before ControlPlaneCallResult.Error is set.
                return new ControlPlaneCallResult(default, null, ex);
            }
        }

        try
        {
            var result = await _inner.CallAsync(method, parameters, ct).ConfigureAwait(false);
            return new ControlPlaneCallResult(result, null, null);
        }
        catch (Exception ex)
        {
            return new ControlPlaneCallResult(default, null, ex);
        }
    }

    private static string? OverlaySurface(string method, JsonObject? parameters)
    {
        if (!string.Equals(method, ProtocolMethods.UiClientMode, StringComparison.Ordinal))
            return null;
        return ReadString(parameters, "client_mode");
    }

    private static bool IsOverlayOpen(string surface)
    {
        var token = surface.Trim().ToLowerInvariant();
        return token is not "terminal" and not "prefix";
    }

    private static string? ReadString(JsonObject? parameters, string name)
    {
        if (parameters is null || !parameters.TryGetPropertyValue(name, out var node))
            return null;
        return node?.GetValue<string>();
    }

    private static string? ReadResultString(JsonElement result, string name)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out var el))
            return null;
        return el.GetString();
    }
}
