using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Reducers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Reducers;

public sealed class TscOutputCompressorTests
{
    private static TscOutputCompressor Make() => new(new CharDivTokenCounter());

    private static CommandInvocation TscInvocation() =>
        CommandInvocation.Buffered("tsc", ["--noEmit"], "tsc --noEmit");

    private static CommandInvocation NpxTscInvocation() =>
        CommandInvocation.Buffered("npx", ["tsc", "--noEmit"], "npx tsc --noEmit");

    private static CommandOutput Out(string stdout, int exitCode = 0) =>
        CommandOutput.Captured(stdout, string.Empty, exitCode, TimeSpan.Zero);

    [Fact]
    public void CanHandle_Tsc_ReturnsTrue() =>
        Assert.True(Make().CanHandle(TscInvocation()));

    [Fact]
    public void CanHandle_NpxTsc_ReturnsTrue() =>
        Assert.True(Make().CanHandle(NpxTscInvocation()));

    [Fact]
    public void CanHandle_NpxOther_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("npx", ["jest"], "npx jest")));

    [Fact]
    public void CanHandle_Git_ReturnsFalse() =>
        Assert.False(Make().CanHandle(CommandInvocation.Buffered("git", ["status"], "git status")));

    [Fact]
    public void Compress_ReducerId_IsTsc()
    {
        var result = Make().Compress(TscInvocation(), Out("Found 0 errors.\n"), CompressionOptions.Default);
        Assert.Equal("tsc", result.ReducerId);
    }

    [Fact]
    public void Compress_PreservesTsErrorLine()
    {
        var input = "src/index.ts(10,5): error TS2345: Argument of type 'string' is not assignable\nFound 1 error.\n";
        var result = Make().Compress(TscInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("TS2345", result.Text);
        Assert.Contains("src/index.ts", result.Text);
    }

    [Fact]
    public void Compress_PreservesSummaryLine()
    {
        var input = "src/foo.ts(1,1): error TS2304: Cannot find name 'x'\nFound 1 error.\n";
        var result = Make().Compress(TscInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("Found 1 error", result.Text);
    }

    [Fact]
    public void Compress_GroupsByFile()
    {
        var input = "src/a.ts(1,1): error TS2304: bad\nsrc/b.ts(2,2): error TS2304: also bad\nFound 2 errors.\n";
        var result = Make().Compress(TscInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Contains("=== src/a.ts ===", result.Text);
        Assert.Contains("=== src/b.ts ===", result.Text);
    }

    [Fact]
    public void Compress_SameFileSingleHeader()
    {
        var input = "src/a.ts(1,1): error TS2304: bad\nsrc/a.ts(2,1): error TS2305: also bad\n";
        var result = Make().Compress(TscInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.Equal(1, result.Text.Split("=== src/a.ts ===").Length - 1);
    }

    [Fact]
    public void Compress_DropsProgressLines()
    {
        var input = "Processing files...\nsrc/a.ts(5,3): error TS2304: oops\nDone.\n";
        var result = Make().Compress(TscInvocation(), Out(input, 1), CompressionOptions.Default);
        Assert.DoesNotContain("Processing files", result.Text);
        Assert.DoesNotContain("Done.", result.Text);
    }

    [Fact]
    public void Compress_CompressedNotLargerThanOriginal()
    {
        var lines = Enumerable.Range(1, 100).Select(i =>
            i % 5 == 0
                ? $"src/file{i}.ts({i},1): error TS2304: fake error"
                : $"Processing file {i}...").ToList();
        lines.Add("Found 20 errors.");
        var result = Make().Compress(TscInvocation(), Out(string.Join('\n', lines), 1), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }
}
