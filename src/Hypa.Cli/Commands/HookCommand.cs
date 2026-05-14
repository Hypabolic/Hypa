using System.CommandLine;
using System.Text.Json;
using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class HookCommand(
    HookIoAdapter io,
    IHarnessRegistry registry,
    HookService hookService)
{
    public Command Build()
    {
        var cmd = new Command("hook", "Process a PreToolUse hook payload from stdin and write agent-specific JSON to stdout.");
        var agentOpt = new Option<string?>("--agent", "Agent harness key (e.g. claude, codex). Auto-detects from payload if omitted.");
        cmd.AddOption(agentOpt);
        cmd.SetHandler(async context =>
        {
            var agentKey = context.ParseResult.GetValueForOption(agentOpt);
            var ct = context.GetCancellationToken();

            var json = await io.ReadStdinAsync(ct);
            if (json is null)
            {
                context.ExitCode = 0;
                return;
            }

            var (adapter, input) = ResolveAdapterAndInput(json.Value, agentKey);
            if (adapter is null || input is null)
            {
                context.ExitCode = 0;
                return;
            }

            try
            {
                var decision = await hookService.ProcessAsync(input, ct);
                var output = adapter.Format(decision, input);
                HookIoAdapter.WriteOutput(output);
                context.ExitCode = output.ExitCode;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"hypa hook: error processing hook: {ex.Message}");
                context.ExitCode = 0;
            }
        });
        return cmd;
    }

    private (IAgentHarnessAdapter? adapter, Runtime.Domain.Hooks.AgentHookInput? input) ResolveAdapterAndInput(
        JsonElement json,
        string? agentKey)
    {
        if (agentKey is not null)
        {
            var adapter = registry.Find(agentKey);
            if (adapter is null)
            {
                Console.Error.WriteLine($"hypa hook: unknown agent '{agentKey}'");
                return (null, null);
            }
            return (adapter, adapter.Parse(json));
        }

        foreach (var candidate in registry.All)
        {
            var parsed = candidate.Parse(json);
            if (parsed is not null)
                return (candidate, parsed);
        }

        return (null, null);
    }
}
