using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Json;

/// <summary>
/// Accepts live JSON number major (1) or legacy string Wire ("1.0") when reading
/// <c>protocol_version</c>. Always writes JSON number <see cref="Protocol.ProtocolVersion.Current"/>.
/// Used where a flexible int property is needed outside source-gen records.
/// </summary>
public sealed class ProtocolVersionMajorJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
            return n;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrEmpty(s))
                throw new JsonException("protocol_version string is empty.");

            // "1.0" or "1" → major 1
            var majorPart = s.Split('.')[0];
            if (int.TryParse(majorPart, out var major))
                return major;

            throw new JsonException($"protocol_version string '{s}' is not a valid major[.minor].");
        }

        throw new JsonException($"protocol_version must be number or string, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
