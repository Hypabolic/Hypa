using System.Buffers;
using System.Text.Json;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Writes a <c>terminal.output</c> payload as UTF-8. Matches
/// <see cref="RuntimeEventPayloadJson.WriteTerminalOutput"/> bytes.
/// Uses <see cref="Convert.TryToBase64Chars"/> then
/// <see cref="Utf8JsonWriter.WriteString(string, ReadOnlySpan{char})"/>.
/// Do not use <see cref="Utf8JsonWriter.WriteBase64String(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>:
/// that encoder does not escape <c>+</c> as <c>\u002B</c>.
/// </summary>
public static class TerminalOutputPayloadWriter
{
    public static bool TryWriteTerminalOutput(
        IBufferWriter<byte> destination,
        string paneId,
        ReadOnlySpan<byte> raw)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);

        var charCount = ((raw.Length + 2) / 3) * 4;
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(charCount, 1));
        try
        {
            if (!Convert.TryToBase64Chars(raw, chars.AsSpan(0, charCount), out var written)
                || written != charCount)
            {
                return false;
            }

            using var writer = new Utf8JsonWriter(destination);
            writer.WriteStartObject();
            writer.WriteString("pane_id"u8, paneId);
            writer.WriteString("encoding"u8, "base64"u8);
            writer.WriteString("data"u8, chars.AsSpan(0, written));
            writer.WriteNumber("byte_count"u8, raw.Length);
            writer.WriteEndObject();
            writer.Flush();
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars, clearArray: true);
        }
    }
}
