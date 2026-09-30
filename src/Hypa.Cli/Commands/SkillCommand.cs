using System.CommandLine;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Cli.Commands;

public sealed class SkillCommand(IHarnessRegistry registry, ISkillRenderer renderer)
{
    public Command Build()
    {
        var cmd = new Command("skill", "Manage and display Hypa skill documentation.");

        cmd.Add(BuildShow());
        cmd.Add(BuildList());

        return cmd;
    }

    private Command BuildShow()
    {
        var show = new Command("show", "Print the Hypa SKILL.md (sections 1+2 by default).");
        var fullOpt = new Option<bool>("--full") { Description = "Print all sections." };
        var mcpOpt = new Option<bool>("--with-mcp") { Description = "Include MCP-specific sections." };
        var agentOpt = new Option<string?>("--agent") { Description = "Show install instructions for the named harness." };
        show.Add(fullOpt);
        show.Add(mcpOpt);
        show.Add(agentOpt);
        show.SetAction(parseResult =>
        {
            var full = parseResult.GetValue(fullOpt);
            var includeMcp = parseResult.GetValue(mcpOpt);
            var agentKey = parseResult.GetValue(agentOpt);

            Console.WriteLine(renderer.Render(fullSections: full, includeMcp: includeMcp));

            if (agentKey is not null)
            {
                var adapter = registry.Find(agentKey);
                if (adapter is null)
                {
                    Console.Error.WriteLine($"Unknown harness '{agentKey}'. Run `hypa skill list` to see available harnesses.");
                    return 1;
                }

                Console.WriteLine();
                Console.WriteLine($"## Harness: {adapter.Key}");
                var caps = adapter.Capability == HarnessCapability.None
                    ? "none"
                    : string.Join(", ", Enum.GetValues<HarnessCapability>()
                        .Where(c => c != HarnessCapability.None && adapter.Capability.HasFlag(c))
                        .Select(c => c.ToString()));
                Console.WriteLine($"Capabilities: {caps}");
                var installHint = adapter.GetInstallPlan(global: true, includeMcp: false).Operations
                    .Any(op => op is InstallOperation.NotSupported)
                    ? $"hypa init --agent {adapter.Key}"
                    : $"hypa init --global --agent {adapter.Key}";
                Console.WriteLine($"Install:      {installHint}");
            }

            return 0;
        });
        return show;
    }

    private Command BuildList()
    {
        var list = new Command("list", "List all registered agent harnesses and their capabilities.");
        list.SetAction(_ =>
        {
            foreach (var adapter in registry.All)
            {
                var caps = adapter.Capability == HarnessCapability.None
                        ? "none"
                        : string.Join(", ", Enum.GetValues<HarnessCapability>()
                            .Where(c => c != HarnessCapability.None && adapter.Capability.HasFlag(c))
                            .Select(c => c.ToString()));
                Console.WriteLine($"{adapter.Key,-20} {caps}");
            }
            return 0;
        });
        return list;
    }
}
