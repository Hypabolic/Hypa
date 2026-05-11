using Hypa.Infrastructure.Compression;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class CharDivTokenCounterTests
{
    private readonly CharDivTokenCounter _counter = new();

    [Fact]
    public void EstimateTokens_ShortString_ReturnsAtLeastOne()
    {
        Assert.Equal(1, _counter.EstimateTokens("hello"));
    }

    [Fact]
    public void EstimateTokens_EmptyString_ReturnsOne()
    {
        Assert.Equal(1, _counter.EstimateTokens(""));
    }

    [Fact]
    public void EstimateTokens_FourHundredChars_Returns100()
    {
        Assert.Equal(100, _counter.EstimateTokens(new string('a', 400)));
    }

    [Fact]
    public void EstimateTokens_EightChars_Returns2()
    {
        Assert.Equal(2, _counter.EstimateTokens("12345678"));
    }
}
