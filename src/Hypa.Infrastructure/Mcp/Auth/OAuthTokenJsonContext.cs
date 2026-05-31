using System.Text.Json.Serialization;

namespace Hypa.Infrastructure.Mcp.Auth;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(OAuthDeviceCodeResponse))]
[JsonSerializable(typeof(DeviceTokenStoreJson))]
internal sealed partial class OAuthTokenJsonContext : JsonSerializerContext;
