using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class DotnetBuildOutputCompressorTests
{
    private static DotnetBuildOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation BuildInvocation(params string[] args) =>
        CommandInvocation.Buffered("dotnet", args, $"dotnet {string.Join(' ', args)}");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    [Fact]
    public void CanHandle_DotnetBuild_ReturnsTrue() =>
        Assert.True(Make().CanHandle(BuildInvocation("build")));

    [Fact]
    public void CanHandle_DotnetTest_ReturnsFalse() =>
        Assert.False(Make().CanHandle(BuildInvocation("test")));

    [Fact]
    public void CanHandle_DotnetBuildWithFlags_ReturnsTrue() =>
        Assert.True(Make().CanHandle(BuildInvocation("build", "-c", "Release")));

    [Fact]
    public void CanHandle_Git_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("git", ["status"], "git status")));

    [Fact]
    public void Compress_ReducerId_IsDotnetBuild()
    {
        var result = Make().Compress(BuildInvocation("build"), Out("Build succeeded.\n"), CompressionOptions.Default);
        Assert.Equal("dotnet-build", result.ReducerId);
    }

    [Fact]
    public void Compress_PreservesBuildFailedLine()
    {
        var input = "Determining projects to restore...\nBuild FAILED.\n\n0 Error(s)\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("Build FAILED", result.Text);
    }

    [Fact]
    public void Compress_PreservesBuildSucceededLine()
    {
        var input = "  Foo -> foo.dll\nBuild succeeded.\n\nTime Elapsed 00:00:03.14\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input), CompressionOptions.Default);
        Assert.Contains("Build succeeded", result.Text);
    }

    [Fact]
    public void Compress_PreservesMsBuildDiagnosticWithCode()
    {
        var input = "src/Foo.cs(12,5): error CS0103: The name 'bar' does not exist in the current context\nBuild FAILED.\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("CS0103", result.Text);
        Assert.Contains("src/Foo.cs", result.Text);
    }

    [Fact]
    public void Compress_PreservesWarningDiagnostic()
    {
        var input = "src/Bar.cs(7,3): warning CS8600: Converting null literal\nBuild succeeded.\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input), CompressionOptions.Default);
        Assert.Contains("CS8600", result.Text);
    }

    [Fact]
    public void Compress_PreservesProjectTargetLine()
    {
        var input = "  Hypa.Runtime -> /repo/bin/net10.0/Hypa.Runtime.dll\nBuild succeeded.\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input), CompressionOptions.Default);
        Assert.Contains("Hypa.Runtime.dll", result.Text);
    }

    [Fact]
    public void Compress_DropsBuildProgress()
    {
        var input = "  Determining projects to restore...\n  Restoring packages...\n  Downloaded NuGet.Pkg 1.0\nBuild succeeded.\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input), CompressionOptions.Default);
        Assert.DoesNotContain("Determining projects", result.Text);
        Assert.DoesNotContain("Downloaded", result.Text);
    }

    [Fact]
    public void Compress_PreservesTimeElapsed()
    {
        var input = "Build succeeded.\n\nTime Elapsed 00:00:02.50\n";
        var result = Make().Compress(BuildInvocation("build"), Out(input), CompressionOptions.Default);
        Assert.Contains("Time Elapsed", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var big = string.Join('\n', Enumerable.Range(1, 200).Select(i =>
            i % 10 == 0
                ? $"src/Foo.cs({i},1): error CS0001: fake"
                : $"  Restoring package number {i}..."));
        var result = Make().Compress(BuildInvocation("build"), Out(big, 1), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
