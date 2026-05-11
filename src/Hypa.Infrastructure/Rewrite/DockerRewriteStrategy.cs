using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class DockerRewriteStrategy : ICommandRewriteStrategy
{
    private static readonly HashSet<string> Supported = ["ps", "logs"];

    public bool CanHandle(string verb) => verb == "docker";

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        var args = tokens.Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg).ToList();
        var sub = args.Skip(1).FirstOrDefault();

        if (sub is null || !Supported.Contains(sub.Value))
            return RewriteDecision.Passthrough();

        var rest = string.Join(" ", args.Select(t => t.Value));
        return RewriteDecision.Rewritten($"hypa {rest}");
    }
}
