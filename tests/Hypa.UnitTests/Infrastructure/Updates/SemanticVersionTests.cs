using Hypa.Runtime.Domain.Updates;
using Xunit;

namespace Hypa.UnitTests.Domain.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("v1.0.0", "v0.9.9", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    public void IsNewer_VariousCases(string latest, string current, bool expectedNewer)
    {
        Assert.True(SemanticVersion.TryParse(latest, out var l));
        Assert.True(SemanticVersion.TryParse(current, out var c));
        Assert.Equal(expectedNewer, l > c);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0", false)]   // stable beats prerelease
    [InlineData("1.0.0", "1.0.0-alpha", true)]     // stable is newer than prerelease
    [InlineData("1.0.0-beta", "1.0.0-alpha", true)] // beta > alpha lexicographically
    public void Prerelease_OrderedBelowStable(string a, string b, bool aIsNewer)
    {
        Assert.True(SemanticVersion.TryParse(a, out var va));
        Assert.True(SemanticVersion.TryParse(b, out var vb));
        Assert.Equal(aIsNewer, va > vb);
    }

    [Theory]
    [InlineData("1.0.0+build.1", "1.0.0")]   // build metadata ignored
    [InlineData("v1.0.0+build.1", "1.0.0")]
    public void BuildMetadata_Ignored(string withMeta, string bare)
    {
        Assert.True(SemanticVersion.TryParse(withMeta, out var a));
        Assert.True(SemanticVersion.TryParse(bare, out var b));
        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notaversion")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    public void TryParse_Invalid_ReturnsFalse(string? input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_TwoPartVersion_Succeeds()
    {
        Assert.True(SemanticVersion.TryParse("1.2", out var v));
        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(0, v.Patch);
    }
}
