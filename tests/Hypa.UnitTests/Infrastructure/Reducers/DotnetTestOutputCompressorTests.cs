using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class DotnetTestOutputCompressorTests
{
    private static DotnetTestOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation TestInvocation(params string[] args) =>
        CommandInvocation.Buffered("dotnet", ["test", .. args], $"dotnet test {string.Join(' ', args)}");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    [Fact]
    public void CanHandle_DotnetTest_ReturnsTrue() =>
        Assert.True(Make().CanHandle(TestInvocation()));

    [Fact]
    public void CanHandle_DotnetBuild_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("dotnet", ["build"], "dotnet build")));

    [Fact]
    public void Compress_ReducerId_IsDotnetTest()
    {
        var result = Make().Compress(TestInvocation(), Out("Passed! - Failed: 0\n"), CompressionOptions.Default);
        Assert.Equal("dotnet-test", result.ReducerId);
    }

    [Fact]
    public void Compress_PreservesFailingTestName()
    {
        var input = "  Failed MyTests.ShouldReturnTrue [10 ms]\n    Expected: True\n    Actual: False\n\nFailed! - Failed: 1\n";
        var result = Make().Compress(TestInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("ShouldReturnTrue", result.Text);
    }

    [Fact]
    public void Compress_PreservesAssertionMessage()
    {
        var input = "  Failed MyTests.Foo [5 ms]\n    Expected: 42\n    Actual:   0\n\nFailed! - Failed: 1\n";
        var result = Make().Compress(TestInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("Expected", result.Text);
        Assert.Contains("Actual", result.Text);
    }

    [Fact]
    public void Compress_PreservesPassedFailedSummary()
    {
        var input = "Test run for project.dll\n\nPassed! - Failed: 0, Passed: 10, Skipped: 1\n  Total: 11\n";
        var result = Make().Compress(TestInvocation(), Out(input), CompressionOptions.Default);
        Assert.Contains("Passed!", result.Text);
    }

    [Fact]
    public void Compress_PreservesCountLines()
    {
        var input = "Failed! - Failed: 2, Passed: 8\n  Total: 10\n  Failed: 2\n  Passed: 8\n";
        var result = Make().Compress(TestInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("Total", result.Text);
    }

    [Fact]
    public void Compress_PreservesTestRunLine()
    {
        var input = "Test run for /repo/tests/Foo.dll (.NETCoreApp,Version=v10.0)\n\nPassed!\n";
        var result = Make().Compress(TestInvocation(), Out(input), CompressionOptions.Default);
        Assert.Contains("Test run for", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"  Passed Test{i} [1 ms]").ToList();
        lines.Add("Passed! - Failed: 0, Passed: 50");
        var result = Make().Compress(TestInvocation(), Out(string.Join('\n', lines)), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
