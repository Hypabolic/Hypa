namespace Hypa.Runtime.Domain.Rewrite;

public sealed record RewriteDecision(RewriteOutcome Outcome, string? Command)
{
    public static RewriteDecision Rewritten(string command) => new(RewriteOutcome.Rewritten, command);
    public static RewriteDecision Generic(string command) => new(RewriteOutcome.GenericWrapper, command);
    public static RewriteDecision Passthrough() => new(RewriteOutcome.Passthrough, null);
    public static RewriteDecision Ask(string command) => new(RewriteOutcome.Ask, command);
    public static RewriteDecision Deny() => new(RewriteOutcome.Deny, null);
}
