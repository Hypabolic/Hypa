using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

/// <summary>
/// The end row (exclusive) of one render line, for render credit. The mux
/// charges each line with this value, and the client acks each line with the
/// same value, read from the same payload. The slices of one frame have one
/// generation and rising end rows.
/// </summary>
internal static class RenderRows
{
    /// <summary>A line or an ack that covers the whole generation.</summary>
    public const int All = int.MaxValue;

    /// <summary>One more than the highest row index of a cells payload, or 0 when it has no row.</summary>
    public static int CellsRowEnd(TerminalRenderCellsPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var end = 0;
        if (payload.Rows is { } rows)
        {
            foreach (var row in rows)
                end = Math.Max(end, row.I + 1);
        }

        return end;
    }

    /// <summary>The same value for a cells payload that is still JSON.</summary>
    public static int CellsRowEnd(JsonElement payload)
    {
        var end = 0;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("i", out var index)
                    && index.TryGetInt32(out var i))
                {
                    end = Math.Max(end, i + 1);
                }
            }
        }

        return end;
    }

    /// <summary>The <c>row_end</c> of a snapshot slice payload, or <see cref="All"/> when it has none.</summary>
    public static int SnapshotRowEnd(ReadOnlySpan<byte> payloadUtf8)
    {
        var reader = new Utf8JsonReader(payloadUtf8);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return All;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return All;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return All;
                var isRowEnd = reader.ValueTextEquals("row_end"u8);
                if (!reader.Read())
                    return All;
                if (isRowEnd)
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var end) ? end : All;
                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return All;
    }
}
