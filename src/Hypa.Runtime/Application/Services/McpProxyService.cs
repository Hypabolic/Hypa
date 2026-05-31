using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Services;

public sealed class McpProxyService(
    IMcpDispatcher dispatcher,
    McpResponseCompressionService compression,
    McpToolSearchIndex searchIndex,
    IClock clock)
{
    public async Task<McpResult> InvokeAsync(McpProxyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerName))
            return InvalidRequest(request, "ServerName is required.");

        if (string.IsNullOrWhiteSpace(request.ToolName))
            return InvalidRequest(request, "ToolName is required.");

        var result = await dispatcher.InvokeAsync(request, ct);
        return compression.Compress(result, request.CompressionHint);
    }

    public async Task<IReadOnlyList<McpResult>> InvokeBatchAsync(
        IReadOnlyList<McpProxyRequest> requests,
        CancellationToken ct)
    {
        var tasks = requests.Select(r => InvokeAsync(r, ct));
        return await Task.WhenAll(tasks);
    }

    public Task<McpSchemaManifest> GetSchemaAsync(CancellationToken ct) =>
        dispatcher.GetSchemaAsync(ct);

    public async Task<IReadOnlyList<McpToolSearchResult>> SearchToolsAsync(
        string query,
        CancellationToken ct)
    {
        var manifest = await dispatcher.GetSchemaAsync(ct);
        return searchIndex.Search(manifest, query);
    }

    private McpResult InvalidRequest(McpProxyRequest request, string message) =>
        new(
            request.ServerName,
            request.ToolName,
            new JsonPayload("{}"),
            string.Empty,
            new McpLatencyMetadata(clock.UtcNow, TimeSpan.Zero),
            IsError: true,
            new McpProxyError(McpErrorCodes.InvalidRequest, message, request.ServerName, request.ToolName));
}
