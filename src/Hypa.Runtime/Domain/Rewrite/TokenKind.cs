namespace Hypa.Runtime.Domain.Rewrite;

public enum TokenKind
{
    Arg,
    QuotedArg,
    Operator,
    Pipe,
    Redirect,
    Shellism,
    Whitespace,
}
