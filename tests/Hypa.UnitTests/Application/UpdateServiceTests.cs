using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Updates;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class UpdateServiceTests
{
    private const string CurrentVersion = "0.1.0";
    private const string LatestVersion = "0.2.0";
    private const string Rid = "linux-x64";
    private const string Repo = "Hypabolic/Hypa";
    private const string Channel = "stable";

    private readonly IUpdateChecker _checker = Substitute.For<IUpdateChecker>();
    private readonly IUpdateCheckCache _cache = Substitute.For<IUpdateCheckCache>();
    private readonly IInstallMetadataStore _metaStore = Substitute.For<IInstallMetadataStore>();
    private readonly IVersionProvider _version = Substitute.For<IVersionProvider>();
    private readonly IRuntimeIdentifierProvider _rid = Substitute.For<IRuntimeIdentifierProvider>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IConfigLoader _config = Substitute.For<IConfigLoader>();
    private readonly UpdateService _service;

    private static readonly DateTimeOffset Now = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    public UpdateServiceTests()
    {
        _version.CurrentVersion.Returns(CurrentVersion);
        _rid.RuntimeIdentifier.Returns(Rid);
        _clock.UtcNow.Returns(Now);
        SetConfig(updateCheckEnabled: true);
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns((UpdateInfo?)null);
        _metaStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata("unknown"));

        _service = new UpdateService(
            _checker, _cache, _metaStore,
            [new Hypa.Infrastructure.Updates.ScriptInstallUpdateStrategy(
                Substitute.For<IHttpClientFactory>(), _metaStore),
             new Hypa.Infrastructure.Updates.PackageManagerUpdateStrategy(),
             new Hypa.Infrastructure.Updates.ManualUpdateStrategy()],
            _version, _rid, _clock, _config);
    }

    // ── Disabled check ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_ChecksDisabled_ReturnsUpToDateWithoutCallingChecker()
    {
        SetConfig(updateCheckEnabled: false);

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(result.Value.IsUpdateAvailable);
        Assert.Equal(CurrentVersion, result.Value.CurrentVersion);
        await _checker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Fresh cache ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_FreshMatchingCache_ReturnsCachedWithoutCallingChecker()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromHours(1));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(LatestVersion, result.Value.LatestVersion);
        await _checker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCachedInfoAsync_FreshMatchingCache_ReturnsCachedWithoutCallingChecker()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromHours(1));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);

        var result = await _service.GetCachedInfoAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(LatestVersion, result.LatestVersion);
        await _checker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCachedInfoAsync_ExpiredMatchingCache_ReturnsNull()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromHours(25));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);

        var result = await _service.GetCachedInfoAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedInfoAsync_CacheKeyMismatch_ReturnsNull()
    {
        var cached = MakeUpdateInfo(repo: "other/repo", checkedAt: Now - TimeSpan.FromHours(1));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);

        var result = await _service.GetCachedInfoAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_ExpiredCache_CallsChecker()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromHours(25));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUpdateInfoAsync_ForceRefresh_CallsCheckerEvenWhenCacheFresh()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromMinutes(5));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: true, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Cache key invalidation ─────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_CacheDifferentVersion_CallsChecker()
    {
        var cached = MakeUpdateInfo(currentVersion: "0.0.9", checkedAt: Now - TimeSpan.FromMinutes(5));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUpdateInfoAsync_CacheDifferentRepo_CallsChecker()
    {
        var cached = MakeUpdateInfo(repo: "other/repo", checkedAt: Now - TimeSpan.FromMinutes(5));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUpdateInfoAsync_CacheNullRepo_ForcesRefresh()
    {
        var cached = MakeUpdateInfo(repo: null, checkedAt: Now - TimeSpan.FromMinutes(5));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUpdateInfoAsync_CacheNullChannel_ForcesRefresh()
    {
        var cached = MakeUpdateInfo(channel: null, checkedAt: Now - TimeSpan.FromMinutes(5));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Network failure fallback ──────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_NetworkFailure_MatchingCache_ReturnsCached()
    {
        var cached = MakeUpdateInfo(checkedAt: Now - TimeSpan.FromHours(25));   // expired but keys match
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Fail(new Error("NetworkError", "timeout")));

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(LatestVersion, result.Value.LatestVersion);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_NetworkFailure_NoCachedData_ReturnsFail()
    {
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns((UpdateInfo?)null);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Fail(new Error("NetworkError", "timeout")));

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_NetworkFailure_CacheWrongVersion_ReturnsFail()
    {
        var cached = MakeUpdateInfo(currentVersion: "0.0.1");
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Fail(new Error("NetworkError", "timeout")));

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_NetworkFailure_CacheWrongRepo_ReturnsFail()
    {
        var cached = MakeUpdateInfo(repo: "other/repo");
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Fail(new Error("NetworkError", "timeout")));

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.False(result.IsOk);
    }

    // ── 304 not-modified ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_NotModified_RefreshesCheckedAtAndSaves()
    {
        var oldCheckedAt = Now - TimeSpan.FromHours(25);
        var cached = MakeUpdateInfo(checkedAt: oldCheckedAt);
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(null));   // 304

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(Now, result.Value.CheckedAt);
        await _cache.Received(1).SaveAsync(Arg.Any<UpdateInfo>(), Arg.Any<CancellationToken>());
    }

    // ── 304 key mismatch ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetUpdateInfoAsync_NotModified_CacheKeyMismatch_ReturnsFail()
    {
        // Cache exists but for a different repo — keys don't match, so 304 cannot be satisfied.
        var cached = MakeUpdateInfo(repo: "other/repo", checkedAt: Now - TimeSpan.FromHours(25));
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(null));   // 304

        var result = await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("UpdateCheck.UnexpectedNotModified", result.Error.Code);
    }

    [Fact]
    public async Task GetUpdateInfoAsync_CacheKeyMismatch_DoesNotSendETag()
    {
        // Cache exists but for a different repo — ETag must NOT be forwarded.
        var cached = MakeUpdateInfo(repo: "other/repo", checkedAt: Now - TimeSpan.FromMinutes(5),
            eTag: "\"abc123\"");
        _cache.GetAsync(Arg.Any<CancellationToken>()).Returns(cached);
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo?, Error>.Ok(MakeUpdateInfo()));

        await _service.GetUpdateInfoAsync(forceRefresh: false, CancellationToken.None);

        await _checker.Received(1).CheckAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string?>(e => e == null),
            Arg.Any<CancellationToken>());
    }

    // ── Strategy selection ────────────────────────────────────────────────────

    [Fact]
    public async Task PlanUpdateAsync_ScriptMetadata_UsesScriptStrategy()
    {
        _metaStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata("script"));
        var update = MakeUpdateInfo();

        var result = await _service.PlanUpdateAsync(update, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("script", result.Value.Strategy);
    }

    [Fact]
    public async Task PlanUpdateAsync_HomebrewMetadata_UsesPackageManagerStrategy()
    {
        _metaStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata("homebrew"));
        var update = MakeUpdateInfo();

        var result = await _service.PlanUpdateAsync(update, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("package-manager", result.Value.Strategy);
        Assert.False(result.Value.CanAutoUpdate);
    }

    [Fact]
    public async Task PlanUpdateAsync_UnknownMetadata_UsesManualStrategy()
    {
        _metaStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata("unknown"));
        var update = MakeUpdateInfo();

        var result = await _service.PlanUpdateAsync(update, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("manual", result.Value.Strategy);
        Assert.False(result.Value.CanAutoUpdate);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetConfig(bool updateCheckEnabled = true)
    {
        var cfg = new HypaConfig
        {
            UpdateCheckEnabled = updateCheckEnabled,
            ReleaseRepository = Repo,
            UpdateChannel = Channel,
        };
        _config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<HypaConfig, Error>.Ok(cfg));
    }

    private static UpdateInfo MakeUpdateInfo(
        string? currentVersion = null,
        DateTimeOffset? checkedAt = null,
        string? repo = Repo,
        string? channel = Channel,
        string? eTag = null) =>
        new(CurrentVersion: currentVersion ?? CurrentVersion,
            LatestVersion: LatestVersion,
            ReleaseUrl: "https://example.com/releases/v0.2.0",
            AssetName: "hypa-linux-x64.tar.gz",
            DownloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            ChecksumsUrl: "https://example.com/SHA256SUMS",
            RuntimeIdentifier: Rid,
            IsUpdateAvailable: true,
            CheckedAt: checkedAt ?? Now,
            ETag: eTag,
            Repo: repo,
            Channel: channel);

    private static InstallMetadata MakeMetadata(string source) =>
        new(Source: source, RuntimeIdentifier: Rid,
            InstallDirectory: "/home/user/.local/share/hypa",
            BinLinkPath: "/home/user/.local/bin/hypa",
            ExecutablePath: "/home/user/.local/share/hypa/hypa",
            InstalledVersion: null, InstalledAt: null);
}
