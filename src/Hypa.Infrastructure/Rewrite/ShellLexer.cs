using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class ShellLexer : IShellLexer
{
    private static readonly string[] Operators = ["&&", "||", ";"];
    private static readonly string[] Redirects = ["2>&1", ">>", "<<", ">", "<"];

    public IReadOnlyList<ShellToken> Lex(string command)
    {
        var tokens = new List<ShellToken>();
        var i = 0;

        while (i < command.Length)
        {
            var start = i;
            var ch = command[i];

            // Whitespace
            if (char.IsWhiteSpace(ch))
            {
                while (i < command.Length && char.IsWhiteSpace(command[i]))
                    i++;
                tokens.Add(new ShellToken(TokenKind.Whitespace, command[start..i], start));
                continue;
            }

            // Unknown shell constructs — return single Shellism to signal passthrough
            if (ch == '$' || ch == '`')
                return [new ShellToken(TokenKind.Shellism, command, 0)];

            // Quoted arg
            if (ch == '\'' || ch == '"')
            {
                var quote = ch;
                i++;
                while (i < command.Length && command[i] != quote)
                {
                    if (command[i] == '\\') i++; // skip escape
                    i++;
                }
                if (i < command.Length) i++; // consume closing quote
                tokens.Add(new ShellToken(TokenKind.QuotedArg, command[start..i], start));
                continue;
            }

            // Operators: && || ;
            var matchedOperator = false;
            foreach (var op in Operators)
            {
                if (command.AsSpan(i).StartsWith(op))
                {
                    tokens.Add(new ShellToken(TokenKind.Operator, op, i));
                    i += op.Length;
                    matchedOperator = true;
                    break;
                }
            }
            if (matchedOperator) continue;

            // Redirects: 2>&1 >> << > <
            var matchedRedirect = false;
            foreach (var redir in Redirects)
            {
                if (command.AsSpan(i).StartsWith(redir))
                {
                    tokens.Add(new ShellToken(TokenKind.Redirect, redir, i));
                    i += redir.Length;
                    matchedRedirect = true;
                    break;
                }
            }
            if (matchedRedirect) continue;

            // Pipe
            if (ch == '|')
            {
                tokens.Add(new ShellToken(TokenKind.Pipe, "|", i));
                i++;
                continue;
            }

            // Background & (trailing shellism)
            if (ch == '&')
            {
                tokens.Add(new ShellToken(TokenKind.Shellism, "&", i));
                i++;
                continue;
            }

            // Arg — read until whitespace or special char
            while (i < command.Length && !IsSpecialStart(command, i))
                i++;
            tokens.Add(new ShellToken(TokenKind.Arg, command[start..i], start));
        }

        return tokens;
    }

    private static bool IsSpecialStart(string s, int i)
    {
        var ch = s[i];
        if (char.IsWhiteSpace(ch)) return true;
        if (ch is '\'' or '"' or '$' or '`' or '|' or '&' or ';') return true;
        if (ch == '>' || ch == '<') return true;
        return false;
    }
}
