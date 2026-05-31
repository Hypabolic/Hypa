using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed record McpClientEntry(McpClient Client, DateTimeOffset CreatedAt) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}
