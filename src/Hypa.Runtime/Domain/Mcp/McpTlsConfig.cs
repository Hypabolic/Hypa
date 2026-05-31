namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpTlsConfig(
    string? CaCertPath,
    string? ClientCertPath,
    string? ClientKeyPath);
