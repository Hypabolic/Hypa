using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Connection;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Rewrite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp.Connection;

public sealed class McpTransportBuilderTests
{
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpSdkBridge _sdk = Substitute.For<IMcpSdkBridge>();
    private readonly IShellLexer _shellLexer = Substitute.For<IShellLexer>();

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public McpTransportBuilderTests()
    {
        _shellLexer.Lex(Arg.Any<string>()).Returns(call =>
        {
            var cmd = call.Arg<string>();
            return (IReadOnlyList<ShellToken>)cmd
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((p, i) => new ShellToken(TokenKind.Arg, p, i))
                .ToList();
        });

        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(EmptyHeaders)));

        _sdk.CreateHttpTransport(Arg.Any<HttpClientTransportOptions>(), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        _sdk.CreateStdioTransport(Arg.Any<StdioClientTransportOptions>())
            .Returns(Substitute.For<IClientTransport>());
    }

    private readonly IBrowserLauncher _browserLauncher = Substitute.For<IBrowserLauncher>();

    private McpTransportBuilder CreateSut()
    {
        var dir = Path.GetTempPath();
        var redaction = new SecretRedactionRegistry();
        var tokenFactory = new McpOAuthTokenStoreFactory(dir, redaction, NullLogger<McpOAuthTokenStore>.Instance);
        return new McpTransportBuilder(_authProvider, _sdk, _shellLexer, _browserLauncher, tokenFactory, Substitute.For<ISecretResolver>());
    }

    private static McpServerDefinition StdioServer(string endpoint = "echo hello") =>
        new("test", new McpTransportConfig(McpTransportKind.Stdio, endpoint), new NoneAuthConfig());

    private static McpServerDefinition HttpServer(
        McpTransportKind kind = McpTransportKind.Http,
        string endpoint = "https://example.com/mcp",
        McpTlsConfig? tls = null) =>
        new("test", new McpTransportConfig(kind, endpoint), new NoneAuthConfig(), Tls: tls);

    private static McpServerDefinition OAuthServer(
        string endpoint = "https://example.com/mcp",
        string? clientId = null) =>
        new("oauth-server", new McpTransportConfig(McpTransportKind.Http, endpoint),
            new McpOAuthConfig(ClientId: clientId));

    [Fact]
    public async Task Stdio_LexesEndpointIntoCommandAndArgs()
    {
        _shellLexer.Lex("node /path/server.js --port 3000")
            .Returns([
                new ShellToken(TokenKind.Arg, "node", 0),
                new ShellToken(TokenKind.QuotedArg, "/path/server.js", 5),
                new ShellToken(TokenKind.Arg, "--port", 20),
                new ShellToken(TokenKind.Arg, "3000", 27),
            ]);

        StdioClientTransportOptions? captured = null;
        _sdk.CreateStdioTransport(Arg.Do<StdioClientTransportOptions>(o => captured = o))
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(StdioServer("node /path/server.js --port 3000"), default);

        Assert.NotNull(captured);
        Assert.Equal("node", captured!.Command);
        Assert.Equal(["/path/server.js", "--port", "3000"], captured.Arguments);
    }

    [Fact]
    public async Task Http_MapsToStreamableHttp_ForHttpKind()
    {
        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(McpTransportKind.Http), default);

        Assert.Equal(HttpTransportMode.StreamableHttp, captured!.TransportMode);
    }

    [Fact]
    public async Task Http_MapsToSse_ForSseKind()
    {
        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(McpTransportKind.Sse), default);

        Assert.Equal(HttpTransportMode.Sse, captured!.TransportMode);
    }

    [Fact]
    public async Task Http_MapsToAutoDetect_ForHttpAutoDetectKind()
    {
        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(McpTransportKind.HttpAutoDetect), default);

        Assert.Equal(HttpTransportMode.AutoDetect, captured!.TransportMode);
    }

    [Fact]
    public async Task Http_AppendsQueryParams_FromAuthContext()
    {
        var qp = new Dictionary<string, string> { ["token"] = "val" };
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(EmptyHeaders, QueryParameters: qp)));

        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(), default);

        Assert.Contains("token=val", captured!.Endpoint.Query);
    }

    [Fact]
    public async Task Http_PropagatesAuthHeaders_ToAdditionalHeaders()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer tok" };
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(headers)));

        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(), default);

        Assert.NotNull(captured!.AdditionalHeaders);
        Assert.Equal("Bearer tok", captured.AdditionalHeaders!["Authorization"]);
    }

    [Fact]
    public async Task Http_PassesNonNullHttpClient_WhenCaCertPathProvided()
    {
        var caCertPath = CreateTempCaCertDerFile();
        try
        {
            HttpClient? capturedClient = null;
            _sdk.CreateHttpTransport(
                    Arg.Any<HttpClientTransportOptions>(),
                    Arg.Do<HttpClient?>(c => capturedClient = c))
                .Returns(Substitute.For<IClientTransport>());

            var server = HttpServer(tls: new McpTlsConfig(
                CaCertPath: caCertPath,
                ClientCertPath: null,
                ClientKeyPath: null));

            await CreateSut().BuildAsync(server, default);

            Assert.NotNull(capturedClient);
        }
        finally
        {
            if (File.Exists(caCertPath))
                File.Delete(caCertPath);
        }
    }

    [Fact]
    public async Task Http_PassesNonNullHttpClient_WhenClientCertAndKeyPathProvided()
    {
        var (certPath, keyPath) = CreateTempClientCertPemFiles();
        try
        {
            HttpClient? capturedClient = null;
            _sdk.CreateHttpTransport(
                    Arg.Any<HttpClientTransportOptions>(),
                    Arg.Do<HttpClient?>(c => capturedClient = c))
                .Returns(Substitute.For<IClientTransport>());

            var server = HttpServer(tls: new McpTlsConfig(
                CaCertPath: null,
                ClientCertPath: certPath,
                ClientKeyPath: keyPath));

            await CreateSut().BuildAsync(server, default);

            Assert.NotNull(capturedClient);
        }
        finally
        {
            if (File.Exists(certPath)) File.Delete(certPath);
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Http_PassesNullHttpClient_WhenNoTlsMaterial()
    {
        HttpClient? capturedClient = new(); // seed non-null to detect no-override
        _sdk.CreateHttpTransport(
                Arg.Any<HttpClientTransportOptions>(),
                Arg.Do<HttpClient?>(c => capturedClient = c))
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(tls: null), default);

        Assert.Null(capturedClient);
    }

    private static (string certPath, string keyPath) CreateTempClientCertPemFiles()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=test-client",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        var certPath = Path.GetTempFileName();
        var keyPath = Path.GetTempFileName();
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    private static string CreateTempCaCertDerFile()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=test-ca",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }

    // Phase D — OAuth wiring tests
    //
    // Non-interactive operations (schema, tools, invoke, etc.) must NEVER start an
    // interactive OAuth flow. BuildAsync uses cached-token-only: inject as bearer header
    // when present, throw McpCredentialResolutionException when absent/expired.
    // The interactive flow lives exclusively in McpBrowserOAuthFlowProvider (auth login).

    [Fact]
    public async Task McpOAuthConfig_WithNoCachedToken_ThrowsCredentialResolutionException()
    {
        // Fresh temp dir — no token file exists.
        await Assert.ThrowsAsync<McpCredentialResolutionException>(() =>
            CreateSut().BuildAsync(OAuthServer(), CancellationToken.None));
    }

    [Fact]
    public async Task McpOAuthConfig_WithValidCachedToken_InjectsBearerHeader()
    {
        var dir = await WriteCachedTokenAsync("oauth-server", "cached-token-abc");
        try
        {
            var sut = CreateSutWithTokenDir(dir);

            HttpClientTransportOptions? captured = null;
            _sdk.CreateHttpTransport(
                    Arg.Do<HttpClientTransportOptions>(o => captured = o),
                    Arg.Any<HttpClient?>())
                .Returns(Substitute.For<IClientTransport>());

            await sut.BuildAsync(OAuthServer(), CancellationToken.None);

            Assert.Equal("Bearer cached-token-abc", captured?.AdditionalHeaders?["Authorization"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task McpOAuthConfig_WithValidCachedToken_DoesNotSetOAuthOnOptions()
    {
        var dir = await WriteCachedTokenAsync("oauth-server", "some-token");
        try
        {
            var sut = CreateSutWithTokenDir(dir);

            HttpClientTransportOptions? captured = null;
            _sdk.CreateHttpTransport(
                    Arg.Do<HttpClientTransportOptions>(o => captured = o),
                    Arg.Any<HttpClient?>())
                .Returns(Substitute.For<IClientTransport>());

            await sut.BuildAsync(OAuthServer(), CancellationToken.None);

            Assert.Null(captured?.OAuth);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NoneAuthConfig_LeavesOAuthNull()
    {
        HttpClientTransportOptions? captured = null;
        _sdk.CreateHttpTransport(
                Arg.Do<HttpClientTransportOptions>(o => captured = o),
                Arg.Any<HttpClient?>())
            .Returns(Substitute.For<IClientTransport>());

        await CreateSut().BuildAsync(HttpServer(), CancellationToken.None);

        Assert.Null(captured?.OAuth);
    }

    private McpTransportBuilder CreateSutWithTokenDir(string dir)
    {
        var redaction = new SecretRedactionRegistry();
        var tokenFactory = new McpOAuthTokenStoreFactory(dir, redaction, NullLogger<McpOAuthTokenStore>.Instance);
        return new McpTransportBuilder(_authProvider, _sdk, _shellLexer, _browserLauncher, tokenFactory, Substitute.For<ISecretResolver>());
    }

    private static async Task<string> WriteCachedTokenAsync(string serverName, string accessToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new McpOAuthTokenStore(
            serverName, dir, new SecretRedactionRegistry(), NullLogger<McpOAuthTokenStore>.Instance);
        await store.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = accessToken,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return dir;
    }
}
