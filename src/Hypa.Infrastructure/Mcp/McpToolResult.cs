using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Hypa.Infrastructure.Mcp;

internal static class McpToolResult
{
    /// <summary>
    /// Builds a canonical UTF-8 JSON object from a flat set of key/value pairs.
    /// Null values are omitted. Uses Utf8JsonWriter — AOT-safe, no reflection.
    /// </summary>
    internal static string BuildArgsJson(params (string Key, string? Value)[] pairs)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var (key, value) in pairs)
        {
            if (value is not null)
                writer.WriteString(key, value);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }


    internal static CallToolResult Ok(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }]
    };

    internal static CallToolResult Err(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    internal static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
