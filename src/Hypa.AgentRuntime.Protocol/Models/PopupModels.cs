using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Protocol.Json;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// Cells or a 1–100 percent of the attach content area.
/// JSON number is cells. JSON string <c>"80%"</c> is percent.
/// </summary>
public sealed record PopupSize
{
    public required bool IsPercent { get; init; }

    public required int Value { get; init; }

    public static PopupSize Cells(int value) => new() { IsPercent = false, Value = value };

    public static PopupSize Percent(int value) => new() { IsPercent = true, Value = value };

    public static bool TryRead(JsonElement element, out PopupSize? size)
    {
        size = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetInt32(out var cells))
                    return false;
                size = Cells(cells);
                return true;
            case JsonValueKind.String:
                var raw = element.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return false;
                raw = raw.Trim();
                if (raw.EndsWith('%'))
                {
                    var body = raw[..^1].Trim();
                    if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                        || percent < 1
                        || percent > 100)
                    {
                        return false;
                    }

                    size = Percent(percent);
                    return true;
                }

                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return false;
                size = Cells(parsed);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
// / <c>popup.open</c> params.
/// Thin client; same rationale as <c>pane.create</c>.
/// </summary>
public sealed record PopupOpenParams
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    [JsonPropertyName("width")]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Height { get; init; }

    [JsonPropertyName("area_cols")]
    public int? AreaCols { get; init; }

    [JsonPropertyName("area_rows")]
    public int? AreaRows { get; init; }

    /// <summary>
    /// Attach client mode at the call site. Non-terminal modes return <c>ui_busy</c>.
    /// </summary>
    [JsonPropertyName("client_mode")]
    public string? ClientMode { get; init; }
}

/// <summary><c>popup.open</c> result. No <c>pane_id</c>. <c>cols</c>/<c>rows</c> are inner PTY cells.</summary>
public sealed record PopupOpenResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("outer_cols")]
    public int? OuterCols { get; init; }

    [JsonPropertyName("outer_rows")]
    public int? OuterRows { get; init; }
}

/// <summary><c>popup.close</c> result.</summary>
public sealed record PopupCloseResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }
}

/// <summary><c>popup.send_keys</c> params. No <c>pane_id</c>. Input is lease-scoped.</summary>
public sealed record PopupSendKeysParams
{
    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>Controller input lease. Same gate as <c>pane.send_keys</c>.</summary>
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>popup.send_keys</c> result.</summary>
public sealed record PopupSendKeysResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("accepted_bytes")]
    public required int AcceptedBytes { get; init; }
}

/// <summary><c>popup.resize</c> params. Recomputes clamped geometry from the content area.</summary>
public sealed record PopupResizeParams
{
    [JsonPropertyName("area_cols")]
    public int? AreaCols { get; init; }

    [JsonPropertyName("area_rows")]
    public int? AreaRows { get; init; }
}

/// <summary><c>popup.resize</c> result. Inner PTY size after clamp.</summary>
public sealed record PopupResizeResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("outer_cols")]
    public int? OuterCols { get; init; }

    [JsonPropertyName("outer_rows")]
    public int? OuterRows { get; init; }
}

/// <summary>
/// Additive <c>session.snapshot.popup</c> when a popup is open.
/// Omitted when closed so live_keys compatibility stays exact.
/// </summary>
public sealed record SessionPopupSnapshot
{
    [JsonPropertyName("open")]
    public required bool Open { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("outer_cols")]
    public int? OuterCols { get; init; }

    [JsonPropertyName("outer_rows")]
    public int? OuterRows { get; init; }

    [JsonPropertyName("area_cols")]
    public int? AreaCols { get; init; }

    [JsonPropertyName("area_rows")]
    public int? AreaRows { get; init; }

    [JsonPropertyName("width")]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonConverter(typeof(PopupSizeJsonConverter))]
    public PopupSize? Height { get; init; }
}
