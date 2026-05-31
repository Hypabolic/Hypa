using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Connection;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Rewrite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Hypa.UnitTests.Mcp.Connection;

public sealed class McpClientConnectionFactoryTests
{
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpSdkBridge _sdk = Substitute.For<IMcpSdkBridge>();
    private readonly IShellLexer _shellLexer = Substitute.For<IShellLexer>();
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ILogger<McpClientConnectionFactory> _logger =
        NullLogger<McpClientConnectionFactory>.Instance;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public McpClientConnectionFactoryTests()
    {
        _shellLexer.Lex(Arg.Any<string>()).Returns(call =>
        {
            var cmd = call.Arg<string>();
            return (IReadOnlyList<ShellToken>)cmd
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((p, i) => new ShellToken(TokenKind.Arg, p, i))
                .ToList();
        });
    }

    private McpTransportBuilder BuildTransport()
    {
        var redaction = new SecretRedactionRegistry();
        var tokenFactory = new McpOAuthTokenStoreFactory(
            Path.GetTempPath(), redaction, NullLogger<McpOAuthTokenStore>.Instance);
        var browserLauncher = Substitute.For<IBrowserLauncher>();
        var secretResolver = Substitute.For<ISecretResolver>();
        return new McpTransportBuilder(_authProvider, _sdk, _shellLexer, browserLauncher, tokenFactory, secretResolver);
    }

    private McpClientConnectionFactory CreateSut() =>
        new(BuildTransport(), _sdk, _loggerFactory, _logger);

    private static McpServerDefinition StdioServer(string name = "test", string endpoint = "echo hello") =>
        new(name,
            new McpTransportConfig(McpTransportKind.Stdio, endpoint),
            new NoneAuthConfig());

    private static McpServerDefinition HttpServer(string name = "test", string endpoint = "https://example.com/mcp") =>
        new(name,
            new McpTransportConfig(McpTransportKind.Http, endpoint),
            new NoneAuthConfig());

    [Fact]
    public async Task GetOrCreateAsync_ReturnsCachedFacade_OnSecondCall()
    {
        var transport = Substitute.For<IClientTransport>();
        var client = Substitute.For<McpClient>();
        _sdk.CreateStdioTransport(Arg.Any<StdioClientTransportOptions>()).Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client));

        await using var sut = CreateSut();

        var first = await sut.GetOrCreateAsync(StdioServer(), default);
        var second = await sut.GetOrCreateAsync(StdioServer(), default);

        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        await _sdk.Received(1).CreateClientAsync(
            Arg.Any<IClientTransport>(),
            Arg.Any<McpClientOptions>(),
            Arg.Any<ILoggerFactory?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_ForcesRecreation()
    {
        var transport = Substitute.For<IClientTransport>();
        var client1 = Substitute.For<McpClient>();
        var client2 = Substitute.For<McpClient>();
        _sdk.CreateStdioTransport(Arg.Any<StdioClientTransportOptions>()).Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client1), Task.FromResult(client2));

        await using var sut = CreateSut();

        var first = await sut.GetOrCreateAsync(StdioServer(), default);
        await sut.InvalidateAsync("test");
        var second = await sut.GetOrCreateAsync(StdioServer(), default);

        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        await _sdk.Received(2).CreateClientAsync(
            Arg.Any<IClientTransport>(),
            Arg.Any<McpClientOptions>(),
            Arg.Any<ILoggerFactory?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stdio_ParsesCommandAndArgs_ViaShellLexer()
    {
        // Override default lexer to return tokens for a quoted-path command
        _shellLexer.Lex("node \"/path/with spaces/server.js\" --root \"/tmp/my dir\"")
            .Returns([
                new ShellToken(TokenKind.Arg, "node", 0),
                new ShellToken(TokenKind.QuotedArg, "/path/with spaces/server.js", 5),
                new ShellToken(TokenKind.Arg, "--root", 32),
                new ShellToken(TokenKind.QuotedArg, "/tmp/my dir", 39),
            ]);

        StdioClientTransportOptions? captured = null;
        var transport = Substitute.For<IClientTransport>();
        var client = Substitute.For<McpClient>();
        _sdk.CreateStdioTransport(Arg.Do<StdioClientTransportOptions>(o => captured = o))
            .Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client));

        await using var sut = CreateSut();
        await sut.GetOrCreateAsync(
            StdioServer("s", "node \"/path/with spaces/server.js\" --root \"/tmp/my dir\""),
            default);

        Assert.NotNull(captured);
        Assert.Equal("node", captured!.Command);
        Assert.Equal(["/path/with spaces/server.js", "--root", "/tmp/my dir"], captured.Arguments);
    }

    [Fact]
    public async Task Http_AppliesAuthHeaders()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token123" };
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(headers)));

        HttpClientTransportOptions? captured = null;
        var transport = Substitute.For<IClientTransport>();
        var client = Substitute.For<McpClient>();
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client));

        await using var sut = CreateSut();
        await sut.GetOrCreateAsync(HttpServer(), default);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.AdditionalHeaders);
        Assert.Equal("Bearer token123", captured.AdditionalHeaders!["Authorization"]);
    }

    [Fact]
    public async Task Http_AppliesQueryParamAuth()
    {
        var queryParams = new Dictionary<string, string> { ["api_key"] = "secret" };
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(EmptyHeaders, QueryParameters: queryParams)));

        HttpClientTransportOptions? captured = null;
        var transport = Substitute.For<IClientTransport>();
        var client = Substitute.For<McpClient>();
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client));

        await using var sut = CreateSut();
        await sut.GetOrCreateAsync(HttpServer("test", "https://example.com/mcp"), default);

        Assert.NotNull(captured);
        Assert.Contains("api_key=secret", captured!.Endpoint.Query);
    }

    [Fact]
    public async Task Http_AppliesConnectTimeout()
    {
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(EmptyHeaders)));

        HttpClientTransportOptions? captured = null;
        var transport = Substitute.For<IClientTransport>();
        var client = Substitute.For<McpClient>();
        _sdk.CreateHttpTransport(Arg.Do<HttpClientTransportOptions>(o => captured = o), Arg.Any<HttpClient?>())
            .Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(client));

        var server = new McpServerDefinition(
            "test",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new NoneAuthConfig(),
            ConnectTimeout: TimeSpan.FromSeconds(15));

        await using var sut = CreateSut();
        await sut.GetOrCreateAsync(server, default);

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(15), captured!.ConnectionTimeout);
    }

    [Fact]
    public async Task Http_BuildsMtlsHandler_WhenCertPathsProvided()
    {
        // Auth context contains cert paths; cert files won't exist on disk in tests,
        // so X509Certificate2.CreateFromPemFile will throw and factory wraps it as ConnectionFailed.
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(
                EmptyHeaders,
                ClientCertificatePath: "/nonexistent/client.pem",
                ClientKeyPath: "/nonexistent/client.key")));

        var transport = Substitute.For<IClientTransport>();
        _sdk.CreateHttpTransport(Arg.Any<HttpClientTransportOptions>(), Arg.Any<HttpClient?>())
            .Returns(transport);

        await using var sut = CreateSut();
        var result = await sut.GetOrCreateAsync(HttpServer(), default);

        // Cert files don't exist → exception wrapped in ConnectionFailed
        Assert.False(result.IsOk);
        Assert.Equal(McpErrorCodes.ConnectionFailed, result.Error.Code);
    }

    [Fact]
    public async Task ConnectionFailure_ReturnsError_DoesNotThrow()
    {
        var transport = Substitute.For<IClientTransport>();
        _sdk.CreateStdioTransport(Arg.Any<StdioClientTransportOptions>()).Returns(transport);
        _sdk.CreateClientAsync(transport, Arg.Any<McpClientOptions>(), Arg.Any<ILoggerFactory?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        await using var sut = CreateSut();
        var result = await sut.GetOrCreateAsync(StdioServer(), default);

        Assert.False(result.IsOk);
        Assert.Equal(McpErrorCodes.ConnectionFailed, result.Error.Code);
        Assert.Contains("Failed to connect to server 'test'.", result.Error.Message);
    }

    [Fact]
    public async Task McpOAuthConfig_WithNoCachedToken_ReturnsAuthRequired_NotHangs()
    {
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(new McpAuthContext(EmptyHeaders)));

        await using var sut = CreateSut();
        var server = new McpServerDefinition(
            "oauth-srv",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new McpOAuthConfig(ClientId: "test-client"));

        var result = await sut.GetOrCreateAsync(server, default);

        Assert.False(result.IsOk);
        Assert.Equal(McpErrorCodes.AuthRequired, result.Error.Code);
        await _sdk.DidNotReceive().CreateClientAsync(
            Arg.Any<IClientTransport>(),
            Arg.Any<McpClientOptions>(),
            Arg.Any<ILoggerFactory?>(),
            Arg.Any<CancellationToken>());
    }
}
