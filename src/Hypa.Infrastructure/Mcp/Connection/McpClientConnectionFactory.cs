using System.Collections.Concurrent;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed class McpClientConnectionFactory : IMcpClientConnectionFactory, IAsyncDisposable
{
    private readonly McpTransportBuilder _transportBuilder;
    private readonly IMcpSdkBridge _sdk;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientConnectionFactory> _logger;
    private readonly ConcurrentDictionary<string, McpClientEntry> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public McpClientConnectionFactory(
        McpTransportBuilder transportBuilder,
        IMcpSdkBridge sdk,
        ILoggerFactory loggerFactory,
        ILogger<McpClientConnectionFactory> logger)
    {
        _transportBuilder = transportBuilder;
        _sdk = sdk;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<Result<IMcpClientFacade, McpProxyError>> GetOrCreateAsync(
        McpServerDefinition server,
        CancellationToken ct)
    {
        if (_cache.TryGetValue(server.Name, out var existing))
            return Result<IMcpClientFacade, McpProxyError>.Ok(new McpClientFacade(existing.Client));

        var sem = _locks.GetOrAdd(server.Name, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(server.Name, out existing))
                return Result<IMcpClientFacade, McpProxyError>.Ok(new McpClientFacade(existing.Client));

            return await CreateAndCacheAsync(server, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task InvalidateAsync(string serverName)
    {
        if (_cache.TryRemove(serverName, out var entry))
            await entry.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _cache.Keys.ToArray())
            await InvalidateAsync(key);
    }

    private async Task<Result<IMcpClientFacade, McpProxyError>> CreateAndCacheAsync(
        McpServerDefinition server,
        CancellationToken ct)
    {
        using var timeoutCts = server.ConnectTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
            timeoutCts.CancelAfter(server.ConnectTimeout!.Value);

        var connectCt = timeoutCts?.Token ?? ct;

        try
        {
            var transport = await _transportBuilder.BuildAsync(server, connectCt);
            var client = await _sdk.CreateClientAsync(
                transport,
                new McpClientOptions(),
                _loggerFactory,
                connectCt);

            _cache[server.Name] = new McpClientEntry(client, DateTimeOffset.UtcNow);
            return Result<IMcpClientFacade, McpProxyError>.Ok(new McpClientFacade(client));
        }
        catch (McpCredentialResolutionException ex)
        {
            _logger.LogWarning("Credential resolution failed for server '{Server}': {Message}",
                server.Name, ex.Message);
            return Result<IMcpClientFacade, McpProxyError>.Fail(new McpProxyError(
                McpErrorCodes.AuthRequired,
                $"Credential resolution failed for server '{server.Name}'.",
                server.Name));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Connection to MCP server '{Server}' timed out after {Timeout}",
                server.Name, server.ConnectTimeout);
            return Result<IMcpClientFacade, McpProxyError>.Fail(new McpProxyError(
                McpErrorCodes.Timeout,
                $"Connection to '{server.Name}' timed out.",
                server.Name));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create MCP client for server '{Server}'", server.Name);
            return Result<IMcpClientFacade, McpProxyError>.Fail(new McpProxyError(
                McpErrorCodes.ConnectionFailed,
                $"Failed to connect to server '{server.Name}'.",
                server.Name));
        }
    }
}
