using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Secrets;

internal sealed class EnvironmentSecretResolver : ISecretResolver
{
    private readonly McpOAuthTokenStoreFactory _tokenStoreFactory;
    private readonly ILogger<EnvironmentSecretResolver> _logger;

    public EnvironmentSecretResolver(
        McpOAuthTokenStoreFactory tokenStoreFactory,
        ILogger<EnvironmentSecretResolver> logger)
    {
        _tokenStoreFactory = tokenStoreFactory;
        _logger = logger;
    }

    public async ValueTask<string?> ResolveAsync(string reference, CancellationToken ct)
    {
        if (reference.StartsWith("env:", StringComparison.Ordinal))
        {
            var varName = reference["env:".Length..];
            return Environment.GetEnvironmentVariable(varName);
        }

        if (reference.StartsWith("file:", StringComparison.Ordinal))
        {
            var filePath = reference["file:".Length..];
            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                return content.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Failed to read secret file {Path}: {Message}", filePath, ex.Message);
                return null;
            }
        }

        if (reference.StartsWith("hypa:dcr:", StringComparison.Ordinal))
        {
            var serverName = reference["hypa:dcr:".Length..];
            try
            {
                var store = _tokenStoreFactory.For(serverName);
                var (_, secret) = await store.GetDcrCredentialsAsync(ct);
                return secret;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Failed to resolve DCR credential for server {Server}: {Message}", serverName, ex.Message);
                return null;
            }
        }

        return reference;
    }
}
