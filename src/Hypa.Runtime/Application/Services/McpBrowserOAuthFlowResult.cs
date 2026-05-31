using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed record McpBrowserOAuthFlowResult(
    bool Succeeded,
    McpOAuthConfig? CompletedConfig,
    int? ToolCount,
    string? Error = null);
