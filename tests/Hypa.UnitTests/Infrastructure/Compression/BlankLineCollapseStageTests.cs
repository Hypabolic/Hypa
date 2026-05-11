using Hypa.Infrastructure.Compression.Stages;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class BlankLineCollapseStageTests
{
    private readonly BlankLineCollapseStage _stage = new();

    [Fact]
    public void Apply_ThreeBlankLines_CollapsedToOne()
    {
        var input = "a\n\n\n\nb";
        var result = _stage.Apply(input);
        Assert.Equal("a\n\nb", result);
    }

    [Fact]
    public void Apply_TwoBlankLines_CollapsedToOne()
    {
        var input = "a\n\n\nb";
        var result = _stage.Apply(input);
        Assert.Equal("a\n\nb", result);
    }

    [Fact]
    public void Apply_OneBlankLine_Unchanged()
    {
        var input = "a\n\nb";
        Assert.Equal(input, _stage.Apply(input));
    }

    [Fact]
    public void Apply_NoBlankLines_Unchanged()
    {
        var input = "a\nb\nc";
        Assert.Equal(input, _stage.Apply(input));
    }
}
