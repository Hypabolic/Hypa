using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Hand-written JSON Lines writer. Do not put log JSON in
/// <c>ProtocolJsonContext</c>.
/// </summary>
public static class ProcessLogJsonWriter
{
    public static byte[] WriteLine(ProcessLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", record.Event);
            writer.WriteString("subsystem", record.Subsystem);
            writer.WriteString("outcome", record.Outcome);
            writer.WriteString("ts", FormatTs(record.Ts));
            writer.WriteNumber("pid", record.Pid);
            WriteString(writer, "session_id", record.SessionId);
            WriteString(writer, "request_id", record.RequestId);
            WriteString(writer, "method", record.Method);
            if (record.ChangesUi is { } changesUi)
                writer.WriteBoolean("changes_ui", changesUi);
            WriteString(writer, "attach_client_id", record.AttachClientId);
            if (!string.IsNullOrEmpty(record.SessionId))
                WriteString(writer, "subscription_id", record.SubscriptionId);
            WriteString(writer, "err", record.Err);
            if (record.Seq is { } seq)
                writer.WriteNumber("seq", seq);
            WriteString(writer, "pane_id", record.PaneId);
            WriteString(writer, "tab_id", record.TabId);
            WriteString(writer, "workspace_id", record.WorkspaceId);
            WriteString(writer, "previous_tab_id", record.PreviousTabId);
            WriteString(writer, "current_tab_id", record.CurrentTabId);
            WriteString(writer, "surface", record.Surface);
            WriteString(writer, "setting_key", record.SettingKey);
            WriteString(writer, "setting_value", record.SettingValue);
            WriteString(writer, "value_kind", record.ValueKind);
            WriteString(writer, "digest", record.Digest);
            if (record.DropCount is { } drops)
                writer.WriteNumber("drop_count", drops);
            if (record.QueueDepth is { } depth)
                writer.WriteNumber("queue_depth", depth);
            WriteString(writer, "disconnect_reason", record.DisconnectReason);
            WriteString(writer, "action", record.Action);
            WriteString(writer, "source", record.Source);
            WriteString(writer, "stage", record.Stage);
            WriteString(writer, "endpoint_id", record.EndpointId);
            WriteString(writer, "transport", record.Transport);
            if (record.ElapsedMs is { } elapsedMs)
                writer.WriteNumber("elapsed_ms", elapsedMs);
            if (record.StageMs is { } stageMs)
                writer.WriteNumber("stage_ms", stageMs);
            WriteString(writer, "event_type", record.EventType);
            if (record.AgentSpecified)
            {
                if (string.IsNullOrEmpty(record.Agent))
                    writer.WriteNull("agent");
                else
                    writer.WriteString("agent", record.Agent);
            }

            WriteString(writer, "agent_status", record.AgentStatus);
            WriteString(writer, "previous_status", record.PreviousStatus);
            WriteString(writer, "reason", record.Reason);
            writer.WriteEndObject();
        }

        var json = buffer.WrittenSpan;
        var line = new byte[json.Length + 1];
        json.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    public static string FormatTs(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            writer.WriteString(name, value);
    }
}
