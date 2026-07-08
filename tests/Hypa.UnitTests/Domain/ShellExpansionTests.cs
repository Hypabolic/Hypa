using Hypa.Runtime.Domain.Rewrite;
using Xunit;

namespace Hypa.UnitTests.Domain;

public sealed class ShellExpansionTests
{
    [Fact]
    public void ContainsExpansion_QuotedDollar_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.QuotedArg, "\"$HOME\"", 0),
        };

        var result = ShellExpansion.ContainsExpansion(tokens);

        Assert.True(result);
    }

    [Fact]
    public void ContainsExpansion_UnquotedDollar_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "prefix-$HOME", 0),
        };

        var result = ShellExpansion.ContainsExpansion(tokens);

        Assert.True(result);
    }

    [Fact]
    public void ContainsExpansion_Backtick_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "`date`", 0),
        };

        var result = ShellExpansion.ContainsExpansion(tokens);

        Assert.True(result);
    }

    [Fact]
    public void ContainsExpansion_PlainArg_ReturnsFalse()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "plain", 0),
        };

        var result = ShellExpansion.ContainsExpansion(tokens);

        Assert.False(result);
    }
}
