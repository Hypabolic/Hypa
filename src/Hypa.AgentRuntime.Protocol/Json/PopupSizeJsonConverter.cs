using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Protocol.Json;

/// <summary>
/// Wire: JSON number is cells. JSON string <c>"80%"</c> is percent 1–100.
/// </summary>
public sealed class PopupSizeJsonConverter : JsonConverter<PopupSize?>
{
    public override PopupSize? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (!reader.TryGetInt32(out var cells))
                    throw new JsonException("width/height cells must be an integer.");
                return PopupSize.Cells(cells);
            case JsonTokenType.String:
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    throw new JsonException("width/height must be cells or a percent string.");
                raw = raw.Trim();
                if (raw.EndsWith('%'))
                {
                    var body = raw[..^1].Trim();
                    if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                        || percent < 1
                        || percent > 100)
                    {
                        throw new JsonException("percent width/height must be 1-100.");
                    }

                    return PopupSize.Percent(percent);
                }

                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCells))
                    throw new JsonException("width/height must be cells or a percent string.");
                return PopupSize.Cells(parsedCells);
            default:
                throw new JsonException("width/height must be a number or percent string.");
        }
    }

    public override void Write(Utf8JsonWriter writer, PopupSize? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsPercent)
        {
            writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"{value.Value}%"));
            return;
        }

        writer.WriteNumberValue(value.Value);
    }
}
