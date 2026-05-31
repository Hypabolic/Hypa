namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpProxyError(
    string Code,
    string Message,
    string? ServerName = null,
    string? ToolName = null);
