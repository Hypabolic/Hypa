using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Infrastructure.Mcp.Connection;

internal interface IMcpClientConnectionFactory
{
    Task<Result<IMcpClientFacade, McpProxyError>> GetOrCreateAsync(
        McpServerDefinition server,
        CancellationToken ct);

    Task InvalidateAsync(string serverName);
}
