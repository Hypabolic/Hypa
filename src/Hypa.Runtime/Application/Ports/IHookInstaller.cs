using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Ports;

public interface IHookInstaller
{
    Task<InstallReport> InstallAsync(
        InstallPlan plan,
        string harnessKey,
        bool dryRun,
        CancellationToken ct = default);
}
