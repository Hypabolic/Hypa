using Hypa.Infrastructure.Updates;
using Hypa.Runtime.Domain.Updates;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Updates;

public sealed class NonAutoUpdateStrategyTests
{
    [Fact]
    public async Task ManualApplyAsync_ReturnsStructuredFailure()
    {
        var strategy = new ManualUpdateStrategy();

        var result = await strategy.ApplyAsync(MakeUpdate(), MakeMetadata("unknown"), CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.ManualRequired", result.Error.Code);
        Assert.Equal("Manual update required.", result.Error.Message);
    }

    [Fact]
    public async Task PackageManagerApplyAsync_ReturnsStructuredFailure()
    {
        var strategy = new PackageManagerUpdateStrategy();

        var result = await strategy.ApplyAsync(MakeUpdate(), MakeMetadata("homebrew"), CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Update.PackageManagerRequired", result.Error.Code);
        Assert.Equal("This install is managed by homebrew.", result.Error.Message);
    }

    private static UpdateInfo MakeUpdate() =>
        new(CurrentVersion: "0.1.0",
            LatestVersion: "0.2.0",
            ReleaseUrl: "https://example.com/releases/v0.2.0",
            AssetName: "hypa-linux-x64.tar.gz",
            DownloadUrl: "https://example.com/hypa-linux-x64.tar.gz",
            ChecksumsUrl: "https://example.com/SHA256SUMS",
            RuntimeIdentifier: "linux-x64",
            IsUpdateAvailable: true,
            CheckedAt: DateTimeOffset.UtcNow);

    private static InstallMetadata MakeMetadata(string source) =>
        new(Source: source,
            RuntimeIdentifier: "linux-x64",
            InstallDirectory: "/home/user/.local/share/hypa",
            BinLinkPath: "/home/user/.local/bin/hypa",
            ExecutablePath: "/home/user/.local/share/hypa/hypa",
            InstalledVersion: null,
            InstalledAt: null);
}
