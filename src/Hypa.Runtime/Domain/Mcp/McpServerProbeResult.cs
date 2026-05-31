namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpServerProbeResult(
    McpServerProbeStatus Status,
    string Message,
    McpAuthGuidance? AuthGuidance = null);
