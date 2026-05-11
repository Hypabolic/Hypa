using Hypa.Infrastructure.Compression.Stages;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class ProgressFilterStageTests
{
    private readonly ProgressFilterStage _stage = new();

    [Fact]
    public void Apply_CarriageReturnLine_Removed()
    {
        // Terminal progress line: ends with \r before the newline separator
        var input = "progress\r\nline2";
        var result = _stage.Apply(input);
        Assert.DoesNotContain("progress", result);
        Assert.Contains("line2", result);
    }

    [Fact]
    public void Apply_ProgressBarLine_Removed()
    {
        var input = "before\n[====>     ] 47%\nafter";
        var result = _stage.Apply(input);
        Assert.DoesNotContain("[====>", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void Apply_NormalLines_Unchanged()
    {
        var input = "Building project\nDone.";
        Assert.Equal(input, _stage.Apply(input));
    }
}
