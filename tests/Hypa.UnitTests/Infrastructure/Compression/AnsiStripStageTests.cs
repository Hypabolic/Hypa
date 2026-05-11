using Hypa.Infrastructure.Compression.Stages;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class AnsiStripStageTests
{
    private readonly AnsiStripStage _stage = new();

    [Fact]
    public void Apply_RemovesColourCodes()
    {
        var input = "\x1B[32mhello\x1B[0m world";
        var result = _stage.Apply(input);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Apply_NoAnsi_Unchanged()
    {
        var input = "plain text";
        Assert.Equal(input, _stage.Apply(input));
    }

    [Fact]
    public void Apply_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _stage.Apply(string.Empty));
    }

    [Fact]
    public void Apply_CursorMovement_Stripped()
    {
        var input = "\x1B[2Khello";
        Assert.Equal("hello", _stage.Apply(input));
    }
}
