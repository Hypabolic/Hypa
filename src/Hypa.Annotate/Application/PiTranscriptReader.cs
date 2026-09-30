using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Read the most recent visible assistant message from a Pi session JSONL file.
/// Supports only the Pi path-kind transcript layout used in tests.
/// </summary>
public static class PiTranscriptReader
{
    public static Result<string, string> ReadLastVisibleAssistantMessage(string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
            return Result<string, string>.Fail(AnnotateLastMessages.UnreadableTranscript);

        string? lastMessage = null;
        try
        {
            foreach (var line in File.ReadLines(transcriptPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseAssistantText(line, out var text))
                    continue;

                if (AnnotationParser.JavascriptTrim(text).Length == 0)
                    continue;

                lastMessage = text;
            }
        }
        catch (IOException)
        {
            return Result<string, string>.Fail(AnnotateLastMessages.UnreadableTranscript);
        }
        catch (UnauthorizedAccessException)
        {
            return Result<string, string>.Fail(AnnotateLastMessages.UnreadableTranscript);
        }

        return lastMessage is null
            ? Result<string, string>.Fail(AnnotateLastMessages.UnreadableTranscript)
            : Result<string, string>.Ok(lastMessage);
    }

    internal static bool TryParseAssistantText(string line, out string text)
    {
        text = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (IsSessionHeader(root))
                return false;

            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "message", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("role", out var role)
                || !string.Equals(role.GetString(), "assistant", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("text", out var textProperty)
                || textProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            text = textProperty.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSessionHeader(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && string.Equals(type.GetString(), "session", StringComparison.Ordinal);
}
