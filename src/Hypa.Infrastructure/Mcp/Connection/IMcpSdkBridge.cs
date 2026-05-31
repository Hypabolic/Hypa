using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Connection;

// One-shot client façade used exclusively by the probe adapter.
// Owns the underlying McpClient and disposes it when finished.
internal interface IProbeClientFacade : IAsyncDisposable
{
    ValueTask<IList<McpClientTool>> ListToolsAsync(CancellationToken ct);
}

internal sealed class McpProbeFacade(McpClient client) : IProbeClientFacade
{
    public ValueTask<IList<McpClientTool>> ListToolsAsync(CancellationToken ct) =>
        client.ListToolsAsync(cancellationToken: ct);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal interface IMcpSdkBridge
{
    Task<McpClient> CreateClientAsync(
        IClientTransport transport,
        McpClientOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct);

    IClientTransport CreateStdioTransport(StdioClientTransportOptions options);
    IClientTransport CreateHttpTransport(HttpClientTransportOptions options, HttpClient? httpClient);

    // Creates and wraps a one-shot MCP client owned by the probe adapter.
    Task<IProbeClientFacade> CreateProbeClientAsync(
        IClientTransport transport,
        McpClientOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct);
}

internal sealed class McpSdkBridge : IMcpSdkBridge
{
    public Task<McpClient> CreateClientAsync(
        IClientTransport transport,
        McpClientOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct) =>
        McpClient.CreateAsync(transport, options, loggerFactory, ct);

    public IClientTransport CreateStdioTransport(StdioClientTransportOptions options) =>
        new StdioClientTransport(options);

    public IClientTransport CreateHttpTransport(HttpClientTransportOptions options, HttpClient? httpClient) =>
        httpClient is null
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, httpClient, null, ownsHttpClient: true);

    public async Task<IProbeClientFacade> CreateProbeClientAsync(
        IClientTransport transport,
        McpClientOptions options,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        var client = await McpClient.CreateAsync(transport, options, loggerFactory, ct);
        return new McpProbeFacade(client);
    }
}
