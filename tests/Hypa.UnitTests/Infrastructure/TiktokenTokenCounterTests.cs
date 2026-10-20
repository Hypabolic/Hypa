using Hypa.Infrastructure.Compression;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class TiktokenTokenCounterTests
{
    [Fact]
    public void The_vocabulary_is_built_on_the_first_count_and_not_before()
    {
        var counter = new TiktokenTokenCounter();
        Assert.False(counter.IsLoaded);

        var tokens = counter.EstimateTokens("hello world");

        Assert.True(counter.IsLoaded);
        Assert.Equal(2, tokens);
    }
}
