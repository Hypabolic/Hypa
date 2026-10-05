using System.CommandLine;
using Hypa.ControlPlane;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class UpdateCommand(UpdateService updateService, InitService initService)
{
    public Command Build()
    {
        var cmd = new Command("update", "Check for updates and upgrade Hypa.");
        var checkOpt = new Option<bool>("--check") { Description = "Check for updates only; do not apply." };
        var forceOpt = new Option<bool>("--force") { Description = "Bypass the update-check cache." };
        cmd.Add(checkOpt);
        cmd.Add(forceOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var checkOnly = parseResult.GetValue(checkOpt);
            var force = parseResult.GetValue(forceOpt);
            var infoResult = await updateService.GetUpdateInfoAsync(forceRefresh: force, ct);
            if (!infoResult.IsOk)
            {
                Console.Error.WriteLine($"Update check failed: {infoResult.Error.Message}");
                return 1;
            }

            var info = infoResult.Value;

            if (!info.IsUpdateAvailable)
            {
                Console.WriteLine($"Hypa is up to date (v{info.CurrentVersion}).");
                return 0;
            }

            var planResult = await updateService.PlanUpdateAsync(info, ct);
            if (!planResult.IsOk)
            {
                Console.Error.WriteLine($"Could not plan update: {planResult.Error.Message}");
                WriteFallbackGuidance(Console.Error, info);
                return 1;
            }

            var plan = planResult.Value;
            Console.WriteLine($"Update available: v{info.CurrentVersion} → v{info.LatestVersion}");

            if (checkOnly)
            {
                Console.WriteLine(plan.Summary);
                if (plan.Detail is not null)
                    Console.WriteLine(plan.Detail);
                if (plan.Command is not null && !plan.CanAutoUpdate)
                    Console.WriteLine($"Run: {plan.Command}");
                return 0;
            }

            if (!plan.CanAutoUpdate)
            {
                if (plan.Detail is not null)
                    Console.WriteLine(plan.Detail);
                else if (plan.Command is not null)
                    Console.WriteLine($"Run: {plan.Command}");
                return 0;
            }

            Console.WriteLine($"Updating to v{info.LatestVersion}...");
            var applyResult = await updateService.ApplyUpdateAsync(info, ct);
            if (!applyResult.IsOk)
            {
                Console.Error.WriteLine($"Update failed: {applyResult.Error.Message}");
                WriteFallbackGuidance(Console.Error, info, plan);
                return 1;
            }

            Console.WriteLine($"Updated to v{info.LatestVersion}.");
            await RefreshHarnessIntegrationsAsync(ct);
            Console.WriteLine(
                $"A running mux still runs the old version. Click {MuxRestartCommands.SidebarAction} " +
                "in the sidebar, or run `hypa mux restart` from a terminal outside Hypa.");
            return 0;
        });

        return cmd;
    }

    private async Task RefreshHarnessIntegrationsAsync(CancellationToken ct)
    {
        var result = await initService.InstallAsync(
        InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false, ct,
        skipMcpImport: true,
        optInWithMcp: false);

        var refreshed = result.Reports
            .SelectMany(r => r.Entries)
            .Where(e => e.Status == InstallStatus.Installed)
            .ToList();

        var failed = result.Reports
            .SelectMany(r => r.Entries)
            .Where(e => e.Status == InstallStatus.Error)
            .ToList();

        if (refreshed.Count > 0)
            Console.WriteLine($"Refreshed harness integrations ({refreshed.Count} item(s) updated).");
        if (failed.Count > 0)
            Console.Error.WriteLine($"Some harness integrations could not be refreshed — run `hypa init` to retry.");
    }

    internal static void WriteFallbackGuidance(TextWriter writer, Runtime.Domain.Updates.UpdateInfo info, Runtime.Domain.Updates.UpdatePlan? plan = null)
    {
        writer.WriteLine($"Download manually: {info.ReleaseUrl}");

        if (plan?.Strategy is "package-manager" && !string.IsNullOrWhiteSpace(plan.Command))
        {
            writer.WriteLine($"Run: {plan.Command}");
            return;
        }

        if (plan?.Strategy is "manual" && !string.IsNullOrWhiteSpace(plan.Detail))
        {
            writer.WriteLine(plan.Detail);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            writer.WriteLine(
                "Windows is not a Ghostty-only F1 mux RID. Build from source, or use install.sh on Linux or macOS.");
        }
        else
        {
            writer.WriteLine("Or re-run the installer: curl -fsSL https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.sh | sh");
        }
    }
}
