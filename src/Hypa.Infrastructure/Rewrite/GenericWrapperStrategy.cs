using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class GenericWrapperStrategy : ICommandRewriteStrategy
{
    // Commands that require a TTY or interactive input — pass through unconditionally.
    private static readonly HashSet<string> Interactive =
    [
        "vim", "vi", "nvim", "nano", "emacs", "pico", "micro",
        "less", "more", "man",
        "top", "htop", "btop", "atop",
        "bash", "sh", "zsh", "fish", "dash",
        "python", "python3", "ipython",
        "node", "irb", "iex", "ghci", "lua",
        "ssh", "telnet", "ftp", "sftp",
        "mysql", "psql", "sqlite3", "mongosh",
        "gdb", "lldb",
    ];

    public bool CanHandle(string verb) => false; // called explicitly by registry, not by verb match

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        var verb = tokens.FirstOrDefault(t => t.Kind == TokenKind.Arg)?.Value;
        if (verb is not null && Interactive.Contains(verb))
            return RewriteDecision.Passthrough();

        var raw = string.Join("", tokens.Select(t => t.Value)).Trim();
        return RewriteDecision.Generic($"hypa -c \"{Escape(raw)}\"");
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"");
}
