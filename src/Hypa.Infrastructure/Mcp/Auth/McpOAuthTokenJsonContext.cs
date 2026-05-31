using System.Text.Json.Serialization;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed record McpOAuthTokenFileJson(
    int Version,
    Dictionary<string, McpOAuthTokenEntryJson> Tokens);

internal sealed record McpOAuthTokenEntryJson(
    string TokenType,
    string AccessToken,
    string? RefreshToken,
    int? ExpiresIn,
    string ObtainedAt,
    string? Scope,
    string? DcrClientId = null,
    string? DcrClientSecret = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpOAuthTokenFileJson))]
[JsonSerializable(typeof(McpOAuthTokenEntryJson))]
[JsonSerializable(typeof(Dictionary<string, McpOAuthTokenEntryJson>))]
internal sealed partial class McpOAuthTokenJsonContext : JsonSerializerContext;
