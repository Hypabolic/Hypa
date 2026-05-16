using System.Net;
using System.Text;
using System.Text.Json;
using Hypa.Infrastructure.Updates;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Updates;

public sealed class GitHubReleasesUpdateCheckerTests
{
    private const string Repo = "Hypabolic/Hypa";
    private const string Channel = "stable";
    private const string CurrentVersion = "1.0.0";
    private const string LatestTag = "v1.1.0";
    private const string Rid = "linux-x64";

    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly IConfigLoader _config = Substitute.For<IConfigLoader>();
    private readonly GitHubReleasesUpdateChecker _checker;

    public GitHubReleasesUpdateCheckerTests()
    {
        _config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<HypaConfig, Error>.Ok(new HypaConfig
            {
                ReleaseRepository = Repo,
                UpdateChannel = Channel,
            }));
        _checker = new GitHubReleasesUpdateChecker(_httpFactory, _config);
    }

    // ── 304 Not Modified ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NotModified_ReturnsOkNull()
    {
        SetupHandler(HttpStatusCode.NotModified, "");
        var result = await _checker.CheckAsync(CurrentVersion, Rid, eTag: "abc", CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CheckAsync_ETagSentInRequest()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotModified, "");
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        await _checker.CheckAsync(CurrentVersion, Rid, eTag: "my-etag", CancellationToken.None);
        Assert.Contains("\"my-etag\"", handler.LastRequest?.Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task CheckAsync_NoETag_NoIfNoneMatchHeader()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotModified, "");
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        await _checker.CheckAsync(CurrentVersion, Rid, eTag: null, CancellationToken.None);
        Assert.Empty(handler.LastRequest!.Headers.IfNoneMatch);
    }

    // ── Non-200 errors ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_NonSuccessStatus_ReturnsFail(HttpStatusCode status)
    {
        SetupHandler(status, "");
        var result = await _checker.CheckAsync(CurrentVersion, Rid, eTag: null, CancellationToken.None);
        Assert.False(result.IsOk);
        Assert.Equal("UpdateCheck.HttpError", result.Error.Code);
    }

    // ── Network failure ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NetworkError_ReturnsFail()
    {
        _httpFactory.CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused"))));
        var result = await _checker.CheckAsync(CurrentVersion, Rid, eTag: null, CancellationToken.None);
        Assert.False(result.IsOk);
        Assert.Equal("UpdateCheck.NetworkError", result.Error.Code);
    }

    // ── Deserialization ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsFail()
    {
        SetupHandler(HttpStatusCode.OK, "not-json");
        var result = await _checker.CheckAsync(CurrentVersion, Rid, eTag: null, CancellationToken.None);
        Assert.False(result.IsOk);
        Assert.Equal("UpdateCheck.ParseError", result.Error.Code);
    }

    // ── Asset selection ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_LinuxX64_SelectsTarGzAsset()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson(LatestTag, [
            ("hypa-linux-x64.tar.gz", "https://example.com/hypa-linux-x64.tar.gz"),
            ("hypa-osx-arm64.tar.gz",  "https://example.com/hypa-osx-arm64.tar.gz"),
            ("SHA256SUMS",             "https://example.com/SHA256SUMS"),
        ]));
        var result = await _checker.CheckAsync(CurrentVersion, "linux-x64", eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Equal("https://example.com/hypa-linux-x64.tar.gz", result.Value!.DownloadUrl);
        Assert.Equal("hypa-linux-x64.tar.gz", result.Value.AssetName);
    }

    [Fact]
    public async Task CheckAsync_MissingAsset_DownloadUrlIsNull()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson(LatestTag, [
            ("SHA256SUMS", "https://example.com/SHA256SUMS"),
        ]));
        var result = await _checker.CheckAsync(CurrentVersion, "linux-x64", eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Null(result.Value!.DownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_MissingChecksums_ChecksumsUrlIsNull()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson(LatestTag, [
            ("hypa-linux-x64.tar.gz", "https://example.com/hypa-linux-x64.tar.gz"),
        ]));
        var result = await _checker.CheckAsync(CurrentVersion, "linux-x64", eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.Null(result.Value!.ChecksumsUrl);
    }

    // ── Version comparison ────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NewerRelease_IsUpdateAvailableTrue()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson("v1.1.0", []));
        var result = await _checker.CheckAsync("1.0.0", Rid, eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.True(result.Value!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_IsUpdateAvailableFalse()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson("v1.0.0", []));
        var result = await _checker.CheckAsync("1.0.0", Rid, eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.False(result.Value!.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_OlderRelease_IsUpdateAvailableFalse()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson("v0.9.0", []));
        var result = await _checker.CheckAsync("1.0.0", Rid, eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.False(result.Value!.IsUpdateAvailable);
    }

    // ── UpdateInfo fields ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_PopulatesUpdateInfoFields()
    {
        SetupHandler(HttpStatusCode.OK, MakeReleaseJson(LatestTag, [
            ("hypa-linux-x64.tar.gz", "https://example.com/dl.tar.gz"),
            ("SHA256SUMS",             "https://example.com/sums"),
        ]), eTag: "test-etag");
        var result = await _checker.CheckAsync(CurrentVersion, Rid, eTag: null, CancellationToken.None);
        Assert.True(result.IsOk);
        var info = result.Value!;
        Assert.Equal(CurrentVersion, info.CurrentVersion);
        Assert.Equal("1.1.0", info.LatestVersion);
        Assert.Equal(Rid, info.RuntimeIdentifier);
        Assert.Equal(Repo, info.Repo);
        Assert.Equal(Channel, info.Channel);
        Assert.Equal("test-etag", info.ETag);
        Assert.Equal("https://example.com/sums", info.ChecksumsUrl);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupHandler(HttpStatusCode status, string body, string? eTag = null)
    {
        var handler = new FakeResponseHandler(status, body, eTag);
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
    }

    private static string MakeReleaseJson(string tag, IEnumerable<(string Name, string Url)> assets)
    {
        var assetJson = string.Join(",", assets.Select(a =>
            $$"""{"name":{{JsonSerializer.Serialize(a.Name)}},"browser_download_url":{{JsonSerializer.Serialize(a.Url)}}}"""));
        return $$"""{"tag_name":{{JsonSerializer.Serialize(tag)}},"html_url":"https://example.com/releases/{{tag}}","assets":[{{assetJson}}]}""";
    }

    private sealed class FakeResponseHandler(HttpStatusCode status, string body, string? eTag = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (eTag is not null)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{eTag}\"");
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw ex;
    }
}
