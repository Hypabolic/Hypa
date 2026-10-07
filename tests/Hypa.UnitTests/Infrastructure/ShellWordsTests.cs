using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Domain.Rewrite;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class ShellWordsTests
{
    private static string[] Words(string command) => ShellWords.ToArguments(new ShellLexer().Lex(command));

    [Theory]
    [InlineData("printf x", new[] { "printf", "x" })]
    [InlineData("jest --flag='(a|b)'", new[] { "jest", "--flag=(a|b)" })]
    [InlineData("jest --flag=\"a b\"", new[] { "jest", "--flag=a b" })]
    [InlineData("echo 'a'\"b\"c", new[] { "echo", "abc" })]
    [InlineData("echo 'a' 'b'", new[] { "echo", "a", "b" })]
    [InlineData("echo ''", new[] { "echo", "" })]
    public void ToArguments_JoinsAdjacentWordParts(string command, string[] expected) =>
        Assert.Equal(expected, Words(command));
}
