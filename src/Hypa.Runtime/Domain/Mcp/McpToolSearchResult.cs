namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpToolSearchResult(
    string ServerName,
    string ToolName,
    string Description,
    double Score);
