namespace Hypa.Runtime.Domain.Rewrite;

public sealed record ShellToken(TokenKind Kind, string Value, int Offset);
