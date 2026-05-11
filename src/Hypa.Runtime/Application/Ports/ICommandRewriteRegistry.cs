using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Ports;

public interface ICommandRewriteRegistry
{
    RewriteDecision Rewrite(string command, RewriteContext context);
}
