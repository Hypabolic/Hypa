using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class PackageManagerOutputCompressorTests
{
    private static PackageManagerOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation PkgInvocation(string exe, params string[] args) =>
        CommandInvocation.Buffered(exe, args, $"{exe} {string.Join(' ', args)}");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    [Theory]
    [InlineData("npm")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    [InlineData("/usr/local/bin/npm")]
    [InlineData("/usr/local/bin/pnpm")]
    [InlineData("/usr/local/bin/yarn")]
    [InlineData(@"C:\Program Files\nodejs\npm")]
    [InlineData(@"C:\Program Files\nodejs\pnpm")]
    [InlineData(@"C:\Program Files\nodejs\yarn")]
    [InlineData("npm.cmd")]
    [InlineData("pnpm.cmd")]
    [InlineData("yarn.cmd")]
    [InlineData("npm.bat")]
    [InlineData("pnpm.bat")]
    [InlineData("yarn.bat")]
    [InlineData("npm.exe")]
    [InlineData("pnpm.exe")]
    [InlineData("yarn.exe")]
    [InlineData("NpM")]
    [InlineData("PnPm")]
    [InlineData("YaRn")]
    [InlineData("/usr/local/bin/NpM.CmD")]
    [InlineData(@"C:\Program Files\nodejs\PnPm.ExE")]
    [InlineData("/opt/bin/YaRn.CMD")]
    [InlineData("/usr/local/bin/NpM.BaT")]
    [InlineData(@"C:\Program Files\nodejs\PnPm.BaT")]
    [InlineData("/opt/bin/YaRn.BAT")]
    public void CanHandle_PackageManagerExecutable_ReturnsTrue(string executable) =>
        Assert.True(Make().CanHandle(PkgInvocation(executable, "install")));

    [Theory]
    [InlineData("/usr/local/share/npm/node")]
    [InlineData("/opt/pnpm/runner")]
    [InlineData(@"C:\tools\yarn\custom.exe")]
    public void CanHandle_UnrelatedPath_ReturnsFalse(string executable) =>
        Assert.False(Make().CanHandle(PkgInvocation(executable, "install")));

    [Theory]
    [InlineData("npm.sh")]
    [InlineData("pnpm.backup")]
    [InlineData("yarn.local")]
    [InlineData("/usr/local/bin/npm.sh")]
    [InlineData(@"C:\Program Files\nodejs\pnpm.backup")]
    [InlineData("/opt/bin/yarn.local")]
    [InlineData("/usr/local/bin/NpM.Sh")]
    [InlineData(@"C:\Program Files\nodejs\PnPm.BaCkUp")]
    [InlineData("/opt/bin/YaRn.LoCaL")]
    public void CanHandle_ArbitraryExecutableSuffix_ReturnsFalse(string executable) =>
        Assert.False(Make().CanHandle(PkgInvocation(executable, "install")));

    [Fact]
    public void CanHandle_CustomPackageScripts_UsesRawPackageManagerExecutable()
    {
        var compressor = Make();

        Assert.True(compressor.CanHandle(PkgInvocation("pnpm", "lint")));
        Assert.True(compressor.CanHandle(PkgInvocation("npm", "run", "lint")));
        Assert.True(compressor.CanHandle(PkgInvocation("yarn", "format")));
    }

    [Fact]
    public void CanHandle_Git_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("git", ["status"], "git status")));

    [Fact]
    public void Compress_ReducerId_IsPkgManager()
    {
        var input = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"npm ERR! {i}"));
        var result = Make().Compress(PkgInvocation("npm", "install"), Out(input, 1), CompressionOptions.Default);
        Assert.Equal("pkg-manager", result.ReducerId);
    }

    [Fact]
    public void Compress_SuccessSmallOutput_ReturnsPassthrough()
    {
        var input = "added 10 packages\n";
        var result = Make().Compress(PkgInvocation("pnpm", "install"), Out(input, 0), CompressionOptions.Default);
        Assert.Equal("passthrough", result.ReducerId);
    }

    [Theory]
    [InlineData("", "", 0, "", "passthrough")]
    [InlineData("stdout only", "", 0, "stdout only", "passthrough")]
    [InlineData("", "stderr only", 0, "stderr only", "passthrough")]
    [InlineData("stdout", "stderr", 0, "stdout\nstderr", "passthrough")]
    [InlineData("", "npm ERR! install failed", 1, "npm ERR! install failed", "pkg-manager")]
    public void Compress_CombinesStreamsWithoutSyntheticBlankLines(
        string stdout,
        string stderr,
        int exitCode,
        string expectedText,
        string expectedReducerId)
    {
        var output = CommandOutput.Captured(stdout, stderr, exitCode, TimeSpan.Zero);

        var result = Make().Compress(
            PkgInvocation("npm", "install"),
            output,
            CompressionOptions.Default);

        Assert.Equal(expectedText, result.Text);
        Assert.Equal(expectedReducerId, result.ReducerId);
    }

    [Fact]
    public void Compress_PreservesNpmErrorLines()
    {
        var big = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"npm ERR! code E{i}: failure at step {i}"));
        var result = Make().Compress(PkgInvocation("npm", "install"), Out(big, 1), CompressionOptions.Default);
        Assert.Contains("npm ERR!", result.Text);
    }

    [Fact]
    public void Compress_PreservesPeerConflictLine()
    {
        var input = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"   downloading package {i}..."));
        input += "\npeer conflict: incompatible versions between react and react-dom\n";
        var result = Make().Compress(PkgInvocation("pnpm", "install"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("peer conflict", result.Text);
    }

    [Fact]
    public void Compress_PreservesInstallSummary()
    {
        var noise = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"  downloading {i}..."));
        var input = noise + "\nadded 150 packages in 12s\n";
        var result = Make().Compress(PkgInvocation("pnpm", "install"), Out(input), CompressionOptions.Default);
        Assert.Contains("added 150 packages", result.Text);
    }

    [Fact]
    public void Compress_DropsProgressLines()
    {
        var input = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"   downloading package {i}..."));
        input += "\nnpm ERR! fatal: network error\n";
        var result = Make().Compress(PkgInvocation("npm", "install"), Out(input, 1), CompressionOptions.Default);
        Assert.DoesNotContain("downloading package", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var lines = Enumerable.Range(1, 200).Select(i =>
            i % 20 == 0 ? $"npm ERR! step {i} failed" : $"  downloading {i}...").ToList();
        var result = Make().Compress(PkgInvocation("npm", "install"), Out(string.Join('\n', lines), 1), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
