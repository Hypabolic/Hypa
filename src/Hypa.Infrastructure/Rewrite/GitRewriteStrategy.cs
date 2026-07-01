using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Infrastructure.Rewrite;

public sealed class GitRewriteStrategy : ICommandRewriteStrategy
{
    private static readonly HashSet<string> Supported = ["status", "diff", "log"];
    private static readonly HashSet<string> OptionsRequiringValue = ["-c", "-C", "--git-dir", "--work-tree", "--namespace"];

    public bool CanHandle(string verb) => verb == "git";

    public RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context)
    {
        var args = tokens.Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg).ToList();
        var sub = FindSubcommand(args);

        if (sub is null || !Supported.Contains(sub))
            return RewriteDecision.Passthrough();

        var rest = string.Join(" ", args.Select(t => t.Value));
        return RewriteDecision.Rewritten($"hypa {rest}");
    }

    private static string? FindSubcommand(IReadOnlyList<ShellToken> args)
    {
        if (args.Count <= 1)
            return null;

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            var value = token.Value;

            if (value == "--")
                return null;

            if (value.Length > 0 && value[0] == '-')
            {
                if (OptionsRequiringValue.Contains(value))
                {
                    i++;
                    continue;
                }

                if (value is "--no-pager" or "-p")
                    continue;

                return null;
            }

            return value;
        }

        return null;
    }
}
