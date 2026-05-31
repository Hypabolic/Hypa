namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpResult(
    string ServerName,
    string ToolName,
    JsonPayload RawResponse,
    string CompressedResponse,
    McpLatencyMetadata Latency,
    bool IsError,
    McpProxyError? Error);
