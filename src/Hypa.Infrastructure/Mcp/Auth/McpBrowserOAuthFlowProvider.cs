using Hypa.Infrastructure.Mcp.Connection;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class McpBrowserOAuthFlowProvider : IMcpBrowserOAuthFlowProvider
{
    private readonly IBrowserLauncher _browserLauncher;
    private readonly McpOAuthTokenStoreFactory _tokenStoreFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly ILogger<McpBrowserOAuthFlowProvider> _logger;

    public McpBrowserOAuthFlowProvider(
        IBrowserLauncher browserLauncher,
        McpOAuthTokenStoreFactory tokenStoreFactory,
        ISecretResolver secretResolver,
        ILogger<McpBrowserOAuthFlowProvider> logger)
    {
        _browserLauncher = browserLauncher;
        _tokenStoreFactory = tokenStoreFactory;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async Task<McpBrowserOAuthFlowResult> StartFlowAsync(
        string serverName,
        string endpoint,
        McpOAuthConfig config,
        McpBrowserOAuthOptions options,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var callbackListener = new OAuthCallbackListener();

        var oauthDelegate = new HypaBrowserOAuthDelegate(
            _browserLauncher,
            callbackListener,
            progress,
            options.NoBrowser,
            options.CallbackTimeout,
            options.Interactive);

        await callbackListener.StartAsync(ct);

        var tokenStore = _tokenStoreFactory.For(serverName);

        var clientSecret = config.ClientSecretRef is not null
            ? await _secretResolver.ResolveAsync(config.ClientSecretRef, ct)
            : null;

        // Capture DCR-issued credentials when no ClientId was provided.
        string? dcrClientId = null;
        string? dcrClientSecret = null;
        var isDcrPath = config.ClientId is null;

        var oauthOptions = new ClientOAuthOptions
        {
            RedirectUri = callbackListener.GetRedirectUri(),
            Scopes = config.Scopes,
            AuthorizationRedirectDelegate = oauthDelegate.HandleAsync,
            TokenCache = tokenStore,
        };

        if (config.ClientId is not null)
            oauthOptions.ClientId = config.ClientId;

        if (clientSecret is not null)
            oauthOptions.ClientSecret = clientSecret;

        // Wire up DCR response capture so the flow result contains the server-assigned credentials.
        if (isDcrPath)
        {
            oauthOptions.DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ResponseDelegate = (response, _) =>
                {
                    dcrClientId = response.ClientId;
                    dcrClientSecret = response.ClientSecret;
                    _logger.LogInformation(
                        "Dynamic client registration succeeded for server '{Server}': clientId={ClientId}",
                        serverName, dcrClientId);
                    return Task.CompletedTask;
                }
            };
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            TransportMode = HttpTransportMode.AutoDetect,
            Name = serverName,
            OAuth = oauthOptions,
        };

        var tlsHandler = McpTransportBuilder.BuildHttpClientHandler(options.Tls);

        McpClient? client = null;
        try
        {
            IClientTransport transport = tlsHandler is not null
                ? new HttpClientTransport(transportOptions, new HttpClient(tlsHandler))
                : new HttpClientTransport(transportOptions);
            client = await McpClient.CreateAsync(transport, new McpClientOptions(), null, ct);

            var tools = await client.ListToolsAsync(cancellationToken: ct);

            // Persist DCR credentials so they can be resolved later.
            if (dcrClientSecret is not null && dcrClientId is not null)
            {
                await tokenStore.StoreDcrCredentialsAsync(dcrClientId, dcrClientSecret, ct);
            }

            // Use DCR-issued credentials when available; otherwise fall back to the original config.
            var resolvedClientId = dcrClientId ?? config.ClientId;
            var resolvedSecretRef = dcrClientSecret is not null
                ? $"hypa:dcr:{serverName}"
                : config.ClientSecretRef;
            var completedConfig = new McpOAuthConfig(
                ClientId: resolvedClientId,
                ClientSecretRef: resolvedSecretRef,
                Scopes: config.Scopes);
            return new McpBrowserOAuthFlowResult(
                Succeeded: true,
                CompletedConfig: completedConfig,
                ToolCount: tools.Count);
        }
        catch (OperationCanceledException)
        {
            return new McpBrowserOAuthFlowResult(
                Succeeded: false,
                CompletedConfig: null,
                ToolCount: null,
                Error: "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OAuth browser flow failed for server '{Server}'", serverName);
            return new McpBrowserOAuthFlowResult(
                Succeeded: false,
                CompletedConfig: null,
                ToolCount: null,
                Error: ex.Message);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
            await callbackListener.StopAsync();
        }
    }
}
