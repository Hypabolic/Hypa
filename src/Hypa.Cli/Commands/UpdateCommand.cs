using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class UpdateCommand(UpdateService updateService, InitService initService)
{
    public Command Build()
    {
        var cmd = new Command("update", "Check for updates and upgrade Hypa.");
        var checkOpt = new Option<bool>("--check", "Check for updates only; do not apply.");
        var forceOpt = new Option<bool>("--force", "Bypass the update-check cache.");
        cmd.AddOption(checkOpt);
        cmd.AddOption(forceOpt);

        cmd.SetHandler(async context =>
        {
            var checkOnly = context.ParseResult.GetValueForOption(checkOpt);
            var force = context.ParseResult.GetValueForOption(forceOpt);
            var ct = context.GetCancellationToken();

            var infoResult = await updateService.GetUpdateInfoAsync(forceRefresh: force, ct);
            if (!infoResult.IsOk)
            {
                Console.Error.WriteLine($"Update check failed: {infoResult.Error.Message}");
                context.ExitCode = 1;
                return;
            }

            var info = infoResult.Value;

            if (!info.IsUpdateAvailable)
            {
                Console.WriteLine($"Hypa is up to date (v{info.CurrentVersion}).");
                context.ExitCode = 0;
                return;
            }

            var planResult = await updateService.PlanUpdateAsync(info, ct);
            if (!planResult.IsOk)
            {
                Console.Error.WriteLine($"Could not plan update: {planResult.Error.Message}");
                WriteFallbackGuidance(Console.Error, info);
                context.ExitCode = 1;
                return;
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
                context.ExitCode = 0;
                return;
            }

            if (!plan.CanAutoUpdate)
            {
                if (plan.Detail is not null)
                    Console.WriteLine(plan.Detail);
                else if (plan.Command is not null)
                    Console.WriteLine($"Run: {plan.Command}");
                context.ExitCode = 0;
                return;
            }

            Console.WriteLine($"Updating to v{info.LatestVersion}...");
            var applyResult = await updateService.ApplyUpdateAsync(info, ct);
            if (!applyResult.IsOk)
            {
                Console.Error.WriteLine($"Update failed: {applyResult.Error.Message}");
                WriteFallbackGuidance(Console.Error, info, plan);
                context.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Updated to v{info.LatestVersion}.");
            await RefreshHarnessIntegrationsAsync(ct);
            Console.WriteLine("Please restart hypa.");
            context.ExitCode = 0;
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
            writer.WriteLine("Or re-run the installer: iwr https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.ps1 -useb | iex");
        else
            writer.WriteLine("Or re-run the installer: curl -fsSL https://raw.githubusercontent.com/Hypabolic/Hypa/main/install.sh | sh");
    }
}
