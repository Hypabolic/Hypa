namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpProxyRequest(
    string ServerName,
    string ToolName,
    JsonPayload Arguments,
    CompressionHint? CompressionHint = null);
