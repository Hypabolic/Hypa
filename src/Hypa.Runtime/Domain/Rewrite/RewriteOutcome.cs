namespace Hypa.Runtime.Domain.Rewrite;

public enum RewriteOutcome
{
    Rewritten,
    GenericWrapper,
    Passthrough,
    Ask,
    Deny,
}
