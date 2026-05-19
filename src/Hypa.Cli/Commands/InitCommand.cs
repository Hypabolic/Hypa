using System.CommandLine;
using System.Text;
using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class InitCommand(InitService initService, HypaDataOptions dataOptions)
{
    public Command Build()
    {
        var cmd = new Command("init", "Install Hypa hooks and skills into detected agent harness config files.");
        var globalOpt = new Option<bool>("--global", "Install into user-level config locations (~/.claude, etc.). This is the default.");
        var projectOpt = new Option<bool>("--project", "Install into the detected project root only.");
        var allOpt = new Option<bool>("--all", "Install globally and into the detected project root when available.");
        var projectRootOpt = new Option<string?>("--project-root", "Explicit project root for --project or --all.");
        var agentOpt = new Option<string?>("--agent", "Install only for the named harness (e.g. claude, codex).");
        var dryRunOpt = new Option<bool>("--dry-run", "Show what would be installed without writing any files.");
        cmd.AddOption(globalOpt);
        cmd.AddOption(projectOpt);
        cmd.AddOption(allOpt);
        cmd.AddOption(projectRootOpt);
        cmd.AddOption(agentOpt);
        cmd.AddOption(dryRunOpt);
        cmd.SetHandler(async context =>
        {
            var global = context.ParseResult.GetValueForOption(globalOpt);
            var project = context.ParseResult.GetValueForOption(projectOpt);
            var all = context.ParseResult.GetValueForOption(allOpt);
            var projectRoot = context.ParseResult.GetValueForOption(projectRootOpt);
            var agentKey = context.ParseResult.GetValueForOption(agentOpt);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);
            var ct = context.GetCancellationToken();

            if (all && (global || project))
            {
                Console.Error.WriteLine("`--all` cannot be combined with `--global` or `--project`.");
                context.ExitCode = 1;
                return;
            }

            if (global && project)
            {
                Console.Error.WriteLine("Use `hypa init --all` to install both global and project-local integration.");
                context.ExitCode = 1;
                return;
            }

            if (!string.IsNullOrWhiteSpace(projectRoot) && !(project || all))
            {
                Console.Error.WriteLine("`--project-root` requires `--project` or `--all`.");
                context.ExitCode = 1;
                return;
            }

            var scope = all
                ? InitScope.All
                : project
                    ? InitScope.Project
                    : InitScope.Global;

            if (dryRun)
                Console.WriteLine("Dry run — no files will be written.\n");

            var result = await initService.InstallAsync(scope, agentKey, projectRoot, dryRun, ct);

            if (result.ErrorMessage is not null)
            {
                Console.Error.WriteLine(result.ErrorMessage);
                var isStorageError = result.Reports.Any(r =>
                    r.HarnessKey == "storage" && r.Entries.Any(e => e.Status == InstallStatus.Error));
                if (isStorageError)
                {
                    if (agentKey == "codex")
                        PrintCodexStorageHint();
                    else
                        PrintGenericStorageHint();
                }
                else
                {
                    Console.Error.WriteLine("Run `hypa init` for global setup, or pass `--project-root <path>` if this is intentional.");
                }
                context.ExitCode = 1;
                return;
            }

            void PrintGenericStorageHint()
            {
                Console.Error.WriteLine($"""

  Hypa could not provision its storage directory. Ensure this path exists and is writable:

    {dataOptions.DataDirectory}

  You can also set a different storage path with `storage_path` in your Hypa config.
""");
            }

            void PrintCodexStorageHint()
            {
                var codexConfigPath = CodexConfigPaths.ResolveConfigPath();
                var dataDirectoryLiteral = ToTomlBasicStringLiteral(dataOptions.DataDirectory);
                Console.Error.WriteLine($"""

  If running inside a Codex sandbox, add {dataOptions.DataDirectory} to writable roots in {codexConfigPath}:

    sandbox_mode = "workspace-write"

    [sandbox_workspace_write]
    writable_roots = [
      {dataDirectoryLiteral}
    ]
""");
            }

            var reports = result.Reports;

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
                        InstallStatus.Warning => "!",
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
            if (result.ProjectSkipped && scope == InitScope.All)
                Console.WriteLine("Project setup skipped — no project root detected.");
            else if (scope == InitScope.Global && result.ProjectRoot is not null)
                Console.WriteLine($"Detected project: {result.ProjectRoot}. Run `hypa init --project` to add project-local integration.");

            context.ExitCode = hasErrors ? 1 : 0;
        });
        return cmd;
    }

    private static string ToTomlBasicStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when char.IsControl(ch) => $"\\u{(int)ch:X4}",
                _ => ch.ToString(),
            });
        }

        builder.Append('"');
        return builder.ToString();
    }
}
