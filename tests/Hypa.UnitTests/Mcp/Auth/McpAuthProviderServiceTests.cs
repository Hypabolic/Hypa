using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class McpAuthProviderServiceTests
{
    private readonly ISecretResolver _secrets = Substitute.For<ISecretResolver>();
    private readonly SecretRedactionRegistry _redaction = new();
    private readonly FakeOAuthTokenService _fakeOAuth = new();
    private readonly McpAuthProviderService _sut;

    public McpAuthProviderServiceTests()
    {
        _sut = new McpAuthProviderService(
            _secrets,
            _fakeOAuth,
            _redaction,
            NullLogger<McpAuthProviderService>.Instance);
    }

    private static McpServerDefinition Server(McpAuthConfig auth) =>
        new("test", new McpTransportConfig(McpTransportKind.Http, "https://example.com"), auth);

    [Fact]
    public async Task None_ReturnsEmptyHeaders()
    {
        var ctx = await _sut.GetAuthContextAsync(Server(new NoneAuthConfig()), default);
        Assert.Empty(ctx.Headers);
        Assert.Null(ctx.BearerToken);
        Assert.Null(ctx.QueryParameters);
    }

    [Fact]
    public async Task Bearer_ReturnsAuthorizationHeader()
    {
        _secrets.ResolveAsync("env:TOKEN", default).Returns(new ValueTask<string?>("my-secret"));

        var ctx = await _sut.GetAuthContextAsync(Server(new BearerAuthConfig("env:TOKEN")), default);

        Assert.Equal("Bearer my-secret", ctx.Headers["Authorization"]);
    }

    [Fact]
    public async Task ApiKey_HeaderMode_SetsNamedHeader()
    {
        _secrets.ResolveAsync("env:APIKEY", default).Returns(new ValueTask<string?>("key123"));

        var ctx = await _sut.GetAuthContextAsync(
            Server(new ApiKeyAuthConfig("X-Api-Key", "env:APIKEY")), default);

        Assert.Equal("key123", ctx.Headers["X-Api-Key"]);
        Assert.Null(ctx.QueryParameters);
    }

    [Fact]
    public async Task ApiKey_QueryStringMode_PopulatesQueryParameters()
    {
        _secrets.ResolveAsync("env:APIKEY", default).Returns(new ValueTask<string?>("key123"));

        var ctx = await _sut.GetAuthContextAsync(
            Server(new ApiKeyAuthConfig("api_key", "env:APIKEY", InQueryString: true)), default);

        Assert.Empty(ctx.Headers);
        Assert.NotNull(ctx.QueryParameters);
        Assert.Equal("key123", ctx.QueryParameters!["api_key"]);
    }

    [Fact]
    public async Task Basic_SetsBase64EncodedAuthorizationHeader()
    {
        _secrets.ResolveAsync("env:USER", default).Returns(new ValueTask<string?>("alice"));
        _secrets.ResolveAsync("env:PASS", default).Returns(new ValueTask<string?>("p@ssw0rd"));

        var ctx = await _sut.GetAuthContextAsync(
            Server(new BasicAuthConfig("env:USER", "env:PASS")), default);

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:p@ssw0rd"));
        Assert.Equal($"Basic {expected}", ctx.Headers["Authorization"]);
    }

    [Fact]
    public async Task OAuth2ClientCredentials_DelegatesToOAuthTokenService()
    {
        _fakeOAuth.ClientCredentialsToken = "oauth-access-token";

        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");

        var ctx = await _sut.GetAuthContextAsync(Server(config), default);

        Assert.Equal("Bearer oauth-access-token", ctx.Headers["Authorization"]);
    }

    [Fact]
    public async Task OAuth2DeviceCode_WithCachedToken_SetsBearerHeader()
    {
        _fakeOAuth.DeviceCodeToken = "device-token-xyz";

        var config = new OAuth2DeviceCodeConfig(
            "https://auth.example.com/device", "https://auth.example.com/token", "client1");

        var ctx = await _sut.GetAuthContextAsync(Server(config), default);

        Assert.Equal("Bearer device-token-xyz", ctx.Headers["Authorization"]);
        Assert.Equal("device-token-xyz", ctx.BearerToken);
    }

    [Fact]
    public async Task OAuth2DeviceCode_WithoutCachedToken_ReturnsEmptyContext()
    {
        _fakeOAuth.DeviceCodeToken = null;

        var config = new OAuth2DeviceCodeConfig(
            "https://auth.example.com/device", "https://auth.example.com/token", "client1");

        var ctx = await _sut.GetAuthContextAsync(Server(config), default);

        Assert.Empty(ctx.Headers);
        Assert.Null(ctx.BearerToken);
    }

    [Fact]
    public async Task Mtls_PopulatesCertAndKeyPaths()
    {
        _secrets.ResolveAsync("file:/certs/client.pem", default).Returns(new ValueTask<string?>("/certs/client.pem"));
        _secrets.ResolveAsync("file:/certs/client.key", default).Returns(new ValueTask<string?>("/certs/client.key"));

        var ctx = await _sut.GetAuthContextAsync(
            Server(new MtlsConfig("file:/certs/client.pem", "file:/certs/client.key")), default);

        Assert.Equal("/certs/client.pem", ctx.ClientCertificatePath);
        Assert.Equal("/certs/client.key", ctx.ClientKeyPath);
    }

    [Fact]
    public async Task Bearer_NullSecret_ThrowsCredentialResolutionException()
    {
        _secrets.ResolveAsync("env:TOKEN", default).Returns(new ValueTask<string?>(null as string));

        await Assert.ThrowsAsync<Hypa.Infrastructure.Mcp.Auth.McpCredentialResolutionException>(
            () => _sut.GetAuthContextAsync(Server(new BearerAuthConfig("env:TOKEN")), default).AsTask());
    }

    [Fact]
    public async Task Basic_NullUsername_ThrowsCredentialResolutionException()
    {
        _secrets.ResolveAsync("env:USER", default).Returns(new ValueTask<string?>(null as string));
        _secrets.ResolveAsync("env:PASS", default).Returns(new ValueTask<string?>("p@ss"));

        await Assert.ThrowsAsync<McpCredentialResolutionException>(
            () => _sut.GetAuthContextAsync(Server(new BasicAuthConfig("env:USER", "env:PASS")), default).AsTask());
    }

    [Fact]
    public async Task Basic_NullPassword_ThrowsCredentialResolutionException()
    {
        _secrets.ResolveAsync("env:USER", default).Returns(new ValueTask<string?>("alice"));
        _secrets.ResolveAsync("env:PASS", default).Returns(new ValueTask<string?>(null as string));

        await Assert.ThrowsAsync<McpCredentialResolutionException>(
            () => _sut.GetAuthContextAsync(Server(new BasicAuthConfig("env:USER", "env:PASS")), default).AsTask());
    }

    [Fact]
    public async Task Bearer_RegistersTokenWithRedactionRegistry()
    {
        _secrets.ResolveAsync("env:SECRET", default).Returns(new ValueTask<string?>("super-secret-token"));

        await _sut.GetAuthContextAsync(Server(new BearerAuthConfig("env:SECRET")), default);

        Assert.Equal("[REDACTED]", _redaction.Redact("super-secret-token"));
    }

    [Fact]
    public async Task OAuth2ClientCredentials_RegistersTokenWithRedactionRegistry()
    {
        _fakeOAuth.ClientCredentialsToken = "oauth-secret-xyz";

        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");

        await _sut.GetAuthContextAsync(Server(config), default);

        Assert.Equal("[REDACTED]", _redaction.Redact("oauth-secret-xyz"));
    }

    private sealed class FakeOAuthTokenService : IOAuthTokenService
    {
        public string? ClientCredentialsToken { get; set; }
        public string? DeviceCodeToken { get; set; }

        public Task<string> GetClientCredentialsTokenAsync(
            OAuth2ClientCredentialsConfig config, CancellationToken ct) =>
            Task.FromResult(ClientCredentialsToken ?? string.Empty);

        public Task<string?> GetDeviceCodeTokenAsync(
            OAuth2DeviceCodeConfig config, CancellationToken ct) =>
            Task.FromResult(DeviceCodeToken);
    }
}
