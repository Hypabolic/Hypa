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

            // Unquoted newline / carriage return. To sh a newline is a command
            // separator (like ';'), except after '\' or inside a heredoc body.
            // Treating it as plain whitespace made the rewriter put two commands
            // into one 'hypa -c "..."' string and made 'hypa -c' run them as a
            // single argv, e.g. "rm -rf a<NL>ls b" became "rm -rf a ls b".
            // Signal passthrough so a real shell handles the whole command.
            // Trailing newlines (nothing but whitespace after them) are harmless.
            if (ch is '\n' or '\r' && !IsTrailingWhitespace(command, i))
                return [new ShellToken(TokenKind.Shellism, command, 0)];

            // Whitespace
            if (char.IsWhiteSpace(ch))
            {
                while (i < command.Length && char.IsWhiteSpace(command[i]))
                    i++;
                tokens.Add(new ShellToken(TokenKind.Whitespace, command[start..i], start));
                continue;
            }

            // Unknown shell constructs — return single Shellism to signal passthrough
            if (ch == '$' || ch == '`' || ch == '(' || ch == ')')
                return [new ShellToken(TokenKind.Shellism, command, 0)];

            // '#' at the start of a word begins a comment: sh ignores the rest of
            // the line, including any ';', '&&' or '|' in it. Splitting on those
            // ran commented-out commands ("ls # x; rm -rf y" ran "rm -rf y"), and
            // direct execution passed '#' and the comment words as arguments.
            if (ch == '#')
                return [new ShellToken(TokenKind.Shellism, command, 0)];

            // Backslash escape outside quotes. "\;", "\&", "\|", "\<NL>" etc. are
            // not operators to sh, but splitting on them did change which commands
            // ran. Only escapes of shell-special characters (or a trailing '\') are
            // treated as unknown syntax; a backslash before a letter or digit stays
            // part of the word so Windows paths like C:\tools\x.exe keep working.
            if (ch == '\\' && IsShellSpecialEscape(command, i))
                return [new ShellToken(TokenKind.Shellism, command, 0)];

            // Heredoc (<<, <<-) and here-string (<<<): the body that follows is
            // data, not commands, and must never be split or rewritten.
            if (command.AsSpan(i).StartsWith("<<"))
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
            {
                if (command[i] == '\\' && IsShellSpecialEscape(command, i))
                    return [new ShellToken(TokenKind.Shellism, command, 0)];
                i++;
            }
            tokens.Add(new ShellToken(TokenKind.Arg, command[start..i], start));
        }

        return tokens;
    }

    /// <summary>
    /// True when the unquoted backslash at <paramref name="i"/> escapes something
    /// other than a letter, digit, '.', '_' or '-' (or ends the string), i.e. when sh would give
    /// it a meaning this lexer does not model.
    /// </summary>
    private static bool IsShellSpecialEscape(string s, int i)
    {
        if (i + 1 >= s.Length) return true;
        var next = s[i + 1];
        return !(char.IsLetterOrDigit(next) || next is '.' or '_' or '-');
    }

    private static bool IsTrailingWhitespace(string s, int i)
    {
        for (var j = i; j < s.Length; j++)
        {
            if (!char.IsWhiteSpace(s[j])) return false;
        }
        return true;
    }

    private static bool IsSpecialStart(string s, int i)
    {
        var ch = s[i];
        if (char.IsWhiteSpace(ch)) return true;
        if (ch is '\'' or '"' or '$' or '`' or '|' or '&' or ';' or '(' or ')') return true;
        if (ch == '>' || ch == '<') return true;
        return false;
    }
}
