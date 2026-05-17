using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Services;

public sealed class InitService(
    IHarnessRegistry registry,
    IHookInstaller installer,
    IProjectRootDetector projectRootDetector,
    IProjectRegistry projectRegistry)
{
    public async Task<InitResult> InstallAsync(
        InitScope scope,
        string? agentKey,
        string? projectRootOverride,
        bool dryRun,
        CancellationToken ct = default)
    {
        var detectedProjectRoot = ResolveProjectRoot(projectRootOverride);

        if (scope == InitScope.Project && detectedProjectRoot is null)
        {
            return new InitResult(
                [],
                null,
                ProjectSkipped: true,
                ErrorMessage: $"No project root detected from {Directory.GetCurrentDirectory()}.");
        }

        var adapters = agentKey is not null
            ? ResolveNamedAdapter(agentKey)
            : registry.All;

        if (adapters.Count == 0)
            return new InitResult([], detectedProjectRoot, ProjectSkipped: false);

        var reports = new List<InstallReport>();

        if (scope is InitScope.Global or InitScope.All)
            reports.AddRange(await InstallForScopeAsync(adapters, global: true, projectRoot: null, agentKey, dryRun, ct));

        if (scope == InitScope.Project)
            reports.AddRange(await InstallForScopeAsync(adapters, global: false, detectedProjectRoot!, agentKey, dryRun, ct));

        var projectSkipped = false;
        if (scope == InitScope.All)
        {
            if (detectedProjectRoot is null)
            {
                projectSkipped = true;
            }
            else
            {
                reports.AddRange(await InstallForScopeAsync(adapters, global: false, detectedProjectRoot, agentKey, dryRun, ct));
            }
        }

        return new InitResult(reports, detectedProjectRoot, projectSkipped);
    }

    private async Task<IReadOnlyList<InstallReport>> InstallForScopeAsync(
        IReadOnlyList<IAgentHarnessAdapter> adapters,
        bool global,
        string? projectRoot,
        string? agentKey,
        bool dryRun,
        CancellationToken ct)
    {
        var reports = new List<InstallReport>(adapters.Count);
        foreach (var adapter in adapters)
        {
            if (agentKey is null && !adapter.IsAvailable())
            {
                reports.Add(new InstallReport(adapter.Key, [
                    new InstallEntry(
                        "Harness availability",
                        InstallStatus.Skipped,
                        "harness not installed on this machine"),
                ]));
                continue;
            }

            var plan = adapter.GetInstallPlan(global, projectRoot);
            var report = await installer.InstallAsync(plan, adapter.Key, dryRun, ct);
            reports.Add(report);

            if (!dryRun && !global && IsSuccessfulInstall(report))
            {
                await projectRegistry.RegisterAsync(projectRoot!, adapter.Key, ct);
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

    private static bool IsSuccessfulInstall(InstallReport report) =>
        report.Entries.Any(e => e.Status is InstallStatus.Installed or InstallStatus.AlreadyPresent) &&
        report.Entries.All(e => e.Status != InstallStatus.Error);

    private string? ResolveProjectRoot(string? projectRootOverride)
    {
        if (!string.IsNullOrWhiteSpace(projectRootOverride))
            return Path.GetFullPath(projectRootOverride);

        return projectRootDetector.Detect(Directory.GetCurrentDirectory());
    }
}

public enum InitScope
{
    Global,
    Project,
    All,
}

public sealed record InitResult(
    IReadOnlyList<InstallReport> Reports,
    string? ProjectRoot,
    bool ProjectSkipped,
    string? ErrorMessage = null);
