using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Auth;

internal interface IOAuthTokenService
{
    Task<string> GetClientCredentialsTokenAsync(OAuth2ClientCredentialsConfig config, CancellationToken ct);
    Task<string?> GetDeviceCodeTokenAsync(OAuth2DeviceCodeConfig config, CancellationToken ct);
}
