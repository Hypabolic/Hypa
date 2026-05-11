using Hypa.Infrastructure.Compression;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Compression;

public sealed class TiktokenTokenCounterTests
{
    private readonly TiktokenTokenCounter _counter = new();

    [Fact]
    public void EstimateTokens_EmptyString_ReturnsAtLeastOne()
    {
        Assert.Equal(1, _counter.EstimateTokens(""));
    }

    [Fact]
    public void EstimateTokens_UsesTokenizerInsteadOfCharDivFallback()
    {
        Assert.NotEqual(100, _counter.EstimateTokens(new string('a', 400)));
    }

    [Fact]
    public void EstimateTokens_CodeLikeText_ReturnsStablePositiveCount()
    {
        var tokens = _counter.EstimateTokens("public static void Main() { Console.WriteLine(\"hello\"); }");

        Assert.InRange(tokens, 10, 30);
    }
}
