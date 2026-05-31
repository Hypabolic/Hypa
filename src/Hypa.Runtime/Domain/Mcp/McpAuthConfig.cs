namespace Hypa.Runtime.Domain.Mcp;

public abstract record McpAuthConfig;

public sealed record NoneAuthConfig() : McpAuthConfig;

public sealed record BearerAuthConfig(string TokenRef) : McpAuthConfig;

public sealed record ApiKeyAuthConfig(
    string HeaderName,
    string ValueRef,
    bool InQueryString = false) : McpAuthConfig;

public sealed record BasicAuthConfig(
    string UsernameRef,
    string PasswordRef) : McpAuthConfig;

public sealed record OAuth2ClientCredentialsConfig(
    string TokenUrl,
    string ClientIdRef,
    string ClientSecretRef,
    string[]? Scopes = null) : McpAuthConfig;

public sealed record OAuth2DeviceCodeConfig(
    string AuthUrl,
    string TokenUrl,
    string ClientId,
    string[]? Scopes = null) : McpAuthConfig;

public sealed record MtlsConfig(
    string? ClientCertRef,
    string? ClientKeyRef) : McpAuthConfig;

public sealed record McpOAuthConfig(
    string? ClientId = null,
    string? ClientSecretRef = null,
    string[]? Scopes = null) : McpAuthConfig;

public sealed record UnknownAuthConfig(string Type) : McpAuthConfig;
