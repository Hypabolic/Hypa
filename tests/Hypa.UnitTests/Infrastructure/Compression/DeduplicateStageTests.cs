using Hypa.Infrastructure.Compression.Stages;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class DeduplicateStageTests
{
    private readonly DeduplicateStage _stage = new();

    [Fact]
    public void Apply_FiveIdenticalLines_CollapsesWithRepeatMarker()
    {
        var input = string.Join('\n', Enumerable.Repeat("same line", 5));
        var result = _stage.Apply(input);
        Assert.Contains("same line", result);
        Assert.Contains("[... repeated 4 times]", result);
        Assert.Equal(2, result.Split('\n').Length);
    }

    [Fact]
    public void Apply_ThreeIdenticalLines_ShowsRepeated2()
    {
        var input = "a\na\na";
        var result = _stage.Apply(input);
        Assert.Contains("[... repeated 2 times]", result);
        Assert.Equal(2, result.Split('\n').Length);
    }

    [Fact]
    public void Apply_TwoIdenticalLines_Unchanged()
    {
        var input = "a\na";
        Assert.Equal(input, _stage.Apply(input));
    }

    [Fact]
    public void Apply_NonConsecutiveDuplicates_Unchanged()
    {
        var input = "a\nb\na";
        Assert.Equal(input, _stage.Apply(input));
    }

    [Fact]
    public void Apply_MixedRuns_OnlyCollapsesRunsOfThreePlus()
    {
        var input = "a\na\na\nb\nb\nc\nc\nc\nc";
        var result = _stage.Apply(input);
        Assert.Contains("[... repeated 2 times]", result);
        Assert.Contains("[... repeated 3 times]", result);
        Assert.DoesNotContain("b\nb\n", result.Replace("\n", "\\n"));
    }
}
