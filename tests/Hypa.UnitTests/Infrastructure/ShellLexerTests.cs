using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Domain.Rewrite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class ShellLexerTests
{
    private readonly ShellLexer _lexer = new();

    [Fact]
    public void SimpleArg_ReturnsSingleArgToken()
    {
        var tokens = _lexer.Lex("git");
        Assert.Single(tokens, t => t.Kind == TokenKind.Arg);
    }

    [Fact]
    public void TwoArgs_ReturnsTwoArgTokens()
    {
        var tokens = _lexer.Lex("git status");
        var args = tokens.Where(t => t.Kind == TokenKind.Arg).ToList();
        Assert.Equal(2, args.Count);
        Assert.Equal("git", args[0].Value);
        Assert.Equal("status", args[1].Value);
    }

    [Fact]
    public void DoubleQuotedArg_ReturnsQuotedArgToken()
    {
        var tokens = _lexer.Lex("echo \"hello world\"");
        Assert.Contains(tokens, t => t.Kind == TokenKind.QuotedArg);
    }

    [Fact]
    public void SingleQuotedArg_ReturnsQuotedArgToken()
    {
        var tokens = _lexer.Lex("echo 'hello'");
        Assert.Contains(tokens, t => t.Kind == TokenKind.QuotedArg);
    }

    [Fact]
    public void AndAnd_ReturnsOperatorToken()
    {
        var tokens = _lexer.Lex("git status && dotnet build");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == "&&");
    }

    [Fact]
    public void OrOr_ReturnsOperatorToken()
    {
        var tokens = _lexer.Lex("git status || echo fail");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == "||");
    }

    [Fact]
    public void Semicolon_ReturnsOperatorToken()
    {
        var tokens = _lexer.Lex("git status ; dotnet build");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator && t.Value == ";");
    }

    [Fact]
    public void Pipe_ReturnsPipeToken()
    {
        var tokens = _lexer.Lex("cat file | grep foo");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Pipe);
    }

    [Fact]
    public void Redirect_ReturnsRedirectToken()
    {
        var tokens = _lexer.Lex("git status 2>&1");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Redirect);
    }

    [Fact]
    public void SubshellExpansion_ReturnsSingleShellism()
    {
        var tokens = _lexer.Lex("echo $(pwd)");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Shellism, tokens[0].Kind);
    }

    [Fact]
    public void Backtick_ReturnsSingleShellism()
    {
        var tokens = _lexer.Lex("echo `pwd`");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Shellism, tokens[0].Kind);
    }

    [Fact]
    public void TokensPreserveOffsets()
    {
        var tokens = _lexer.Lex("git status");
        var gitToken = tokens.First(t => t.Kind == TokenKind.Arg && t.Value == "git");
        var statusToken = tokens.First(t => t.Kind == TokenKind.Arg && t.Value == "status");
        Assert.Equal(0, gitToken.Offset);
        Assert.Equal(4, statusToken.Offset);
    }
}
