using System.Buffers;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// <c>src/api/mod.rs:22-84</c> UI-changing methods. Hypa names from
/// <see cref="ProtocolMethods"/>.
/// </summary>
public static class ProcessLogApi
{
    public static bool IsRoutine(string method) =>
        method is ProtocolMethods.PaneGet
            or ProtocolMethods.PaneRead
            or ProtocolMethods.PaneList
            or ProtocolMethods.WorkspaceList
            or ProtocolMethods.TabList
            or ProtocolMethods.PaneReportAgent
            or ProtocolMethods.PaneReportAgentSession
            or ProtocolMethods.PaneReportMetadata
            or ProtocolMethods.RuntimeHealth
            or ProtocolMethods.Ping
            or ProtocolMethods.TabGet
            or ProtocolMethods.WorkspaceGet
            or ProtocolMethods.PaneCurrent
            or ProtocolMethods.PaneNeighbor
            or ProtocolMethods.PaneEdges
            or ProtocolMethods.PaneProcessInfo
            or ProtocolMethods.AgentList
            or ProtocolMethods.AgentStatus
            or ProtocolMethods.AgentGet
            or ProtocolMethods.AgentRead
            or ProtocolMethods.SessionSnapshot
            or ProtocolMethods.EventsWait
            or ProtocolMethods.LayoutExport
            or ProtocolMethods.PluginList
            or ProtocolMethods.PluginLogList
            or ProtocolMethods.PluginActionList
            or ProtocolMethods.PluginResourceList
            or ProtocolMethods.PluginResourceGet
            or ProtocolMethods.PluginConfigGet
            or ProtocolMethods.IntegrationList
            or ProtocolMethods.WorktreeList
            or ProtocolMethods.ServerAgentManifests;

    public static bool ChangesUi(string method)
    {
        if (IsRoutine(method)
            && method is not (
                ProtocolMethods.PaneReportAgent
                or ProtocolMethods.PaneReportAgentSession
                or ProtocolMethods.PaneReportMetadata))
        {
            return false;
        }

        return method is ProtocolMethods.ServerReloadConfig
            or ProtocolMethods.ServerReloadAgentManifests
            or ProtocolMethods.NotificationShow
            or ProtocolMethods.WorkspaceCreate
            or ProtocolMethods.WorkspaceFocus
            or ProtocolMethods.WorkspaceRename
            or ProtocolMethods.WorkspaceMove
            or ProtocolMethods.WorkspaceMoveBlock
            or ProtocolMethods.WorkspaceReportMetadata
            or ProtocolMethods.WorkspaceClose
            or ProtocolMethods.WorktreeCreate
            or ProtocolMethods.WorktreeOpen
            or ProtocolMethods.WorktreeRemove
            or ProtocolMethods.TabCreate
            or ProtocolMethods.TabFocus
            or ProtocolMethods.TabRename
            or ProtocolMethods.TabMove
            or ProtocolMethods.TabClose
            or ProtocolMethods.LayoutApply
            or ProtocolMethods.LayoutSetSplitRatio
            or ProtocolMethods.AgentRename
            or ProtocolMethods.AgentViewSet
            or ProtocolMethods.AgentViewClear
            or ProtocolMethods.AgentFocus
            or ProtocolMethods.AgentStart
            or ProtocolMethods.AgentPrompt
            or ProtocolMethods.AgentSendKeys
            or ProtocolMethods.PaneSplit
            or ProtocolMethods.PaneSwap
            or ProtocolMethods.PaneMove
            or ProtocolMethods.PaneZoom
            or ProtocolMethods.PaneFocusDirection
            or ProtocolMethods.PaneResize
            or ProtocolMethods.PaneScroll
            or ProtocolMethods.PaneFocus
            or ProtocolMethods.PaneInputSet
            or ProtocolMethods.PaneRename
            or ProtocolMethods.PaneClose
            or ProtocolMethods.PaneShow
            or ProtocolMethods.PaneHide
            or ProtocolMethods.PaneCreate
            or ProtocolMethods.PopupClose
            or ProtocolMethods.PopupOpen
            or ProtocolMethods.PopupSendKeys
            or ProtocolMethods.PopupResize
            or ProtocolMethods.PluginUnlink
            or ProtocolMethods.PluginDisable
            or ProtocolMethods.PluginActionInvoke
            or ProtocolMethods.PluginPaneOpen
            or ProtocolMethods.PluginPaneFocus
            or ProtocolMethods.PluginPaneClose
            or ProtocolMethods.PaneReportAgent
            or ProtocolMethods.PaneReportAgentSession
            or ProtocolMethods.PaneReportMetadata
            or ProtocolMethods.PaneClearAgentAuthority
            or ProtocolMethods.PaneReleaseAgent
            or ProtocolMethods.UiClientMode
            or ProtocolMethods.ClientHostThemeSet
            or ProtocolMethods.ClientWindowTitleSet
            or ProtocolMethods.ClientWindowTitleClear;
    }

    public static ProcessLogLevel StartLevel(string method)
    {
        if (ChangesUi(method) && !IsRoutine(method))
            return ProcessLogLevel.Information;
        return ProcessLogLevel.Debug;
    }

    public static ProcessLogLevel CompleteLevel(string method, string outcome)
    {
        if (!string.Equals(outcome, ProcessLogEvents.OutcomeOk, StringComparison.Ordinal)
            || (ChangesUi(method) && !IsRoutine(method)))
        {
            return ProcessLogLevel.Information;
        }

        return ProcessLogLevel.Debug;
    }

    public static void WriteStart(
        IProcessLogSink sink,
        string? requestId,
        string method,
        string? attachClientId,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var level = StartLevel(method);
        if (!sink.IsEnabled(level))
            return;

        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.ApiRequestStart,
            Subsystem = ProcessLogEvents.SubsystemApi,
            Outcome = ProcessLogEvents.OutcomeStarted,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = level,
            SessionId = sessionId,
            RequestId = requestId,
            Method = method,
            ChangesUi = ChangesUi(method),
            AttachClientId = attachClientId,
        });
    }

    public static void WriteComplete(
        IProcessLogSink sink,
        string? requestId,
        string method,
        string outcome,
        string? attachClientId,
        string? sessionId,
        long? seq)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var level = CompleteLevel(method, outcome);
        if (!sink.IsEnabled(level))
            return;

        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.ApiRequestComplete,
            Subsystem = ProcessLogEvents.SubsystemApi,
            Outcome = outcome,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = level,
            SessionId = sessionId,
            RequestId = requestId,
            Method = method,
            ChangesUi = ChangesUi(method),
            AttachClientId = attachClientId,
            Seq = seq,
        });
    }

    public static void WriteFail(
        IProcessLogSink sink,
        string? requestId,
        string method,
        string err,
        string? attachClientId,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(ProcessLogLevel.Warning))
            return;

        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.ApiRequestFail,
            Subsystem = ProcessLogEvents.SubsystemApi,
            Outcome = ProcessLogEvents.OutcomeError,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Warning,
            SessionId = sessionId,
            RequestId = requestId,
            Method = method,
            ChangesUi = ChangesUi(method),
            AttachClientId = attachClientId,
            Err = err,
        });
    }

    /// <summary>
    /// Redact a fail message through the JSON string path, then store only the
    /// <c>err</c> text. Do not pass the envelope object to <see cref="WriteFail"/>.
    /// </summary>
    public static string RedactFailErr(IEventPayloadRedactor redactor, string? err)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var payload = EncodeErrObject(err ?? "");
        var redacted = redactor.RedactJsonPayload(ProcessLogEvents.ApiRequestFail, payload);
        if (TryReadErrField(redacted, out var message))
            return message;
        return "error";
    }

    internal static string EncodeErrObject(string err)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("err", err);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryReadErrField(string json, out string message)
    {
        message = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("err", out var el)
                || el.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            message = el.GetString() ?? "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
