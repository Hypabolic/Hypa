using System.CommandLine;
using System.Text.Json;
using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;

namespace Hypa.Cli.Commands;

public sealed class HookCommand(
    IHookIo io,
    IHarnessRegistry registry,
    HookService hookService)
{
    public Command Build()
    {
        var cmd = new Command("hook", "Process a PreToolUse hook payload from stdin and write agent-specific JSON to stdout.");
        var agentOpt = new Option<string?>("--agent") { Description = "Agent harness key (e.g. claude, codex). Auto-detects from payload if omitted." };
        cmd.Add(agentOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var agentKey = parseResult.GetValue(agentOpt);
            var json = await io.ReadStdinAsync(ct);
            if (json is null)
            {
                return 0;
            }

            var (adapter, input) = ResolveAdapterAndInput(json.Value, agentKey);
            if (adapter is null || input is null)
            {
                return 0;
            }

            try
            {
                var decision = await hookService.ProcessAsync(input, ct);
                var output = adapter.Format(decision, input);
                io.WriteOutput(output);
                return output.ExitCode;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"hypa hook: error processing hook: {ex.Message}");
                return 1;
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
