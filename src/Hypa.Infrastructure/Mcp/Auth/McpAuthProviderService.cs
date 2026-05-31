using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class McpAuthProviderService : IMcpAuthProvider
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    private readonly ISecretResolver _secretResolver;
    private readonly IOAuthTokenService _oauthTokenService;
    private readonly SecretRedactionRegistry _redactionRegistry;
    private readonly ILogger<McpAuthProviderService> _logger;

    public McpAuthProviderService(
        ISecretResolver secretResolver,
        IOAuthTokenService oauthTokenService,
        SecretRedactionRegistry redactionRegistry,
        ILogger<McpAuthProviderService> logger)
    {
        _secretResolver = secretResolver;
        _oauthTokenService = oauthTokenService;
        _redactionRegistry = redactionRegistry;
        _logger = logger;
    }

    public async ValueTask<McpAuthContext> GetAuthContextAsync(McpServerDefinition server, CancellationToken ct)
    {
        return server.Auth switch
        {
            NoneAuthConfig => new McpAuthContext(EmptyHeaders),

            BearerAuthConfig bearer => await ResolveBearerAsync(bearer, ct),

            ApiKeyAuthConfig apiKey => await ResolveApiKeyAsync(apiKey, ct),

            BasicAuthConfig basic => await ResolveBasicAsync(basic, ct),

            OAuth2ClientCredentialsConfig clientCreds => await ResolveClientCredentialsAsync(clientCreds, ct),

            OAuth2DeviceCodeConfig deviceCode => await ResolveDeviceCodeAsync(deviceCode, ct),

            MtlsConfig mtls => await ResolveMtlsAsync(mtls, ct),

            UnknownAuthConfig unknown => HandleUnknown(unknown),

            _ => new McpAuthContext(EmptyHeaders),
        };
    }

    private async ValueTask<McpAuthContext> ResolveBearerAsync(BearerAuthConfig config, CancellationToken ct)
    {
        var token = await _secretResolver.ResolveAsync(config.TokenRef, ct)
            ?? throw new McpCredentialResolutionException(
                $"Secret reference '{config.TokenRef}' resolved to null. Verify the secret is set.");
        _redactionRegistry.Register(token);
        return new McpAuthContext(
            Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" });
    }

    private async ValueTask<McpAuthContext> ResolveApiKeyAsync(ApiKeyAuthConfig config, CancellationToken ct)
    {
        var value = await _secretResolver.ResolveAsync(config.ValueRef, ct)
            ?? throw new McpCredentialResolutionException(
                $"Secret reference '{config.ValueRef}' resolved to null. Verify the secret is set.");
        _redactionRegistry.Register(value);

        if (config.InQueryString)
        {
            return new McpAuthContext(
                Headers: EmptyHeaders,
                QueryParameters: new Dictionary<string, string> { [config.HeaderName] = value });
        }

        return new McpAuthContext(
            Headers: new Dictionary<string, string> { [config.HeaderName] = value });
    }

    private async ValueTask<McpAuthContext> ResolveBasicAsync(BasicAuthConfig config, CancellationToken ct)
    {
        var username = await _secretResolver.ResolveAsync(config.UsernameRef, ct)
            ?? throw new McpCredentialResolutionException(
                $"Secret reference '{config.UsernameRef}' resolved to null. Verify the secret is set.");
        var password = await _secretResolver.ResolveAsync(config.PasswordRef, ct)
            ?? throw new McpCredentialResolutionException(
                $"Secret reference '{config.PasswordRef}' resolved to null. Verify the secret is set.");
        _redactionRegistry.Register(password);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new McpAuthContext(
            Headers: new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" },
            Username: username,
            Password: password);
    }

    private async ValueTask<McpAuthContext> ResolveClientCredentialsAsync(
        OAuth2ClientCredentialsConfig config, CancellationToken ct)
    {
        var token = await _oauthTokenService.GetClientCredentialsTokenAsync(config, ct);
        _redactionRegistry.Register(token);
        return new McpAuthContext(
            Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" });
    }

    private async ValueTask<McpAuthContext> ResolveDeviceCodeAsync(
        OAuth2DeviceCodeConfig config, CancellationToken ct)
    {
        var token = await _oauthTokenService.GetDeviceCodeTokenAsync(config, ct);
        if (token is null)
        {
            return new McpAuthContext(
                Headers: EmptyHeaders,
                BearerToken: null);
        }

        _redactionRegistry.Register(token);
        return new McpAuthContext(
            Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            BearerToken: token);
    }

    private async ValueTask<McpAuthContext> ResolveMtlsAsync(MtlsConfig config, CancellationToken ct)
    {
        var certPath = config.ClientCertRef is not null
            ? await _secretResolver.ResolveAsync(config.ClientCertRef, ct)
            : null;
        var keyPath = config.ClientKeyRef is not null
            ? await _secretResolver.ResolveAsync(config.ClientKeyRef, ct)
            : null;

        return new McpAuthContext(
            Headers: EmptyHeaders,
            ClientCertificatePath: certPath,
            ClientKeyPath: keyPath);
    }

    private McpAuthContext HandleUnknown(UnknownAuthConfig config)
    {
        _logger.LogWarning("Unknown auth type '{Type}' — returning empty auth context", config.Type);
        return new McpAuthContext(EmptyHeaders);
    }
}
