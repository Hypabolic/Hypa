using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class CopilotCliAdapter : IAgentHarnessAdapter
{
    public string Key => "copilot-cli";
    public HarnessCapability Capability => HarnessCapability.PreToolUse;

    public AgentHookInput? Parse(JsonElement json)
    {
        if (!json.TryGetProperty("toolName", out var toolNameEl))
            return null;

        var toolName = toolNameEl.GetString();
        if (toolName != "bash")
            return null;

        if (!json.TryGetProperty("toolArgs", out var toolArgsEl))
            return null;

        var toolArgsJson = toolArgsEl.GetString();
        if (toolArgsJson is null)
            return null;

        try
        {
            using var argsDoc = JsonDocument.Parse(toolArgsJson);
            if (!argsDoc.RootElement.TryGetProperty("command", out var commandEl))
                return null;

            var command = commandEl.GetString();
            if (command is null)
                return null;

            return new AgentHookInput(toolName, command, json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public AgentHookOutput Format(HookDecision decision, AgentHookInput input)
    {
        return decision switch
        {
            HookDecision.Rewrite r => FormatDenyWithSuggestion($"Use: {r.Command}"),
            HookDecision.Deny d => FormatDenyWithSuggestion(d.Reason),
            HookDecision.Ask a => FormatDenyWithSuggestion(a.Reason),
            _ => new AgentHookOutput(0, null),
        };
    }

    public bool IsDetected(bool global, string? projectRoot = null) => false;

    public bool IsAvailable() => false;

    public InstallPlan GetInstallPlan(bool global, string? projectRoot = null)
    {
        return new InstallPlan([
            new InstallOperation.NotSupported("Copilot CLI hooks must be configured via `gh copilot config`"),
        ]);
    }

    public UninstallPlan GetUninstallPlan(bool global, string? projectRoot = null)
    {
        return new UninstallPlan([
            new UninstallOperation.NotSupported("Manual config required — remove hooks from VS Code settings manually"),
        ]);
    }

    private static AgentHookOutput FormatDenyWithSuggestion(string reason)
    {
        var output = new CopilotCliOutput("deny", reason);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.CopilotCliOutput);
        return new AgentHookOutput(0, json);
    }

}

internal sealed record CopilotCliOutput(
    string PermissionDecision,
    string PermissionDecisionReason);
