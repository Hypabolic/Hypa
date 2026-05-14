namespace Hypa.Runtime.Domain.Hooks;

public abstract record HookDecision
{
    public sealed record Passthrough : HookDecision;
    public sealed record Rewrite(string Command) : HookDecision;
    public sealed record Deny(string Reason) : HookDecision;
    public sealed record Ask(string Reason) : HookDecision;
}
