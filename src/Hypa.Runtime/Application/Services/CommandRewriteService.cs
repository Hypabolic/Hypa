using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;

namespace Hypa.Runtime.Application.Services;

public sealed class CommandRewriteService(
    IConfigLoader configLoader,
    ICommandRewriteRegistry registry)
{
    public async Task<RewriteDecision> RewriteAsync(string command, CancellationToken ct = default)
    {
        var configResult = await configLoader.LoadAsync(ct);
        var config = configResult.IsOk ? configResult.Value : Domain.Config.HypaConfig.Default;

        var context = new RewriteContext(
            IsHypaDisabled: !config.Enabled,
            ExcludeCommands: config.ExcludeCommands,
            GenericWrapperEnabled: config.GenericWrapperEnabled);

        if (context.IsHypaDisabled)
            return RewriteDecision.Passthrough();

        return registry.Rewrite(command, context);
    }
}
