using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Hypa.Runtime.Domain.Updates;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

public sealed class UpdateAvailableCheckTests
{
    private const string CurrentVersion = "1.0.0";
    private const string LatestVersion = "1.1.0";
    private const string Rid = "linux-x64";

    private readonly IConfigLoader _config = Substitute.For<IConfigLoader>();
    private readonly IInstallMetadataStore _metadataStore = Substitute.For<IInstallMetadataStore>();
    private readonly IUpdateService _updateService = Substitute.For<IUpdateService>();
    private readonly UpdateAvailableCheck _check;

    public UpdateAvailableCheckTests()
    {
        SetConfig();
        _updateService.GetCachedInfoAsync(Arg.Any<CancellationToken>())
            .Returns((UpdateInfo?)null);
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Ok(MakeUpdateInfo(isUpdateAvailable: false)));
        _metadataStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata("script"));
        _check = new UpdateAvailableCheck(_config, _metadataStore, _updateService);
    }

    [Fact]
    public void Category_IsUpdate() => Assert.Equal("Update", _check.Category);

    // ── Checks disabled ───────────────────────────────────────────────────────

    [Fact]
    public void Run_ChecksDisabled_ReturnsDisabledStatus()
    {
        SetConfig(updateCheckEnabled: false);
        var result = _check.Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Equal("update checks disabled", result.Value);
    }

    // ── Service failure / timeout ──────────────────────────────────────────────

    [Fact]
    public void Run_ServiceReturnsFail_ReturnsWarnWithCheckFailed()
    {
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Fail(new Error("UpdateCheck.NetworkError", "connection refused")));
        var result = _check.Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("check failed", result.Value);
        Assert.Equal("connection refused", result.Detail);
    }

    [Fact]
    public void Run_ServiceThrowsOperationCanceled_ReturnsWarnWithTimedOut()
    {
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<UpdateInfo, Error>>>(_ => throw new OperationCanceledException());
        var result = _check.Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Equal("check timed out", result.Value);
    }

    // ── Up to date ────────────────────────────────────────────────────────────

    [Fact]
    public void Run_UpToDate_ReturnsOkWithVersion()
    {
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Ok(MakeUpdateInfo(isUpdateAvailable: false)));
        var result = _check.Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
        Assert.Contains("up to date", result.Value);
        Assert.Contains(CurrentVersion, result.Value);
    }

    // ── Update available ──────────────────────────────────────────────────────

    [Fact]
    public void Run_UpdateAvailable_ReturnsWarnWithBothVersions()
    {
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Ok(MakeUpdateInfo(isUpdateAvailable: true)));
        var result = _check.Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains(LatestVersion, result.Value);
        Assert.Contains(CurrentVersion, result.Value);
    }

    [Fact]
    public void Run_FreshCachedUpdateAvailable_ReturnsCachedResultWithoutRefresh()
    {
        _updateService.GetCachedInfoAsync(Arg.Any<CancellationToken>())
            .Returns(MakeUpdateInfo(isUpdateAvailable: true));

        var result = _check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains(LatestVersion, result.Value);
        _updateService.DidNotReceive().GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Hint text per source ──────────────────────────────────────────────────

    [Theory]
    [InlineData("script", "hypa update")]
    [InlineData("homebrew", "brew upgrade hypa")]
    [InlineData("winget", "winget upgrade hypa")]
    [InlineData("scoop", "scoop update hypa")]
    [InlineData("apt", "sudo apt update && sudo apt install --only-upgrade hypa")]
    [InlineData("dnf", "sudo dnf upgrade hypa")]
    [InlineData("unknown", "hypa update")]
    public void Run_UpdateAvailable_HintContainsCorrectCommand(string source, string expectedCommand)
    {
        _metadataStore.GetAsync(Arg.Any<CancellationToken>()).Returns(MakeMetadata(source));
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Ok(MakeUpdateInfo(isUpdateAvailable: true)));
        var result = _check.Run();
        Assert.NotNull(result.Detail);
        Assert.Contains(expectedCommand, result.Detail);
    }

    [Fact]
    public void Run_UpdateAvailable_MetadataStoreThrows_FallsBackToHypaUpdate()
    {
        _metadataStore.GetAsync(Arg.Any<CancellationToken>())
            .Returns<Task<InstallMetadata>>(_ => throw new InvalidOperationException("storage unavailable"));
        _updateService.GetUpdateInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Result<UpdateInfo, Error>.Ok(MakeUpdateInfo(isUpdateAvailable: true)));
        var result = _check.Run();
        Assert.NotNull(result.Detail);
        Assert.Contains("hypa update", result.Detail);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetConfig(bool updateCheckEnabled = true)
    {
        _config.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<HypaConfig, Error>.Ok(new HypaConfig
            {
                UpdateCheckEnabled = updateCheckEnabled,
            }));
    }

    private static UpdateInfo MakeUpdateInfo(bool isUpdateAvailable) =>
        new(CurrentVersion: CurrentVersion,
            LatestVersion: LatestVersion,
            ReleaseUrl: "https://example.com/releases/v1.1.0",
            AssetName: "hypa-linux-x64.tar.gz",
            DownloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            ChecksumsUrl: "https://example.com/SHA256SUMS",
            RuntimeIdentifier: Rid,
            IsUpdateAvailable: isUpdateAvailable,
            CheckedAt: DateTimeOffset.UtcNow,
            ETag: null,
            Repo: "Hypabolic/Hypa",
            Channel: "stable");

    private static InstallMetadata MakeMetadata(string source) =>
        new(Source: source, RuntimeIdentifier: Rid,
            InstallDirectory: "/home/user/.local/share/hypa",
            BinLinkPath: "/home/user/.local/bin/hypa",
            ExecutablePath: "/home/user/.local/share/hypa/hypa",
            InstalledVersion: null, InstalledAt: null);
}
