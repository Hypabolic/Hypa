using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Compression.Stages;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class TruncationStageTests
{
    private TruncationStage MakeStage(int maxTotal = 500, int head = 80, int tail = 80) =>
        new(new CompressionOptions
        {
            MaxTotalLines = maxTotal,
            MaxHeadLines = head,
            MaxTailLines = tail,
        }, new ImportantLineClassifier());

    [Fact]
    public void Apply_BelowLimit_Unchanged()
    {
        var stage = MakeStage(maxTotal: 500);
        var input = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"line {i}"));
        var result = stage.Apply(input);
        Assert.Equal(input, result);
        Assert.False(stage.WasTruncated);
    }

    [Fact]
    public void Apply_AboveLimit_SetsTruncatedFlag()
    {
        var stage = MakeStage(maxTotal: 100, head: 10, tail: 10);
        var input = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));
        stage.Apply(input);
        Assert.True(stage.WasTruncated);
    }

    [Fact]
    public void Apply_AboveLimit_PreservesHeadAndTail()
    {
        var stage = MakeStage(maxTotal: 100, head: 10, tail: 10);
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
        var result = stage.Apply(string.Join('\n', lines));
        Assert.Contains("line 1", result);
        Assert.Contains("line 10", result);
        Assert.Contains("line 200", result);
        Assert.Contains("line 191", result);
    }

    [Fact]
    public void Apply_ImportantMiddleLines_Preserved()
    {
        var stage = MakeStage(maxTotal: 20, head: 5, tail: 5);
        var lines = Enumerable.Range(1, 30).Select(i =>
            i == 15 ? "error: something failed" : $"line {i}").ToArray();
        var result = stage.Apply(string.Join('\n', lines));
        Assert.Contains("error: something failed", result);
    }

    [Fact]
    public void Apply_CompilerDiagnosticInMiddle_Preserved()
    {
        var stage = MakeStage(maxTotal: 20, head: 5, tail: 5);
        var lines = Enumerable.Range(1, 30).Select(i =>
            i == 15 ? "CS1234: something broken" : $"line {i}").ToArray();
        var result = stage.Apply(string.Join('\n', lines));
        Assert.Contains("CS1234", result);
    }

    [Fact]
    public void Apply_ContainsOmissionMarker()
    {
        var stage = MakeStage(maxTotal: 20, head: 5, tail: 5);
        var input = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var result = stage.Apply(input);
        Assert.Contains("lines omitted", result);
    }
}
