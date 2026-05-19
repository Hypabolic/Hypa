using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;
using Hypa.Runtime.Domain.Projects;

namespace Hypa.Runtime.Application.Services;

public sealed class UninstallService(
    IHarnessRegistry registry,
    IHookUninstaller uninstaller,
    IBinaryRemover binaryRemover,
    IProjectRootDetector projectRootDetector,
    IProjectRegistry projectRegistry)
{
    /// <summary>
    /// Returns null when agentKey is provided but not found in the registry.
    /// Returns an empty list when no adapters are registered.
    /// </summary>
    public async Task<IReadOnlyList<UninstallReport>?> UninstallHarnessesAsync(
        bool global,
        string? agentKey,
        bool dryRun,
        CancellationToken ct = default)
    {
        IReadOnlyList<IAgentHarnessAdapter> adapters;
        if (agentKey is not null)
        {
            var found = registry.Find(agentKey);
            if (found is null) return null;
            adapters = [found];
        }
        else
        {
            adapters = registry.All;
        }

        var currentRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory());

        if (!global && currentRoot is null)
            return adapters
                .Select(adapter => new UninstallReport(adapter.Key, [
                    new UninstallEntry(
                        "Project root detection",
                        UninstallStatus.Error,
                        $"No project root detected from {Directory.GetCurrentDirectory()}"),
                ]))
                .ToList();

        var reports = new List<UninstallReport>(adapters.Count);
        foreach (var adapter in adapters)
        {
            UninstallPlan plan;
            IReadOnlyList<ProjectRegistration> registered = [];

            if (global)
            {
                registered = await projectRegistry.GetByAgentAsync(adapter.Key, ct);
                var allRoots = CollectProjectRoots(currentRoot, registered);
                plan = MergeAllScopedPlans(adapter, allRoots);
            }
            else
            {
                plan = adapter.GetUninstallPlan(global: false, currentRoot);
            }

            var report = await uninstaller.UninstallAsync(plan, adapter.Key, dryRun, ct);
            reports.Add(report);

            if (!dryRun && global && !report.Entries.Any(e => e.Status == UninstallStatus.Error))
            {
                foreach (var reg in registered)
                    await projectRegistry.UnregisterAsync(reg.RootPath, adapter.Key, ct);
            }
        }

        return reports;
    }

    public async Task<(bool Removed, string? Error)> PurgeDataAsync(bool dryRun, CancellationToken ct = default)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hypa");

        try
        {
            if (!Directory.Exists(dataDir))
                return (false, null);

            if (!dryRun)
                Directory.Delete(dataDir, recursive: true);

            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    public Task<BinaryRemoveResult> RemoveBinaryAsync(bool dryRun, CancellationToken ct = default) =>
        binaryRemover.RemoveAsync(dryRun, ct);

    // Collects all unique project roots: the current CWD root plus every registered root.
    private static IReadOnlyList<string> CollectProjectRoots(string? currentRoot, IReadOnlyList<ProjectRegistration> registered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        if (currentRoot is not null && seen.Add(currentRoot))
            roots.Add(currentRoot);

        foreach (var reg in registered)
            if (seen.Add(reg.RootPath))
                roots.Add(reg.RootPath);

        return roots;
    }

    // Merges global and all project-scoped uninstall plans into one, filtering NotSupported.
    // If every operation across all scopes is NotSupported (e.g. Copilot), falls back to the
    // global plan so the user still sees the manual-removal message.
    private static UninstallPlan MergeAllScopedPlans(IAgentHarnessAdapter adapter, IReadOnlyList<string> projectRoots)
    {
        var globalPlan = adapter.GetUninstallPlan(global: true, null);

        var realOps = new List<UninstallOperation>(
            globalPlan.Operations.Where(op => op is not UninstallOperation.NotSupported));

        foreach (var root in projectRoots)
        {
            var projectPlan = adapter.GetUninstallPlan(global: false, root);
            realOps.AddRange(projectPlan.Operations.Where(op => op is not UninstallOperation.NotSupported));
        }

        return realOps.Count > 0 ? new UninstallPlan(realOps) : globalPlan;
    }
}
