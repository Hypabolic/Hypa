using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Ports;

public interface IShellLexer
{
    IReadOnlyList<ShellToken> Lex(string command);
}
