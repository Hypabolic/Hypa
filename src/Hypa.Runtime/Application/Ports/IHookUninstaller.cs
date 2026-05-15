using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Ports;

public interface IHookUninstaller
{
    Task<UninstallReport> UninstallAsync(
        UninstallPlan plan,
        string harnessKey,
        bool dryRun,
        CancellationToken ct = default);
}
