using Hypa.Infrastructure.Compression.Stages;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class ProgressFilterStageTests
{
    private readonly ProgressFilterStage _stage = new();

    [Fact]
    public void Apply_CarriageReturnProgressBarLine_Removed()
    {
        // Terminal progress-bar overwrite: ends with \r before the newline separator.
        var input = "[##########] 50%\r\nline2";
        var result = _stage.Apply(input);
        Assert.DoesNotContain("[##########]", result);
        Assert.Contains("line2", result);
    }

    [Fact]
    public void Apply_CarriageReturnSpinnerLine_Removed()
    {
        var input = "before\n⠋⠙⠹\r\nafter";
        var result = _stage.Apply(input);
        Assert.DoesNotContain("⠋⠙⠹", result);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void Apply_CrlfProseLines_AllPreserved()
    {
        var input = "First line of output.\r\nSecond line of output.\r\nThird line of output.";
        var result = _stage.Apply(input);
        Assert.Contains("First line of output.", result);
        Assert.Contains("Second line of output.", result);
        Assert.Contains("Third line of output.", result);
    }

    [Fact]
    public void Apply_CrlfMixedWithProgressBar_ProseKeptProgressDropped()
    {
        var input = "Starting build.\r\nCompiling module foo.\r\n[####] 100%\r\nBuild complete.";
        var result = _stage.Apply(input);
        Assert.Contains("Starting build.", result);
        Assert.Contains("Compiling module foo.", result);
        Assert.Contains("Build complete.", result);
        Assert.DoesNotContain("[####] 100%", result);
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
    public void Apply_JsonArrayClosingBracketLine_Unchanged()
    {
        var input = "{\n  \"items\": [\n    \"value\"\n  ]\n}";
        Assert.Equal(input, _stage.Apply(input));
    }

    [Fact]
    public void Apply_NormalLines_Unchanged()
    {
        var input = "Building project\nDone.";
        Assert.Equal(input, _stage.Apply(input));
    }
}
