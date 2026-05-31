using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Rewrite;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed class McpTransportBuilder
{
    private readonly IMcpAuthProvider _authProvider;
    private readonly IMcpSdkBridge _sdk;
    private readonly IShellLexer _shellLexer;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly McpOAuthTokenStoreFactory _tokenStoreFactory;
    private readonly ISecretResolver _secretResolver;

    public McpTransportBuilder(
        IMcpAuthProvider authProvider,
        IMcpSdkBridge sdk,
        IShellLexer shellLexer,
        IBrowserLauncher browserLauncher,
        McpOAuthTokenStoreFactory tokenStoreFactory,
        ISecretResolver secretResolver)
    {
        _authProvider = authProvider;
        _sdk = sdk;
        _shellLexer = shellLexer;
        _browserLauncher = browserLauncher;
        _tokenStoreFactory = tokenStoreFactory;
        _secretResolver = secretResolver;
    }

    public Task<IClientTransport> BuildAsync(McpServerDefinition server, CancellationToken ct) =>
        server.Transport.Kind switch
        {
            McpTransportKind.Stdio => Task.FromResult(BuildStdio(server)),
            _ => BuildHttpAsync(server, ct),
        };

    private IClientTransport BuildStdio(McpServerDefinition server)
    {
        var endpoint = server.Transport.Endpoint ?? string.Empty;
        var tokens = _shellLexer.Lex(endpoint);
        var args = tokens
            .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
            .Select(t => t.Value)
            .ToArray();

        var command = args.Length > 0 ? args[0] : endpoint;
        var arguments = args.Length > 1 ? args[1..] : [];

        return _sdk.CreateStdioTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = arguments,
            Name = server.Name,
        });
    }

    private async Task<IClientTransport> BuildHttpAsync(McpServerDefinition server, CancellationToken ct)
    {
        var auth = await _authProvider.GetAuthContextAsync(server, ct);
        var endpoint = BuildEndpointUri(server.Transport.Endpoint!, auth);

        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = MapTransportMode(server.Transport.Kind),
            Name = server.Name,
        };

        if (server.ConnectTimeout.HasValue)
            options.ConnectionTimeout = server.ConnectTimeout.Value;

        var headers = auth.Headers.Count > 0
            ? auth.Headers.ToDictionary()
            : new Dictionary<string, string>();

        if (server.Auth is McpOAuthConfig)
            await InjectCachedOAuthTokenAsync(server.Name, headers, ct);

        if (headers.Count > 0)
            options.AdditionalHeaders = headers;

        var httpClient = BuildHttpClient(server, auth);
        return _sdk.CreateHttpTransport(options, httpClient);
    }

    // Non-interactive operations (schema, tools, invoke, etc.) must never start an interactive
    // OAuth flow. Inject the cached token as a plain bearer header; fail fast with AuthRequired
    // when the token is absent or expired so the caller surfaces a clear error instead of hanging.
    // The interactive flow lives exclusively in McpBrowserOAuthFlowProvider (mcp auth login).
    private async Task InjectCachedOAuthTokenAsync(
        string serverName, Dictionary<string, string> headers, CancellationToken ct)
    {
        var cached = await _tokenStoreFactory.For(serverName).GetTokensAsync(ct);
        if (cached is not null && !string.IsNullOrEmpty(cached.AccessToken))
            headers["Authorization"] = $"Bearer {cached.AccessToken}";
        else
            throw new McpCredentialResolutionException(
                $"No valid OAuth token for '{serverName}'. Run: hypa mcp auth login --server {serverName}");
    }

    private static HttpTransportMode MapTransportMode(McpTransportKind kind) => kind switch
    {
        McpTransportKind.Sse => HttpTransportMode.Sse,
        McpTransportKind.Http => HttpTransportMode.StreamableHttp,
        _ => HttpTransportMode.AutoDetect,
    };

    private static Uri BuildEndpointUri(string baseEndpoint, McpAuthContext auth)
    {
        var uri = new Uri(baseEndpoint);
        if (auth.QueryParameters is not { Count: > 0 } qp)
            return uri;

        var qs = string.Join("&", qp.Select(
            kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        var existing = uri.Query.TrimStart('?');
        var separator = existing.Length > 0 ? "&" : "";
        return new Uri($"{uri.GetLeftPart(UriPartial.Path)}?{existing}{separator}{qs}");
    }

    public async Task<(IClientTransport Transport, WwwAuthenticateCapture? Capture)> BuildForProbeAsync(
        McpServerDefinition server, CancellationToken ct)
    {
        if (server.Transport.Kind == McpTransportKind.Stdio)
            return (await BuildAsync(server, ct), null);

        // McpOAuthConfig: never set HttpClientTransportOptions.OAuth in probe mode.
        // Use a cached token as a plain bearer header when available; otherwise probe unauthenticated.
        if (server.Auth is McpOAuthConfig)
            return (await BuildHttpForOAuthProbeAsync(server, ct), null);

        // NoneAuth: inject capture handler so the probe can detect WWW-Authenticate: Bearer.
        if (server.Auth is not NoneAuthConfig)
            return (await BuildAsync(server, ct), null);

        var auth = await _authProvider.GetAuthContextAsync(server, ct);
        var endpoint = BuildEndpointUri(server.Transport.Endpoint!, auth);

        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = MapTransportMode(server.Transport.Kind),
            Name = server.Name,
        };

        if (server.ConnectTimeout.HasValue)
            options.ConnectionTimeout = server.ConnectTimeout.Value;

        if (auth.Headers.Count > 0)
            options.AdditionalHeaders = auth.Headers.ToDictionary();

        var capture = new WwwAuthenticateCapture();
        capture.InnerHandler = BuildHttpClientHandler(server, auth) ?? new HttpClientHandler();
        var httpClient = new HttpClient(capture);

        return (_sdk.CreateHttpTransport(options, httpClient), capture);
    }

    private async Task<IClientTransport> BuildHttpForOAuthProbeAsync(
        McpServerDefinition server, CancellationToken ct)
    {
        var auth = await _authProvider.GetAuthContextAsync(server, ct);
        var endpoint = BuildEndpointUri(server.Transport.Endpoint!, auth);

        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = MapTransportMode(server.Transport.Kind),
            Name = server.Name,
        };

        if (server.ConnectTimeout.HasValue)
            options.ConnectionTimeout = server.ConnectTimeout.Value;

        var headers = auth.Headers.Count > 0
            ? auth.Headers.ToDictionary()
            : new Dictionary<string, string>();

        // Inject cached token as a plain bearer header when valid; never construct ClientOAuthOptions.
        var cachedToken = await _tokenStoreFactory.For(server.Name).GetTokensAsync(ct);
        if (cachedToken is not null && !string.IsNullOrEmpty(cachedToken.AccessToken))
            headers["Authorization"] = $"Bearer {cachedToken.AccessToken}";

        if (headers.Count > 0)
            options.AdditionalHeaders = headers;

        var httpClient = BuildHttpClient(server, auth);
        return _sdk.CreateHttpTransport(options, httpClient);
    }

    internal static HttpClientHandler? BuildHttpClientHandler(McpTlsConfig? tls)
    {
        var certPath = tls?.ClientCertPath;
        var keyPath = tls?.ClientKeyPath;
        var caCertPath = tls?.CaCertPath;

        if (certPath is null && keyPath is null && caCertPath is null)
            return null;

        var handler = new HttpClientHandler();

        if (certPath is not null && keyPath is not null)
        {
            var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            handler.ClientCertificates.Add(cert);
        }

        if (caCertPath is not null)
        {
            var caCert = X509CertificateLoader.LoadCertificateFromFile(caCertPath);
            handler.ServerCertificateCustomValidationCallback =
                (_, serverCert, chain, errors) => ValidateWithCustomCa(serverCert, chain, caCert, errors);
        }

        return handler;
    }

    private static HttpClientHandler? BuildHttpClientHandler(McpServerDefinition server, McpAuthContext auth)
    {
        var certPath = server.Tls?.ClientCertPath ?? auth.ClientCertificatePath;
        var keyPath = server.Tls?.ClientKeyPath ?? auth.ClientKeyPath;
        var caCertPath = server.Tls?.CaCertPath;

        if (certPath is null && keyPath is null && caCertPath is null)
            return null;

        var handler = new HttpClientHandler();

        if (certPath is not null && keyPath is not null)
        {
            var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            handler.ClientCertificates.Add(cert);
        }

        if (caCertPath is not null)
        {
            var caCert = X509CertificateLoader.LoadCertificateFromFile(caCertPath);
            handler.ServerCertificateCustomValidationCallback =
                (_, serverCert, chain, errors) => ValidateWithCustomCa(serverCert, chain, caCert, errors);
        }

        return handler;
    }

    private static HttpClient? BuildHttpClient(McpServerDefinition server, McpAuthContext auth)
    {
        var handler = BuildHttpClientHandler(server, auth);
        return handler is null ? null : new HttpClient(handler);
    }

    private static bool ValidateWithCustomCa(
        X509Certificate? serverCert,
        X509Chain? chain,
        X509Certificate2 caCert,
        SslPolicyErrors errors)
    {
        if (serverCert is null)
            return false;

        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;

        using var customChain = new X509Chain();
        customChain.ChainPolicy.ExtraStore.Add(caCert);
        customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        if (!customChain.Build(new X509Certificate2(serverCert)))
            return false;

        var root = customChain.ChainElements[^1].Certificate;
        return root.Thumbprint.Equals(caCert.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }
}
