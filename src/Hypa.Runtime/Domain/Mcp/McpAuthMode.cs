namespace Hypa.Runtime.Domain.Mcp;

public enum McpAuthMode
{
    None,
    Bearer,
    ApiKey,
    Basic,
    OAuth2ClientCredentials,
    OAuth2DeviceCode,
    Mtls,
    McpOAuth,
}
