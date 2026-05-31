using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpDispatcher
{
    Task<McpResult> InvokeAsync(McpProxyRequest request, CancellationToken ct);
    Task<IReadOnlyList<McpResult>> InvokeBatchAsync(IReadOnlyList<McpProxyRequest> requests, CancellationToken ct);
    Task<McpSchemaManifest> GetSchemaAsync(CancellationToken ct);
    Task<IReadOnlyList<McpToolSearchResult>> SearchToolsAsync(string query, CancellationToken ct);
}
