using Hypa.Runtime.Domain.Hooks;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Services;

public sealed class HookService(CommandRewriteService rewriteService)
{
    public async Task<HookDecision> ProcessAsync(AgentHookInput input, CancellationToken ct = default)
    {
        if (IsHypaCommand(input.Command))
            return new HookDecision.Passthrough();

        var decision = await rewriteService.RewriteAsync(input.Command, ct);

        return decision.Outcome switch
        {
            RewriteOutcome.Rewritten or RewriteOutcome.GenericWrapper =>
                new HookDecision.Rewrite(decision.Command!),
            RewriteOutcome.Deny =>
                new HookDecision.Deny($"Command blocked by Hypa policy: {input.Command}"),
            RewriteOutcome.Ask =>
                new HookDecision.Ask($"Confirm running: {decision.Command ?? input.Command}"),
            _ => new HookDecision.Passthrough(),
        };
    }

    private static bool IsHypaCommand(string command)
    {
        var trimmed = command.TrimStart();
        return trimmed == "hypa" ||
               trimmed.StartsWith("hypa ", StringComparison.Ordinal);
    }
}
