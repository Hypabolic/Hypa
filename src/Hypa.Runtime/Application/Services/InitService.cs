using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Runtime.Application.Services;

public sealed class InitService(
    IHarnessRegistry registry,
    IHookInstaller installer,
    IProjectRootDetector projectRootDetector,
    IProjectRegistry projectRegistry,
    IStorageProvisioner storageProvisioner,
    IMcpServerImportService? importService = null)
{
    public async Task<InitResult> InstallAsync(
        InitScope scope,
        string? agentKey,
        string? projectRootOverride,
        bool dryRun,
        CancellationToken ct = default,
        bool skipMcpImport = false)
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
        var importReports = new List<McpImportReport>();
        var importErrors = new List<string>();

        if (!dryRun)
        {
            var provision = await storageProvisioner.ProvisionAsync(ct);
            if (!provision.IsOk)
            {
                var err = provision.Error;
                return new InitResult(
                    [new InstallReport("storage", [new InstallEntry("Database provisioning failed", InstallStatus.Error, err.Message)])],
                    detectedProjectRoot,
                    ProjectSkipped: false,
                    ErrorMessage: err.Message);
            }
            reports.Add(new InstallReport("storage", [new InstallEntry("Database provisioned", InstallStatus.Installed)]));
        }

        if (scope is InitScope.Global or InitScope.All)
        {
            reports.AddRange(await InstallForScopeAsync(adapters, global: true, projectRoot: null, agentKey, dryRun, ct));
            if (!skipMcpImport && importService is not null)
            {
                var importResult = await RunImportAsync(importService, agentKey, McpImportScope.Global, null, dryRun, ct);
                if (importResult.IsOk) importReports.Add(importResult.Value);
                else importErrors.Add($"Global MCP import: {importResult.Error.Message}");
            }
        }

        if (scope == InitScope.Project)
        {
            reports.AddRange(await InstallForScopeAsync(adapters, global: false, detectedProjectRoot!, agentKey, dryRun, ct));
            if (!skipMcpImport && importService is not null)
            {
                var importResult = await RunImportAsync(importService, agentKey, McpImportScope.Project, detectedProjectRoot, dryRun, ct);
                if (importResult.IsOk) importReports.Add(importResult.Value);
                else importErrors.Add($"Project MCP import: {importResult.Error.Message}");
            }
        }

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
                if (!skipMcpImport && importService is not null)
                {
                    var importResult = await RunImportAsync(importService, agentKey, McpImportScope.Project, detectedProjectRoot, dryRun, ct);
                    if (importResult.IsOk) importReports.Add(importResult.Value);
                    else importErrors.Add($"Project MCP import: {importResult.Error.Message}");
                }
            }
        }

        // Add import errors as warnings to the harness reports without aborting installation.
        if (importErrors.Count > 0)
        {
            reports.Add(new InstallReport(
                "mcp-import",
                importErrors.Select((err, i) => new InstallEntry(
                    i == 0 ? "MCP Server Import" : "Additional Import Issue",
                    InstallStatus.Warning,
                    err)).ToList()));
        }

        McpImportReport? mergedImport = importReports.Count > 0
            ? new McpImportReport(
                importReports.SelectMany(r => r.Sources).ToList(),
                importReports.Sum(r => r.ImportedCount),
                importReports.Sum(r => r.AlreadyPresentCount),
                importReports.Sum(r => r.SkippedCount),
                importReports.Sum(r => r.ConflictCount))
            : null;

        return new InitResult(reports, detectedProjectRoot, projectSkipped, ImportReport: mergedImport);
    }

    private static async Task<Result<McpImportReport, Error>> RunImportAsync(
        IMcpServerImportService importService,
        string? agentKey,
        McpImportScope scope,
        string? projectRoot,
        bool dryRun,
        CancellationToken ct)
    {
        return await importService.ImportAsync(
            new McpImportRequest(agentKey, scope, projectRoot, Replace: false, DryRun: dryRun), ct);
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
                var registration = await projectRegistry.RegisterAsync(projectRoot!, adapter.Key, ct);
                if (!registration.IsOk)
                {
                    report = report with
                    {
                        Entries = [
                            .. report.Entries,
                            new InstallEntry(
                                "Project registration",
                                InstallStatus.Warning,
                                registration.Error.Message),
                        ],
                    };
                    reports[^1] = report;
                }
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
    McpImportReport? ImportReport = null,
    string? ErrorMessage = null);
