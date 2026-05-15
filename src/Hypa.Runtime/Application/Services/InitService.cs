using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Services;

public sealed class InitService(
    IHarnessRegistry registry,
    IHookInstaller installer,
    IProjectRootDetector projectRootDetector,
    IProjectRegistry projectRegistry)
{
    public async Task<IReadOnlyList<InstallReport>> InstallAsync(
        bool global,
        string? agentKey,
        bool dryRun,
        CancellationToken ct = default)
    {
        var adapters = agentKey is not null
            ? ResolveNamedAdapter(agentKey)
            : registry.All;

        var projectRoot = global ? null : projectRootDetector.Detect(Directory.GetCurrentDirectory());

        var reports = new List<InstallReport>(adapters.Count);
        foreach (var adapter in adapters)
        {
            if (agentKey is null && !adapter.IsDetected(global, projectRoot))
            {
                reports.Add(new InstallReport(adapter.Key, [
                    new InstallEntry("Detection", InstallStatus.Skipped, "harness not detected in this scope"),
                ]));
                continue;
            }

            var plan = adapter.GetInstallPlan(global, projectRoot);
            var report = await installer.InstallAsync(plan, adapter.Key, dryRun, ct);
            reports.Add(report);

            if (!dryRun && !global && HasInstalledEntries(report))
            {
                var effectiveRoot = projectRoot ?? Directory.GetCurrentDirectory();
                await projectRegistry.RegisterAsync(effectiveRoot, adapter.Key, ct);
            }
        }
        return reports;
    }

    private IReadOnlyList<IAgentHarnessAdapter> ResolveNamedAdapter(string key)
    {
        var adapter = registry.Find(key);
        return adapter is not null
            ? [adapter]
            : [];
    }

    private static bool HasInstalledEntries(InstallReport report) =>
        report.Entries.Any(e => e.Status is InstallStatus.Installed or InstallStatus.AlreadyPresent);
}
