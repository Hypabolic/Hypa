using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;

namespace Hypa.Cli.Commands;

/// <summary>
/// Runtime skill install and removal for <c>hypa init</c> and <c>hypa uninstall</c>.
/// This step calls the integration service in process. It does not need a mux.
/// </summary>
internal static class RuntimeSkillStep
{
    public static bool TryMap(string agentKey, out OfficialIntegrationTarget target)
    {
        if (string.Equals(agentKey, "copilot-cli", StringComparison.Ordinal))
        {
            target = OfficialIntegrationTarget.Copilot;
            return true;
        }

        return OfficialIntegrationTargets.TryParse(agentKey, out target);
    }

    public static int InstallDetected(IOfficialIntegrationService integrations, string? agentKey)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        var targets = Select(integrations.DetectedTargets(), agentKey);
        if (targets.Count == 0)
            return 0;

        Console.WriteLine("[runtime skill]");
        var failed = false;
        foreach (var target in targets)
        {
            var result = integrations.InstallRuntimeSkill(target);
            if (!result.IsOk)
            {
                failed = true;
                Console.WriteLine($"  ! {target.Label()} ({result.Error.Message})");
                continue;
            }

            var detail = string.Join("; ", result.Value.Messages);
            Console.WriteLine($"  ✓ {target.Label()} ({detail})");
        }

        return failed ? 1 : 0;
    }

    public static int Remove(IOfficialIntegrationService integrations, string? agentKey)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        IReadOnlyList<OfficialIntegrationTarget> targets = agentKey is null
            ? OfficialIntegrationTargets.All
            : TryMap(agentKey, out var named)
                ? [named]
                : [];
        if (targets.Count == 0)
            return 0;

        var lines = new List<string>();
        var failed = false;
        foreach (var target in targets)
        {
            var result = integrations.RemoveRuntimeSkill(target);
            if (!result.IsOk)
            {
                failed = true;
                lines.Add($"  ! {target.Label()} ({result.Error.Message})");
                continue;
            }

            foreach (var message in result.Value.Messages)
            {
                if (message.StartsWith("removed ", StringComparison.Ordinal))
                    lines.Add($"  ✓ {target.Label()} ({message})");
            }
        }

        if (lines.Count == 0)
            return failed ? 1 : 0;

        Console.WriteLine("[runtime skill]");
        foreach (var line in lines)
            Console.WriteLine(line);
        return failed ? 1 : 0;
    }

    private static IReadOnlyList<OfficialIntegrationTarget> Select(
        IReadOnlyList<OfficialIntegrationTarget> detected,
        string? agentKey)
    {
        if (agentKey is null)
            return detected;
        if (!TryMap(agentKey, out var named))
            return [];
        foreach (var target in detected)
        {
            if (target == named)
                return [named];
        }

        return [];
    }
}
