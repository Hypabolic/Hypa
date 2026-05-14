using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Services;

public sealed class HookService(CommandRewriteService rewriteService, IReadRedirector readRedirector)
{
    public async Task<HookDecision> ProcessAsync(AgentHookInput input, CancellationToken ct = default)
    {
        if (input.ToolName is "Read" or "Grep")
        {
            var tempPath = await readRedirector.RedirectAsync(input.Path ?? "", ct);
            return tempPath is not null
                ? new HookDecision.Redirect(tempPath)
                : new HookDecision.Passthrough();
        }

        if (input.ToolName != "Bash")
            return new HookDecision.Passthrough();

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
