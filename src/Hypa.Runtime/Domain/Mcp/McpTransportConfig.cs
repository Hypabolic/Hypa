namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpTransportConfig(McpTransportKind Kind, string? Endpoint);
