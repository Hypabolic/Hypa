using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class InitCommand(InitService initService)
{
    public Command Build()
    {
        var cmd = new Command("init", "Install Hypa hooks and skills into detected agent harness config files.");
        var globalOpt = new Option<bool>("--global", "Install into user-level config locations (~/.claude, etc.).");
        var agentOpt = new Option<string?>("--agent", "Install only for the named harness (e.g. claude, codex).");
        var dryRunOpt = new Option<bool>("--dry-run", "Show what would be installed without writing any files.");
        cmd.AddOption(globalOpt);
        cmd.AddOption(agentOpt);
        cmd.AddOption(dryRunOpt);
        cmd.SetHandler(async context =>
        {
            var global = context.ParseResult.GetValueForOption(globalOpt);
            var agentKey = context.ParseResult.GetValueForOption(agentOpt);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);
            var ct = context.GetCancellationToken();

            if (dryRun)
                Console.WriteLine("Dry run — no files will be written.\n");

            var reports = await initService.InstallAsync(global, agentKey, dryRun, ct);

            if (reports.Count == 0)
            {
                Console.WriteLine(agentKey is not null
                    ? $"Agent '{agentKey}' not found. Run `hypa skill list` to see available harnesses."
                    : "No harnesses detected.");
                context.ExitCode = 1;
                return;
            }

            foreach (var report in reports)
            {
                Console.WriteLine($"[{report.HarnessKey}]");
                foreach (var entry in report.Entries)
                {
                    var symbol = entry.Status switch
                    {
                        InstallStatus.Installed => "✓",
                        InstallStatus.AlreadyPresent => "↷",
                        InstallStatus.Skipped => "–",
                        InstallStatus.Error => "!",
                        _ => "?",
                    };
                    var detail = entry.Detail is not null ? $" ({entry.Detail})" : "";
                    Console.WriteLine($"  {symbol} {entry.Description}{detail}");
                }

                if (report.HarnessKey == "codex" &&
                    report.Entries.Any(e => e.Status is InstallStatus.Installed or InstallStatus.AlreadyPresent))
                {
                    Console.WriteLine("  – Review and trust the Hypa hook with `/hooks` in Codex if prompted.");
                }
            }

            var hasErrors = reports.Any(r => r.Entries.Any(e => e.Status == InstallStatus.Error));
            context.ExitCode = hasErrors ? 1 : 0;
        });
        return cmd;
    }
}
