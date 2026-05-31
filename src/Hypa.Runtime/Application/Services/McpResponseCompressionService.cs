using System.Text.Json.Nodes;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed class McpResponseCompressionService
{
    public McpResult Compress(McpResult result, CompressionHint? hint)
    {
        if (result.IsError || hint == CompressionHint.Raw)
            return result;

        var compressed = hint == CompressionHint.Structured
            ? CompactJson(result.RawResponse.RawJson)
            : ExtractAndNormaliseText(result.RawResponse.RawJson);

        return result with { CompressedResponse = compressed };
    }

    private static string CompactJson(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            return node?.ToJsonString() ?? raw;
        }
        catch
        {
            return ExtractAndNormaliseText(raw);
        }
    }

    private static string ExtractAndNormaliseText(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is JsonArray arr)
            {
                var parts = arr
                    .OfType<JsonObject>()
                    .Where(o => o["type"]?.GetValue<string>() == "text")
                    .Select(o => o["text"]?.GetValue<string>() ?? string.Empty);

                var joined = string.Join('\n', parts);
                if (!string.IsNullOrWhiteSpace(joined))
                    return NormaliseWhitespace(joined);
            }
        }
        catch { }

        return NormaliseWhitespace(raw);
    }

    private static string NormaliseWhitespace(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (!string.IsNullOrWhiteSpace(trimmed))
                result.Add(trimmed);
        }

        return string.Join('\n', result).Trim();
    }
}
