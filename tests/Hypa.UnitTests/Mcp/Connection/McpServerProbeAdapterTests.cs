using System.Net;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Connection;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Rewrite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Hypa.UnitTests.Mcp.Connection;

public sealed class McpServerProbeAdapterTests
{
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IShellLexer _shellLexer = Substitute.For<IShellLexer>();
    private readonly IMcpSdkBridge _sdk = Substitute.For<IMcpSdkBridge>();
    private readonly McpConfigValidationService _validator = new();
    private readonly McpTransportBuilder _transportBuilder;
    private readonly McpServerProbeAdapter _sut;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public McpServerProbeAdapterTests()
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

        _transportBuilder = McpTransportBuilderFactory.Create(_authProvider, _sdk, _shellLexer);
        _sut = new McpServerProbeAdapter(
            _transportBuilder,
            _sdk,
            _validator,
            NullLoggerFactory.Instance,
            NullLogger<McpServerProbeAdapter>.Instance);
    }

    private static McpServerDefinition RemoteServer(
        string name = "svc",
        string endpoint = "https://example.com/mcp",
        McpAuthConfig? auth = null,
        TimeSpan? connectTimeout = null) =>
        new(name,
            new McpTransportConfig(McpTransportKind.HttpAutoDetect, endpoint),
            auth ?? new NoneAuthConfig(),
            ConnectTimeout: connectTimeout);

    private IProbeClientFacade FakeClient(
        IList<McpClientTool>? tools = null,
        Exception? listToolsException = null)
    {
        var client = Substitute.For<IProbeClientFacade>();
        if (listToolsException is not null)
            client.ListToolsAsync(Arg.Any<CancellationToken>())
                .Returns(new ValueTask<IList<McpClientTool>>(
                    Task.FromException<IList<McpClientTool>>(listToolsException)));
        else
            client.ListToolsAsync(Arg.Any<CancellationToken>())
                .Returns(new ValueTask<IList<McpClientTool>>(tools ?? []));
        client.DisposeAsync().Returns(ValueTask.CompletedTask);
        return client;
    }

    private static HttpRequestException HttpException(int statusCode) =>
        new($"HTTP {statusCode}", null, (HttpStatusCode)statusCode);

    private void SetupProbeClientAsync(IProbeClientFacade? client = null, Exception? throws = null)
    {
        if (throws is not null)
        {
            _sdk.CreateProbeClientAsync(
                    Arg.Any<IClientTransport>(),
                    Arg.Any<McpClientOptions>(),
                    Arg.Any<ILoggerFactory?>(),
                    Arg.Any<CancellationToken>())
                .ThrowsAsync(throws);
        }
        else
        {
            var c = client ?? FakeClient();
            _sdk.CreateProbeClientAsync(
                    Arg.Any<IClientTransport>(),
                    Arg.Any<McpClientOptions>(),
                    Arg.Any<ILoggerFactory?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(c));
        }
    }

    [Fact]
    public async Task ProbeAsync_Reachable_WhenListToolsSucceeds()
    {
        SetupProbeClientAsync(FakeClient());

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.Reachable, result.Status);
        Assert.Contains("tools/list", result.Message);
    }

    [Fact]
    public async Task ProbeAsync_InvalidConfig_BeforeAnyNetwork()
    {
        var server = new McpServerDefinition(
            "svc",
            new McpTransportConfig(McpTransportKind.Http, string.Empty),
            new NoneAuthConfig());

        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.InvalidConfig, result.Status);
        await _sdk.DidNotReceive().CreateProbeClientAsync(
            Arg.Any<IClientTransport>(),
            Arg.Any<McpClientOptions>(),
            Arg.Any<ILoggerFactory?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeAsync_AuthRequired_OnCredentialResolutionException()
    {
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpAuthContext>(
                Task.FromException<McpAuthContext>(new McpCredentialResolutionException("env:X missing"))));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.Contains("env:X missing", result.Message);
        Assert.NotNull(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_AuthRequired_On401HttpRequestException()
    {
        SetupProbeClientAsync(throws: HttpException(401));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotNull(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_AuthRequired_On403HttpRequestException()
    {
        SetupProbeClientAsync(throws: HttpException(403));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotNull(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_AuthRequired_OnSdkExceptionWith401InMessage()
    {
        SetupProbeClientAsync(throws: new InvalidOperationException("HTTP 401 Unauthorized"));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotNull(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_Timeout_WhenConnectTimeoutFires()
    {
        var tcs = new TaskCompletionSource<IProbeClientFacade>();
        _sdk.CreateProbeClientAsync(
                Arg.Any<IClientTransport>(),
                Arg.Any<McpClientOptions>(),
                Arg.Any<ILoggerFactory?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ct = call.Arg<CancellationToken>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });

        var server = RemoteServer(connectTimeout: TimeSpan.FromMilliseconds(50));
        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task ProbeAsync_RespectsOuterCancellation()
    {
        using var cts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource<IProbeClientFacade>();
        _sdk.CreateProbeClientAsync(
                Arg.Any<IClientTransport>(),
                Arg.Any<McpClientOptions>(),
                Arg.Any<ILoggerFactory?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ct = call.Arg<CancellationToken>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            });

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.ProbeAsync(RemoteServer(), cts.Token));
    }

    [Fact]
    public async Task ProbeAsync_ConnectionFailed_OnHttpRequestException()
    {
        SetupProbeClientAsync(throws: HttpException(500));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.ConnectionFailed, result.Status);
    }

    [Fact]
    public async Task ProbeAsync_Unknown_OnArbitraryException()
    {
        SetupProbeClientAsync(throws: new InvalidOperationException("something unexpected"));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task ProbeAsync_DoesNotLeakSecretsInMessage()
    {
        // Exception message contains a literal that looks like a resolved secret value.
        // The adapter must not include it verbatim (uses type name only for Unknown).
        SetupProbeClientAsync(throws: new InvalidOperationException("resolved: SECRET_VALUE actual-token"));

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.Unknown, result.Status);
        Assert.DoesNotContain("SECRET_VALUE", result.Message);
        Assert.DoesNotContain("actual-token", result.Message);
    }

    [Fact]
    public async Task ProbeAsync_DisposesClientOnSuccess()
    {
        var client = FakeClient();
        SetupProbeClientAsync(client);

        await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        await client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ProbeAsync_DisposesClientOnFailure()
    {
        var client = FakeClient(listToolsException: new InvalidOperationException("boom"));
        SetupProbeClientAsync(client);

        await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        await client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task BuildGuidance_None_SuggestsBearerAndDeviceCodeExamples()
    {
        SetupProbeClientAsync(throws: HttpException(401));
        var server = RemoteServer(name: "my-server", endpoint: "https://example.com/mcp", auth: new NoneAuthConfig());

        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.NotNull(result.AuthGuidance);
        Assert.NotNull(result.AuthGuidance!.NextCommands);
        Assert.Equal(2, result.AuthGuidance.NextCommands!.Count);
        Assert.All(result.AuthGuidance.NextCommands, cmd => Assert.StartsWith("hypa mcp add", cmd));
        Assert.Contains(result.AuthGuidance.NextCommands, cmd => cmd.Contains("--auth bearer"));
        Assert.Contains(result.AuthGuidance.NextCommands, cmd => cmd.Contains("--auth oauth2DeviceCode"));
        Assert.All(result.AuthGuidance.NextCommands, cmd => Assert.Contains("--transport http", cmd));
    }

    [Fact]
    public async Task ProbeAsync_ConnectionFailed_OnHttpRequestExceptionWithAuthKeywordInMessage()
    {
        // 500 with "unauthorized" in the message must not be routed through the semantic auth check;
        // only exceptions without a status code fall through to IsAuthSemantic.
        var ex = new HttpRequestException("Internal Server Error: unauthorized operation", null, HttpStatusCode.InternalServerError);
        SetupProbeClientAsync(throws: ex);

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.ConnectionFailed, result.Status);
        Assert.Null(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_AuthRequired_OnHttpRequestExceptionWithNullStatusAndAuthKeyword()
    {
        // SDK wraps a 401 in HttpRequestException without setting StatusCode — exercises catch #2.
        var ex = new HttpRequestException("HTTP 401 Unauthorized", null, null);
        SetupProbeClientAsync(throws: ex);

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotNull(result.AuthGuidance);
    }

    [Fact]
    public async Task ProbeAsync_ConnectionFailed_OnDnsFailure()
    {
        // DNS failure: HttpRequestException with null StatusCode, no auth keyword — exercises
        // the null-status non-auth path through catch #2 (condition false) → catch #4.
        var ex = new HttpRequestException("Name resolution failure", null, null);
        SetupProbeClientAsync(throws: ex);

        var result = await _sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.ConnectionFailed, result.Status);
    }

    [Fact]
    public async Task BuildGuidance_Sse_SuggestsCorrectTransport()
    {
        SetupProbeClientAsync(throws: HttpException(401));
        var server = new McpServerDefinition(
            "my-server",
            new McpTransportConfig(McpTransportKind.Sse, "https://example.com/mcp"),
            new NoneAuthConfig());

        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.NotNull(result.AuthGuidance?.NextCommands);
        Assert.All(result.AuthGuidance!.NextCommands!, cmd => Assert.Contains("--transport sse", cmd));
    }

    [Fact]
    public async Task BuildGuidance_StreamableHttp_SuggestsCorrectTransport()
    {
        SetupProbeClientAsync(throws: HttpException(401));
        var server = new McpServerDefinition(
            "my-server",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new NoneAuthConfig());

        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.NotNull(result.AuthGuidance?.NextCommands);
        Assert.All(result.AuthGuidance!.NextCommands!, cmd => Assert.Contains("--transport streamableHttp", cmd));
    }

    [Fact]
    public async Task BuildGuidance_DeviceCode_EchoesNonSecretMetadata()
    {
        SetupProbeClientAsync(throws: HttpException(401));
        var auth = new OAuth2DeviceCodeConfig(
            AuthUrl: "https://auth.example.com/authorize",
            TokenUrl: "https://auth.example.com/token",
            ClientId: "my-client-id",
            Scopes: ["read", "write"]);
        var server = RemoteServer(auth: auth);

        var result = await _sut.ProbeAsync(server, CancellationToken.None);

        Assert.NotNull(result.AuthGuidance);
        var guidance = result.AuthGuidance!;
        Assert.Equal("oauth2DeviceCode", guidance.SuggestedAuthMode);
        Assert.Equal("https://auth.example.com/authorize", guidance.AuthorizationUrl);
        Assert.Equal("https://auth.example.com/token", guidance.TokenUrl);
        Assert.Equal("my-client-id", guidance.ClientId);
        Assert.NotNull(guidance.Scopes);
        Assert.Contains("read", guidance.Scopes!);
        Assert.Contains("write", guidance.Scopes!);
    }
}

[Collection("SequentialEnvTests")]
public sealed class McpServerProbeAdapterEnvTests
{
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IShellLexer _shellLexer = Substitute.For<IShellLexer>();
    private readonly IMcpSdkBridge _sdk = Substitute.For<IMcpSdkBridge>();
    private readonly McpServerProbeAdapter _sut;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public McpServerProbeAdapterEnvTests()
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

        _sdk.CreateProbeClientAsync(
                Arg.Any<IClientTransport>(),
                Arg.Any<McpClientOptions>(),
                Arg.Any<ILoggerFactory?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Net.Http.HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var transportBuilder = McpTransportBuilderFactory.Create(_authProvider, _sdk, _shellLexer);
        _sut = new McpServerProbeAdapter(
            transportBuilder,
            _sdk,
            new McpConfigValidationService(),
            NullLoggerFactory.Instance,
            NullLogger<McpServerProbeAdapter>.Instance);
    }

    [Fact]
    public async Task BuildGuidance_NeverIncludesSecretRefValues()
    {
        Environment.SetEnvironmentVariable("LEAK", "actual-secret");
        try
        {
            var server = new McpServerDefinition(
                "svc",
                new McpTransportConfig(McpTransportKind.HttpAutoDetect, "https://example.com/mcp"),
                new BearerAuthConfig("env:LEAK"));

            var result = await _sut.ProbeAsync(server, CancellationToken.None);

            Assert.NotNull(result.AuthGuidance);
            var guidanceText = result.AuthGuidance!.NextCommands is not null
                ? string.Join(" ", result.AuthGuidance.NextCommands)
                : string.Empty;

            Assert.DoesNotContain("actual-secret", result.Message);
            Assert.DoesNotContain("actual-secret", guidanceText);
            Assert.DoesNotContain("actual-secret",
                result.AuthGuidance.AuthorizationUrl ?? string.Empty);
            Assert.DoesNotContain("actual-secret",
                result.AuthGuidance.TokenUrl ?? string.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEAK", null);
        }
    }
}

// Phase E — MCP OAuth probe detection tests (separate class for clarity)
public sealed class McpServerProbeAdapterOAuthDetectionTests
{
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IShellLexer _shellLexer = Substitute.For<IShellLexer>();
    private readonly IMcpSdkBridge _sdk = Substitute.For<IMcpSdkBridge>();

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public McpServerProbeAdapterOAuthDetectionTests()
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
    }

    // bearerChallengeOverride simulates WwwAuthenticateCapture having intercepted a 401+Bearer
    // from the actual SDK probe request, bypassing the need for a real HTTP client in tests.
    private McpServerProbeAdapter CreateSut(bool? bearerChallengeOverride = null)
    {
        var transportBuilder = McpTransportBuilderFactory.Create(_authProvider, _sdk, _shellLexer);
        var sut = new McpServerProbeAdapter(
            transportBuilder,
            _sdk,
            new McpConfigValidationService(),
            NullLoggerFactory.Instance,
            NullLogger<McpServerProbeAdapter>.Instance);
        if (bearerChallengeOverride.HasValue)
            sut.BearerChallengeOverride = bearerChallengeOverride.Value;
        return sut;
    }

    private static McpServerDefinition RemoteServer(string endpoint = "https://example.com/mcp") =>
        new("svc",
            new McpTransportConfig(McpTransportKind.HttpAutoDetect, endpoint),
            new NoneAuthConfig());

    private void SetupProbe401()
    {
        _sdk.CreateProbeClientAsync(
                Arg.Any<IClientTransport>(),
                Arg.Any<McpClientOptions>(),
                Arg.Any<ILoggerFactory?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("401", null, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task ProbeAsync_McpOAuth_Detected_On401_WithBearerChallenge()
    {
        SetupProbe401();

        // bearerChallengeOverride: true simulates WwwAuthenticateCapture detecting 401+Bearer.
        // No discovery client needed — Bearer challenge alone is the mcpOAuth signal.
        var sut = CreateSut(bearerChallengeOverride: true);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotNull(result.AuthGuidance);
        Assert.Equal("mcpOAuth", result.AuthGuidance!.SuggestedAuthMode);
        Assert.NotNull(result.AuthGuidance.NextCommands);
    }

    [Fact]
    public async Task ProbeAsync_GenericGuidance_When401_And_NoBearerChallenge()
    {
        // Probe got 401 but WwwAuthenticateCapture did not see WWW-Authenticate: Bearer.
        SetupProbe401();

        var sut = CreateSut(bearerChallengeOverride: false);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.NotEqual("mcpOAuth", result.AuthGuidance?.SuggestedAuthMode);
    }

    [Fact]
    public async Task ProbeAsync_McpOAuth_StillReturned_WhenDiscoveryEndpointWouldReturn404()
    {
        // Bearer challenge → mcpOAuth even when a discovery endpoint would 404.
        // The probe no longer makes the discovery call; SDK handles it during the OAuth flow.
        SetupProbe401();

        var sut = CreateSut(bearerChallengeOverride: true);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.Equal("mcpOAuth", result.AuthGuidance?.SuggestedAuthMode);
    }

    [Fact]
    public async Task ProbeAsync_McpOAuth_StillReturned_WhenDiscoveryEndpointWouldTimeout()
    {
        // Bearer challenge → mcpOAuth even when a discovery endpoint would time out.
        SetupProbe401();

        var sut = CreateSut(bearerChallengeOverride: true);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.Equal("mcpOAuth", result.AuthGuidance?.SuggestedAuthMode);
    }

    [Fact]
    public async Task ProbeAsync_McpOAuth_StillReturned_WhenDiscoveryEndpointWouldReturnNoAuthServers()
    {
        // Bearer challenge → mcpOAuth regardless of what a discovery response would contain.
        SetupProbe401();

        var sut = CreateSut(bearerChallengeOverride: true);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.Equal("mcpOAuth", result.AuthGuidance?.SuggestedAuthMode);
    }

    [Fact]
    public async Task ProbeAsync_McpOAuth_StillReturned_WhenDiscoveryWouldReturnEmptyAuthServers()
    {
        // Bearer challenge is sufficient; empty authorization_servers in a discovery response
        // would have been a false negative under the old design — no longer possible.
        SetupProbe401();

        var sut = CreateSut(bearerChallengeOverride: true);
        var result = await sut.ProbeAsync(RemoteServer(), CancellationToken.None);

        Assert.Equal(McpServerProbeStatus.AuthRequired, result.Status);
        Assert.Equal("mcpOAuth", result.AuthGuidance?.SuggestedAuthMode);
    }
}

// Helper shared by probe adapter and connection factory tests
file static class McpTransportBuilderFactory
{
    public static McpTransportBuilder Create(
        IMcpAuthProvider authProvider,
        IMcpSdkBridge sdk,
        IShellLexer shellLexer)
    {
        var redaction = new SecretRedactionRegistry();
        var tokenFactory = new McpOAuthTokenStoreFactory(
            Path.GetTempPath(), redaction, NullLogger<McpOAuthTokenStore>.Instance);
        var browserLauncher = Substitute.For<IBrowserLauncher>();
        var secretResolver = Substitute.For<ISecretResolver>();
        return new McpTransportBuilder(authProvider, sdk, shellLexer, browserLauncher, tokenFactory, secretResolver);
    }
}

public sealed class WwwAuthenticateCaptureTests
{
    [Fact]
    public async Task Sets_HasBearerChallenge_On_401_With_Bearer_Header()
    {
        var inner = new StubInnerHandler(HttpStatusCode.Unauthorized, bearerChallenge: true);
        var capture = new WwwAuthenticateCapture { InnerHandler = inner };
        using var client = new HttpClient(capture);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(capture.HasBearerChallenge);
    }

    [Fact]
    public async Task Does_Not_Set_HasBearerChallenge_On_401_Without_Bearer_Header()
    {
        var inner = new StubInnerHandler(HttpStatusCode.Unauthorized, bearerChallenge: false);
        var capture = new WwwAuthenticateCapture { InnerHandler = inner };
        using var client = new HttpClient(capture);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(capture.HasBearerChallenge);
    }

    [Fact]
    public async Task Does_Not_Set_HasBearerChallenge_On_200()
    {
        var inner = new StubInnerHandler(HttpStatusCode.OK, bearerChallenge: false);
        var capture = new WwwAuthenticateCapture { InnerHandler = inner };
        using var client = new HttpClient(capture);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.com/"));

        Assert.False(capture.HasBearerChallenge);
    }

    private sealed class StubInnerHandler(HttpStatusCode status, bool bearerChallenge) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(status);
            if (bearerChallenge)
                resp.Headers.WwwAuthenticate.Add(
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer"));
            return Task.FromResult(resp);
        }
    }
}
