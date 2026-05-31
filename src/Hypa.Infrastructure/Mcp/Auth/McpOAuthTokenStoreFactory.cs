using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class McpOAuthTokenStoreFactory
{
    private readonly string _storagePath;
    private readonly SecretRedactionRegistry _redactionRegistry;
    private readonly ILogger<McpOAuthTokenStore> _logger;

    public McpOAuthTokenStoreFactory(
        string storagePath,
        SecretRedactionRegistry redactionRegistry,
        ILogger<McpOAuthTokenStore> logger)
    {
        _storagePath = storagePath;
        _redactionRegistry = redactionRegistry;
        _logger = logger;
    }

    public McpOAuthTokenStore For(string serverName) =>
        new(serverName, _storagePath, _redactionRegistry, _logger);
}
