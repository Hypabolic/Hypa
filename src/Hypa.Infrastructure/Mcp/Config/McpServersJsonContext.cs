using System.Text.Json.Serialization;

namespace Hypa.Infrastructure.Mcp.Config;

[JsonSerializable(typeof(McpServersFileJson))]
[JsonSerializable(typeof(McpServerJson))]
[JsonSerializable(typeof(McpAuthJson))]
[JsonSerializable(typeof(McpTlsJson))]
[JsonSerializable(typeof(List<McpServerJson>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
internal sealed partial class McpServersJsonContext : JsonSerializerContext { }
