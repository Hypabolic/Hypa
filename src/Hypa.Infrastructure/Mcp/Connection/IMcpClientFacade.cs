using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hypa.Infrastructure.Mcp.Connection;

internal interface IMcpClientFacade
{
    ValueTask<IList<McpClientTool>> ListToolsAsync(CancellationToken ct);
    ValueTask<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct);
}

internal sealed class McpClientFacade(McpClient client) : IMcpClientFacade
{
    public ValueTask<IList<McpClientTool>> ListToolsAsync(CancellationToken ct) =>
        client.ListToolsAsync(cancellationToken: ct);

    public ValueTask<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct) =>
        client.CallToolAsync(toolName, arguments, cancellationToken: ct);
}
