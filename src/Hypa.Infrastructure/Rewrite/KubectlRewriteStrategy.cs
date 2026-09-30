using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class KubectlRewriteStrategy : ICommandRewriteStrategy
{
    private static readonly HashSet<string> Supported = ["get", "describe"];

    public bool CanHandle(string verb) => verb == "kubectl";

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        var args = ShellVerb.CommandArgs(tokens);
        var sub = args.Skip(1).FirstOrDefault();

        if (sub is null || !Supported.Contains(sub.Value))
            return RewriteDecision.Passthrough();

        var prefix = ShellVerb.AssignmentPrefix(tokens);
        var rest = string.Join(" ", args.Select(t => t.Value));
        return RewriteDecision.Rewritten($"{prefix}hypa {rest}");
    }
}
