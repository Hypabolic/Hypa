namespace Hypa.Infrastructure.Mcp.Config;

internal sealed record McpServersFileJson(IReadOnlyList<McpServerJson>? Servers);

internal sealed record McpServerJson(
    string? Name,
    string? Transport,
    string? Endpoint,
    McpAuthJson? Auth,
    McpTlsJson? Tls,
    int? ConnectTimeoutSeconds,
    int? RequestTimeoutSeconds);

internal sealed record McpAuthJson(
    string? Type,
    string? TokenRef,
    string? HeaderName,
    string? ValueRef,
    bool? InQueryString,
    string? UsernameRef,
    string? PasswordRef,
    string? TokenUrl,
    string? ClientIdRef,
    string? ClientSecretRef,
    string? AuthUrl,
    string? ClientId,
    string[]? Scopes,
    string? ClientCertRef,
    string? ClientKeyRef);

internal sealed record McpTlsJson(
    string? CaCertPath,
    string? ClientCertPath,
    string? ClientKeyPath);
