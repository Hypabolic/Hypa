using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class GitRewriteStrategy : ICommandRewriteStrategy
{
    private static readonly HashSet<string> Supported = ["status", "diff", "log"];
    private static readonly HashSet<string> OptionsRequiringValue = ["-c", "-C", "--git-dir", "--work-tree", "--namespace"];
    private static readonly HashSet<string> PagerOptions = ["--no-pager", "-P", "--paginate", "-p"];

    public bool CanHandle(string verb) => verb == "git";

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        // CommandArgs skips leading VAR=value so env prefixes do not masquerade as the verb.
        var args = ShellVerb.CommandArgs(tokens);
        var sub = FindSubcommand(args);

        if (sub is null || !Supported.Contains(sub))
            return RewriteDecision.Passthrough();

        var prefix = ShellVerb.AssignmentPrefix(tokens);
        var rest = string.Join(" ", args.Select(t => t.Value));
        return RewriteDecision.Rewritten($"{prefix}hypa {rest}");
    }

    private static string? FindSubcommand(IReadOnlyList<ShellToken> args)
    {
        if (args.Count <= 1)
            return null;

        for (var i = 1; i < args.Count; i++)
        {
            var value = args[i].Value;

            if (value == "--")
                return null;

            if (value.Length > 0 && value[0] == '-')
            {
                if (OptionsRequiringValue.Contains(value))
                {
                    i++;
                    continue;
                }

                if (PagerOptions.Contains(value))
                    continue;

                return null;
            }

            return value;
        }

        return null;
    }
}
