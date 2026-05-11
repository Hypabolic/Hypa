using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Ports;

public interface ICommandRewriteStrategy
{
    bool CanHandle(string verb);
    RewriteDecision Rewrite(IReadOnlyList<ShellToken> tokens, RewriteContext context);
}
