using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol.Envelopes;
using Hypa.AgentRuntime.Protocol.Json;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Bound one serialized RPC NDJSON line to the socket frame.
/// </summary>
public static class NdjsonRpcBudget
{
    /// <summary>Conservative id used when the request id is not known yet.</summary>
    public const string EstimateId = "id-estimate-xxxxxxxxxxxxxxxxxxxxxxxx";

    public static int Utf8LineBytes(string json) =>
        Encoding.UTF8.GetByteCount(json) + 1;

    public static bool LineFits(string json, int maxLineBytes) =>
        Utf8LineBytes(json) <= maxLineBytes;

    public static JsonObject FitPaneReadObject(JsonObject result, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var doc = JsonDocument.Parse(result.ToJsonString());
        if (!TryFitPaneRead(EstimateId, doc.RootElement, maxLineBytes, out var json))
            return result;

        using var fitted = JsonDocument.Parse(json);
        if (!fitted.RootElement.TryGetProperty("result", out var body)
            || body.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        return JsonNode.Parse(body.GetRawText()) as JsonObject ?? result;
    }

    public static bool TryFitPaneRead(
        string? id,
        JsonElement result,
        int maxLineBytes,
        out string json)
    {
        json = "";
        if (maxLineBytes < 1 || result.ValueKind != JsonValueKind.Object)
            return false;

        var obj = JsonObject.Create(result);
        if (obj is null)
            return false;

        if (TrySerialize(id, obj, maxLineBytes, out json))
            return true;

        obj.Remove("compressed");
        if (TrySerialize(id, obj, maxLineBytes, out json))
            return true;

        var text = "";
        if (obj["text"] is JsonValue textVal)
            _ = textVal.TryGetValue(out text);
        text ??= "";

        var lo = 0;
        var hi = text.Length;
        var bestJson = (string?)null;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            obj["text"] = SliceChars(text, mid);
            if (TrySerialize(id, obj, maxLineBytes, out var candidate))
            {
                bestJson = candidate;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (bestJson is null)
            return false;

        json = bestJson;
        return true;
    }

    private static bool TrySerialize(string? id, JsonObject result, int maxLineBytes, out string json)
    {
        using var doc = JsonDocument.Parse(result.ToJsonString());
        var response = new RpcResponse
        {
            Id = id,
            Result = doc.RootElement.Clone(),
        };
        json = JsonSerializer.Serialize(response, ProtocolJsonContext.Default.RpcResponse);
        return LineFits(json, maxLineBytes);
    }

    private static string SliceChars(string text, int length)
    {
        if (length >= text.Length)
            return text;
        if (length <= 0)
            return "";
        if (char.IsHighSurrogate(text[length - 1]))
            length--;
        return length <= 0 ? "" : text[..length];
    }
}
