using System.CommandLine;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class UninstallCommand(
    UninstallService uninstallService,
    IOfficialIntegrationService? integrations = null)
{
    public Command Build()
    {
        var cmd = new Command("uninstall", "Remove all Hypa integrations, user data, and binary.");
        var agentOpt = new Option<string?>("--agent") { Description = "Remove only for the named harness (e.g. claude, codex). Skips data and binary purge." };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show what would be removed without touching any files." };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the interactive confirmation prompt." };
        cmd.Add(agentOpt);
        cmd.Add(dryRunOpt);
        cmd.Add(yesOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var agentKey = parseResult.GetValue(agentOpt);
            var dryRun = parseResult.GetValue(dryRunOpt);
            var yes = parseResult.GetValue(yesOpt);
            if (dryRun)
            {
                Console.WriteLine("Dry run — no files will be removed.\n");
            }
            else if (!yes)
            {
                Console.Write("Remove all Hypa integrations and data? [y/N] ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
                Console.WriteLine();
            }

            // 1. Uninstall harness configs (always global)
            var reports = await uninstallService.UninstallHarnessesAsync(global: true, agentKey, dryRun, ct);

            if (reports is null)
            {
                Console.WriteLine($"Agent '{agentKey}' not found. Run `hypa skill list` to see available harnesses.");
                return 1;
            }

            foreach (var report in reports)
            {
                Console.WriteLine($"[{report.HarnessKey}]");
                foreach (var entry in report.Entries)
                {
                    var symbol = entry.Status switch
                    {
                        UninstallStatus.Removed => "✓",
                        UninstallStatus.NotPresent => "↷",
                        UninstallStatus.Skipped => "–",
                        UninstallStatus.Error => "!",
                        _ => "?",
                    };
                    var detail = entry.Detail is not null ? $"  ({entry.Detail})" : "";
                    Console.WriteLine($"  {symbol} {entry.Description}{detail}");
                }
            }

            var hasErrors = reports.Any(r => r.Entries.Any(e => e.Status == UninstallStatus.Error));

            if (integrations is not null && !dryRun)
            {
                if (RuntimeSkillStep.Remove(integrations, agentKey) != 0)
                    hasErrors = true;
            }

            // 2. Data and binary purge for a full uninstall (not agent-scoped).
            //    Skipped when --agent is used to avoid removing shared data while other harnesses may still be configured.
            if (agentKey is null)
            {
                Console.WriteLine();

                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".hypa");

                var (dataRemoved, dataError) = await uninstallService.PurgeDataAsync(dryRun, ct);
                if (dataError is not null)
                {
                    Console.WriteLine($"Data: ! {dataError}");
                    hasErrors = true;
                }
                else if (dataRemoved)
                {
                    Console.WriteLine(dryRun
                        ? $"Data: ✓ {dataDir} would be removed"
                        : $"Data: ✓ {dataDir} removed");
                }
                else
                {
                    Console.WriteLine($"Data: ↷ {dataDir} not found");
                }

                var binaryResult = await uninstallService.RemoveBinaryAsync(dryRun, ct);
                if (binaryResult.Removed)
                {
                    var detail = binaryResult.Detail is not null ? $"  ({binaryResult.Detail})" : "";
                    Console.WriteLine(dryRun
                        ? $"Binary: ✓ would be removed{detail}"
                        : $"Binary: ✓ removed{detail}");
                }
                else
                {
                    var detail = binaryResult.Detail ?? "not found at expected locations";
                    Console.WriteLine($"Binary: ! {detail}");
                    hasErrors = true;
                }
            }

            return hasErrors ? 1 : 0;
        });

        return cmd;
    }
}
