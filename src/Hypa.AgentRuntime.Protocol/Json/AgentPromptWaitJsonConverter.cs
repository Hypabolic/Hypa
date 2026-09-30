using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Protocol.Json;

/// <summary>
/// Accepts nested <c>wait: { until, timeout_ms }</c> or legacy <c>wait: true|false</c>
/// (until protocol major 2). Writes an object or null.
/// </summary>
public sealed class AgentPromptWaitJsonConverter : JsonConverter<AgentWaitSpec?>
{
    public override AgentWaitSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.False:
                return null;
            case JsonTokenType.True:
                return new AgentWaitSpec();
            case JsonTokenType.StartObject:
                return JsonSerializer.Deserialize(ref reader, ProtocolJsonContext.Default.AgentWaitSpec);
            default:
                throw new JsonException("wait must be a bool or object.");
        }
    }

    public override void Write(Utf8JsonWriter writer, AgentWaitSpec? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.AgentWaitSpec);
    }
}
