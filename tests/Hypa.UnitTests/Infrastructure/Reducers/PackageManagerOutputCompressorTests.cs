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

    [Fact]
    public void CanHandle_Pnpm_ReturnsTrue() =>
        Assert.True(Make().CanHandle(PkgInvocation("pnpm", "install")));

    [Fact]
    public void CanHandle_Npm_ReturnsTrue() =>
        Assert.True(Make().CanHandle(PkgInvocation("npm", "install")));

    [Fact]
    public void CanHandle_Yarn_ReturnsTrue() =>
        Assert.True(Make().CanHandle(PkgInvocation("yarn")));

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
