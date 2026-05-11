using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class TscRewriteStrategy : ICommandRewriteStrategy
{
    public bool CanHandle(string verb) => verb == "tsc";

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        var raw = string.Join("", tokens.Select(t => t.Value));
        return RewriteDecision.Generic($"hypa -c \"{Escape(raw)}\"");
    }

    private static string Escape(string s) => s.Replace("\"", "\\\"");
}
