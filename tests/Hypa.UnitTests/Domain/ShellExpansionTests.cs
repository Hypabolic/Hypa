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
        // ~* / ~? start with ~ but are not POSIX tilde words (login names
        // cannot contain glob metacharacters). They still route via
        // ContainsGlobOrBraceExpansion for pathname expansion.
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, value, 0),
        };

        Assert.False(ShellExpansion.ContainsTildeExpansion(tokens));
    }

    [Theory]
    [InlineData("*.json")]
    [InlineData("path/*.ts")]
    [InlineData("file?")]
    [InlineData("file[ab]")]
    [InlineData("~*")]
    [InlineData("~?")]
    [InlineData("~[a]")]
    public void ContainsGlobOrBraceExpansion_UnquotedGlob_ReturnsTrue(string value)
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, value, 0),
        };

        Assert.True(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }

    [Theory]
    [InlineData("{a,b}")]
    [InlineData("file{a,b}.txt")]
    [InlineData("{1..3}")]
    [InlineData("pre{x..y}post")]
    [InlineData("{a,b,c}")]
    [InlineData("{a,")] // quote-split prefix of {a,"b"}
    public void ContainsGlobOrBraceExpansion_UnquotedBrace_ReturnsTrue(string value)
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, value, 0),
        };

        Assert.True(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }

    [Fact]
    public void ContainsGlobOrBraceExpansion_QuoteSplitBraceWord_ReturnsTrue()
    {
        // Lexer yields Arg("{a,") + QuotedArg("\"b\"") + Arg("}") for {a,"b"}.
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, "{a,", 0),
            new ShellToken(TokenKind.QuotedArg, "\"b\"", 3),
            new ShellToken(TokenKind.Arg, "}", 6),
        };

        Assert.True(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("{x}")]
    [InlineData("{}")]
    [InlineData("a{b}c")]
    [InlineData("no-braces")]
    public void ContainsGlobOrBraceExpansion_PlainOrNonExpandingBrace_ReturnsFalse(string value)
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Arg, value, 0),
        };

        Assert.False(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }

    [Theory]
    [InlineData("\"*.ts\"")]
    [InlineData("'*.json'")]
    [InlineData("\"{a,b}\"")]
    [InlineData("'{1..3}'")]
    [InlineData("\"file?\"")]
    public void ContainsGlobOrBraceExpansion_QuotedGlobOrBrace_ReturnsFalse(string value)
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.QuotedArg, value, 0),
        };

        Assert.False(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }

    [Fact]
    public void ContainsGlobOrBraceExpansion_OnlyScansArgTokens()
    {
        var tokens = new[]
        {
            new ShellToken(TokenKind.Operator, "&&", 0),
            new ShellToken(TokenKind.Pipe, "|", 3),
            new ShellToken(TokenKind.QuotedArg, "\"*.ts\"", 5),
            new ShellToken(TokenKind.Arg, "plain", 12),
        };

        Assert.False(ShellExpansion.ContainsGlobOrBraceExpansion(tokens));
    }
}
