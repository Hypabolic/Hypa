using Hypa.Infrastructure.Updates;
using Hypa.Runtime.Domain.Updates;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class InstallSourceDetectionTests
{
    private const string Home = "/home/user";

    private static string Detect(string processPath, Func<string, string?>? resolve = null) =>
        InstallMetadataStore.DetectSource(
            processPath,
            isWindows: false,
            home: Home,
            localAppData: Home + "/.local/share",
            tryResolveSymlink: resolve ?? (_ => null));

    [Theory]
    [InlineData("/opt/homebrew/Cellar/hypa/1.0.0/libexec/hypa")]
    [InlineData("/usr/local/Cellar/hypa/1.0.0/libexec/hypa")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/hypa/1.0.0/libexec/hypa")]
    [InlineData("/opt/homebrew/bin/hypa")]
    public void Homebrew_PathsAreDetected(string path) =>
        Assert.Equal("homebrew", Detect(path));

    [Theory]
    [InlineData("/usr/local/lib/node_modules/@hypabolic/hypa/node_modules/@hypabolic/hypa-darwin-arm64/bin/hypa")]
    [InlineData("/home/user/.nvm/versions/node/v22.1.0/lib/node_modules/@hypabolic/hypa-linux-x64/bin/hypa")]
    public void Npm_PlatformPackagePathsAreDetected(string path) =>
        Assert.Equal("npm", Detect(path));

    [Fact]
    public void Pnpm_NestedStorePathIsDetected() =>
        Assert.Equal("pnpm", Detect(
            "/home/user/.local/share/pnpm/global/5/node_modules/.pnpm/@hypabolic+hypa-linux-x64@1.0.0/node_modules/@hypabolic/hypa-linux-x64/bin/hypa"));

    [Theory]
    [InlineData("/home/user/venv/lib/python3.12/site-packages/hypa/bin/hypa", "pip")]
    [InlineData("/usr/local/lib/python3.12/dist-packages/hypa/bin/hypa", "pip")]
    [InlineData("/home/user/.local/pipx/venvs/hypa/lib/python3.12/site-packages/hypa/bin/hypa", "pipx")]
    [InlineData("/home/user/.local/share/uv/tools/hypa/lib/python3.12/site-packages/hypa/bin/hypa", "uv")]
    public void Python_WheelPathsAreDetected(string path, string expected) =>
        Assert.Equal(expected, Detect(path));

    [Fact]
    public void WindowsStylePaths_AreNormalizedBeforeMatching() =>
        Assert.Equal("pip", InstallMetadataStore.DetectSource(
            @"C:\Users\u\venv\Lib\site-packages\hypa\bin\hypa.exe",
            isWindows: true,
            home: @"C:\Users\u",
            localAppData: @"C:\Users\u\AppData\Local",
            tryResolveSymlink: _ => null));

    [Fact]
    public void ScriptInstall_IsStillDetected() =>
        Assert.Equal("script", Detect(Home + "/.local/share/hypa/hypa"));

    [Fact]
    public void ScriptInstall_VersionedDirectoryBehindSymlink_IsStillDetected() =>
        Assert.Equal("script", Detect(
            Home + "/.local/share/hypa-0123abcd/hypa",
            path => path == Home + "/.local/share/hypa" ? Home + "/.local/share/hypa-0123abcd" : null));

    [Theory]
    [InlineData("")]
    [InlineData("/usr/bin/hypa")]
    [InlineData("/home/user/src/Hypa/src/Hypa.Cli/bin/Release/net10.0/hypa")]
    public void Unrecognized_PathsAreUnknown(string path) =>
        Assert.Equal("unknown", Detect(path));

    [Theory]
    [InlineData("homebrew", "brew upgrade hypa")]
    [InlineData("npm", "npm install -g @hypabolic/hypa@latest")]
    [InlineData("pnpm", "pnpm add -g @hypabolic/hypa@latest")]
    [InlineData("pip", "python3 -m pip install --upgrade hypa")]
    [InlineData("pipx", "pipx upgrade hypa")]
    [InlineData("uv", "uv tool upgrade hypa")]
    public async Task PackageManagerStrategy_PlansTheMatchingUpgradeCommand(string source, string command)
    {
        var strategy = new PackageManagerUpdateStrategy();
        var metadata = new InstallMetadata(source, "osx-arm64", null, null, null, null, null);
        var update = new UpdateInfo(
            CurrentVersion: "1.0.0", LatestVersion: "1.0.1", ReleaseUrl: "https://example.test/release",
            AssetName: "hypa-osx-arm64.tar.gz", DownloadUrl: null, ChecksumsUrl: null,
            RuntimeIdentifier: "osx-arm64", IsUpdateAvailable: true, CheckedAt: DateTimeOffset.UnixEpoch,
            Repo: "Hypabolic/Hypa", Channel: "stable");

        Assert.True(strategy.CanHandle(metadata));
        var plan = await strategy.PlanAsync(update, metadata, CancellationToken.None);

        Assert.True(plan.IsOk);
        Assert.False(plan.Value.CanAutoUpdate);
        Assert.Equal(command, plan.Value.Command);
        Assert.Contains(command, plan.Value.Detail);
        Assert.Contains("hypa mux stop", plan.Value.Detail);
    }
}
