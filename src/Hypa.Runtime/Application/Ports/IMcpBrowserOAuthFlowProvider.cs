using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpBrowserOAuthFlowProvider
{
    Task<McpBrowserOAuthFlowResult> StartFlowAsync(
        string serverName,
        string endpoint,
        McpOAuthConfig config,
        McpBrowserOAuthOptions options,
        CancellationToken ct,
        IProgress<string>? progress = null);
}
