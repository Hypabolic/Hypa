namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpAuthContext(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string>? QueryParameters = null,
    string? BearerToken = null,
    string? Username = null,
    string? Password = null,
    string? ClientCertificatePath = null,
    string? ClientKeyPath = null);
