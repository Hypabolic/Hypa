using System.Net;
using System.Text;
using System.Text.Json;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Secrets;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class OAuthTokenServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-oauth-test-{Guid.NewGuid():N}");
    private readonly ISecretResolver _secrets = Substitute.For<ISecretResolver>();

    public OAuthTokenServiceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private OAuthTokenService BuildSut(HttpMessageHandler handler, OAuthTokenCache? cache = null) =>
        new(
            new FakeHttpClientFactory(handler),
            _secrets,
            cache ?? new OAuthTokenCache(),
            new HypaDataOptions { DataDirectory = _tempDir },
            NullLogger<OAuthTokenService>.Instance);

    private static HttpMessageHandler RespondWith(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        });
        return new StaticResponseHandler(json, status);
    }

    [Fact]
    public async Task GetClientCredentialsTokenAsync_FirstCall_MakesHttpRequest()
    {
        var handler = RespondWith(new { access_token = "tok1", token_type = "bearer", expires_in = 3600 });
        _secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("resolved"));

        var sut = BuildSut(handler);
        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");

        var token = await sut.GetClientCredentialsTokenAsync(config, default);

        Assert.Equal("tok1", token);
    }

    [Fact]
    public async Task GetClientCredentialsTokenAsync_SecondCall_ReturnsCachedToken()
    {
        var callCount = 0;
        var handler = new CountingHandler(
            () => JsonSerializer.Serialize(new { access_token = $"tok{++callCount}", token_type = "bearer", expires_in = 3600 },
                new JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

        _secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("resolved"));

        var cache = new OAuthTokenCache();
        var sut = BuildSut(handler, cache);
        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");

        var first = await sut.GetClientCredentialsTokenAsync(config, default);
        var second = await sut.GetClientCredentialsTokenAsync(config, default);

        Assert.Equal("tok1", first);
        Assert.Equal("tok1", second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetClientCredentialsTokenAsync_ExpiredToken_TriggersRefresh()
    {
        _secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("resolved"));

        var cache = new OAuthTokenCache();
        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");
        var cacheKey = $"{config.ClientIdRef}@{config.TokenUrl}";

        cache.Set(cacheKey, "old-token", expiresIn: -10);

        var handler = RespondWith(new { access_token = "new-token", token_type = "bearer", expires_in = 3600 });
        var sut = BuildSut(handler, cache);

        var token = await sut.GetClientCredentialsTokenAsync(config, default);
        Assert.Equal("new-token", token);
    }

    [Fact]
    public async Task GetClientCredentialsTokenAsync_TokenExpiringWithin60s_TriggersRefresh()
    {
        _secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("resolved"));

        var cache = new OAuthTokenCache();
        var config = new OAuth2ClientCredentialsConfig(
            "https://auth.example.com/token", "env:CID", "env:CSECRET");
        var cacheKey = $"{config.ClientIdRef}@{config.TokenUrl}";

        cache.Set(cacheKey, "old-token", expiresIn: 30);

        var handler = RespondWith(new { access_token = "refreshed-token", token_type = "bearer", expires_in = 3600 });
        var sut = BuildSut(handler, cache);

        var token = await sut.GetClientCredentialsTokenAsync(config, default);
        Assert.Equal("refreshed-token", token);
    }

    [Fact]
    public async Task GetDeviceCodeTokenAsync_EmptyCache_ReturnsNull()
    {
        var sut = BuildSut(new StaticResponseHandler("{}", HttpStatusCode.OK));
        var config = new OAuth2DeviceCodeConfig(
            "https://auth.example.com/device", "https://auth.example.com/token", "client1");

        var token = await sut.GetDeviceCodeTokenAsync(config, default);
        Assert.Null(token);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;

        public StaticResponseHandler(string json, HttpStatusCode status)
        {
            _json = json;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<string> _jsonFactory;
        public CountingHandler(Func<string> jsonFactory) => _jsonFactory = jsonFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonFactory(), Encoding.UTF8, "application/json"),
            });
    }
}
