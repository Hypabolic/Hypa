using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Compression.Stages;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class GenericOutputCompressorTests
{
    private static GenericOutputCompressor MakeCompressor(CompressionOptions? options = null)
    {
        var counter = new CharDivTokenCounter();
        var classifier = new ImportantLineClassifier();
        var truncation = new TruncationStage(options ?? CompressionOptions.Default, classifier);
        ICompressionStage[] stages =
        [
            new AnsiStripStage(),
            new BlankLineCollapseStage(),
            new ProgressFilterStage(),
            new DeduplicateStage(),
        ];
        return new GenericOutputCompressor(stages, truncation, counter);
    }

    private static CommandInvocation FakeInvocation() =>
        CommandInvocation.Buffered("echo", [], "echo hello");

    private static CommandOutput FakeOutput(string stdout, string stderr = "") =>
        CommandOutput.Captured(stdout, stderr, 0, TimeSpan.Zero);

    [Fact]
    public void Compress_ReducerId_IsGeneric()
    {
        var compressor = MakeCompressor();
        var result = compressor.Compress(FakeInvocation(), FakeOutput("hello\n"), CompressionOptions.Default);
        Assert.Equal("generic", result.ReducerId);
    }

    [Fact]
    public void Compress_CompressedNeverLargerThanInput()
    {
        var compressor = MakeCompressor();
        var bigOutput = string.Join('\n', Enumerable.Range(1, 600).Select(i => $"line {i}"));
        var result = compressor.Compress(FakeInvocation(), FakeOutput(bigOutput), CompressionOptions.Default);
        Assert.True(result.CompressedTokens <= result.OriginalTokens);
    }

    [Fact]
    public void Compress_AnsiAndBlankLinesReduced()
    {
        var compressor = MakeCompressor();
        var input = "\x1B[32mhello\x1B[0m\n\n\n\nworld";
        var result = compressor.Compress(FakeInvocation(), FakeOutput(input), CompressionOptions.Default);
        Assert.DoesNotContain("\x1B[", result.Text);
    }

    [Fact]
    public void Compress_CrlfOutput_ContentSurvives()
    {
        var compressor = MakeCompressor();
        var input = "Cloning repository.\r\nResolving dependencies.\r\nBuild succeeded.";
        var result = compressor.Compress(FakeInvocation(), FakeOutput(input), CompressionOptions.Default);
        Assert.Contains("Cloning repository.", result.Text);
        Assert.Contains("Resolving dependencies.", result.Text);
        Assert.Contains("Build succeeded.", result.Text);
    }

    [Fact]
    public void CanHandle_AlwaysTrue()
    {
        var compressor = MakeCompressor();
        Assert.True(compressor.CanHandle(FakeInvocation()));
    }
}
