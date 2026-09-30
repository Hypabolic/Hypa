using System.Text.Json;

namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginActionResultParser
{
    public static PluginStructuredActionResult? TryParse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var line = LastNonEmptyLine(stdout);
        if (line is null)
            return null;
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("status", out var statusEl)
                || statusEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var status = statusEl.GetString();
            if (string.IsNullOrWhiteSpace(status))
                return null;

            string? message = null;
            if (doc.RootElement.TryGetProperty("message", out var messageEl)
                && messageEl.ValueKind == JsonValueKind.String)
            {
                message = messageEl.GetString();
            }

            string? data = null;
            if (doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                data = dataEl.GetRawText();
            }

            return new PluginStructuredActionResult
            {
                Status = status,
                Message = message,
                DataJson = data,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? LastNonEmptyLine(string text)
    {
        var span = text.AsSpan();
        while (span.Length > 0)
        {
            var end = span.Length;
            var nl = span.LastIndexOfAny('\n', '\r');
            ReadOnlySpan<char> line;
            if (nl < 0)
            {
                line = span;
                span = ReadOnlySpan<char>.Empty;
            }
            else
            {
                line = span[(nl + 1)..end];
                span = span[..nl];
            }

            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.ToString();
        }

        return null;
    }
}
