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

    [Fact]
    public void ContainsTildeExpansion_UnquotedLeadingTilde_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~/Desktop", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_BareTilde_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_TildeSlashOnly_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~/", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_TildeUser_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~user/bin", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_BareTildeUser_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~user", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_TildeUserWithDotsAndDashes_ReturnsTrue()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "~j.doe-1/bin", 0),
        };

        Assert.True(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_QuotedTilde_ReturnsFalse()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.QuotedArg, "\"~/Desktop\"", 0),
        };

        Assert.False(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_TildeNotAtStart_ReturnsFalse()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "a~b", 0),
        };

        Assert.False(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Fact]
    public void ContainsTildeExpansion_PlainArg_ReturnsFalse()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "plain", 0),
        };

        Assert.False(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Theory]
    [InlineData("~*")]
    [InlineData("~?")]
    [InlineData("~[a]")]
    [InlineData("~user*")]
    [InlineData("~user?x")]
    public void ContainsTildeExpansion_GlobLikeTildeForms_ReturnFalse(string value)
    {
        // Copilot review on #65: ~* / ~? start with ~ but are not POSIX tilde
        // words. Routing them through the shell would enable globbing without
        // performing tilde expansion.
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, value, 0),
        };

        Assert.False(ShellExpansion.ContainsTildeExpansion(tokens));
    }
}
