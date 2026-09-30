using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// AOT-safe source-generated helpers for HYJR stored envelopes and pane_id extraction.
/// </summary>
public static class HyjrPayloadJson
{
    /// <summary>
    /// Typed opaque blob stored when the caller payload is not a JSON value.
    /// Never contains the original secret bytes.
    /// </summary>
    public const string OpaqueRedactedPayloadJson =
        "{\"opaque\":true,\"kind\":\"redacted_payload\",\"encoding\":\"utf8\",\"data\":\"[REDACTED]\"}";

    /// <summary>UTF-8 bytes of <see cref="OpaqueRedactedPayloadJson"/>.</summary>
    public static ReadOnlySpan<byte> OpaqueRedactedPayloadUtf8 => OpaqueRedactedPayloadBytes;

    /// <summary>Owned UTF-8 bytes of <see cref="OpaqueRedactedPayloadJson"/>.</summary>
    public static readonly byte[] OpaqueRedactedPayloadBytes =
        Encoding.UTF8.GetBytes(OpaqueRedactedPayloadJson);

    /// <summary>
    /// Max stored payload JSON bytes. Fits base64 of 64 KiB raw terminal bytes plus wrapper.
    /// </summary>
    public const int MaxPayloadBytes = 96 * 1024;

    public static bool IsJsonValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Same acceptance set as <see cref="IsJsonValue(string)"/>. Allocates nothing.
    /// </summary>
    public static bool IsJsonValue(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return false;

        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read())
                return false;
            if (!reader.TrySkip())
                return false;
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Write <c>{type, occurred_at, payload}</c> into <paramref name="destination"/>.
    /// Uses the same <c>yyyy-MM-ddTHH:mm:ssZ</c> timestamp as
    /// <see cref="WrapStoredPayload"/>. Non-JSON payloads splice
    /// <see cref="OpaqueRedactedPayloadUtf8"/>.
    /// </summary>
    public static bool TryWriteStoredPayload(
        IBufferWriter<byte> destination,
        string type,
        DateTimeOffset occurredAt,
        ReadOnlySpan<byte> payloadUtf8)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        ReadOnlySpan<byte> payload = IsJsonValue(payloadUtf8)
            ? payloadUtf8
            : OpaqueRedactedPayloadUtf8;

        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteString("type"u8, type);
        writer.WriteString(
            "occurred_at"u8,
            occurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        writer.WritePropertyName("payload"u8);
        writer.WriteRawValue(payload, skipInputValidation: true);
        writer.WriteEndObject();
        writer.Flush();
        return true;
    }

    /// <summary>
    /// Keep a JSON value as-is. Replace non-JSON text with a typed opaque object.
    /// </summary>
    public static string EnsureJsonPayload(string payloadJson) =>
        IsJsonValue(payloadJson) ? payloadJson : OpaqueRedactedPayloadJson;

    public static string WrapStoredPayload(string type, DateTimeOffset occurredAt, string payloadJson)
    {
        payloadJson = EnsureJsonPayload(payloadJson);
        using var doc = JsonDocument.Parse(payloadJson);
        var envelope = new HyjrStoredEnvelope
        {
            Type = type,
            OccurredAt = occurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Payload = doc.RootElement.Clone(),
        };
        return JsonSerializer.Serialize(envelope, HyjrPayloadJsonContext.Default.HyjrStoredEnvelope);
    }

    public static bool TryUnwrapStoredPayload(
        string storedJson,
        out string type,
        out DateTimeOffset occurredAt,
        out string payloadJson)
    {
        type = "unknown";
        occurredAt = DateTimeOffset.UnixEpoch;
        payloadJson = "{}";

        try
        {
            var env = JsonSerializer.Deserialize(storedJson, HyjrPayloadJsonContext.Default.HyjrStoredEnvelope);
            if (env is null)
                return false;

            if (!string.IsNullOrEmpty(env.Type))
                type = env.Type;

            if (!string.IsNullOrEmpty(env.OccurredAt) &&
                DateTimeOffset.TryParse(env.OccurredAt, out var oa))
            {
                occurredAt = oa;
            }

            if (env.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                payloadJson = env.Payload.GetRawText();
            else if (env.Payload.ValueKind == JsonValueKind.Undefined)
                payloadJson = "{}";
            else
                payloadJson = env.Payload.GetRawText();

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? TryGetPaneId(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize(payloadJson, HyjrPayloadJsonContext.Default.PaneIdPayload);
            return string.IsNullOrEmpty(dto?.PaneId) ? null : dto.PaneId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Render seq/observe key. Popup overlays use <c>popup</c> and have no pane id.
    /// </summary>
    public static string? TryGetRenderKey(string payloadJson)
    {
        var target = TryGetStringField(payloadJson, "target");
        if (string.Equals(target, "popup", StringComparison.Ordinal))
            return "popup";
        return TryGetPaneId(payloadJson);
    }

    public static bool IsPopupTarget(string payloadJson) =>
        string.Equals(TryGetStringField(payloadJson, "target"), "popup", StringComparison.Ordinal);

    public static string? TryGetStringField(string payloadJson, string name)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(name))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty(name, out var prop))
                return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Stored HYJR payload wrap: {type, occurred_at, payload}.</summary>
public sealed record HyjrStoredEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("occurred_at")]
    public string? OccurredAt { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>Minimal payload shape for observe routing.</summary>
public sealed record PaneIdPayload
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

[JsonSerializable(typeof(HyjrStoredEnvelope))]
[JsonSerializable(typeof(PaneIdPayload))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class HyjrPayloadJsonContext : JsonSerializerContext;
