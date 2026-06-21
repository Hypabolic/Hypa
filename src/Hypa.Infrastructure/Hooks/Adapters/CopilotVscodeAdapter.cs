using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class CopilotVscodeAdapter : IAgentHarnessAdapter
{
    public string Key => "copilot-vscode";
    public HarnessCapability Capability => HarnessCapability.PreToolUse;

    public AgentHookInput? Parse(JsonElement json)
    {
        if (!json.TryGetProperty("tool_name", out var toolNameEl))
            return null;

        var toolName = toolNameEl.GetString();
        if (toolName != "runTerminalCommand")
            return null;

        if (!json.TryGetProperty("tool_input", out var toolInput))
            return null;

        if (!toolInput.TryGetProperty("command", out var commandEl))
            return null;

        var command = commandEl.GetString();
        if (command is null)
            return null;

        return new AgentHookInput(toolName, command, json);
    }

    public AgentHookOutput Format(HookDecision decision, AgentHookInput input)
    {
        return decision switch
        {
            HookDecision.Rewrite r => FormatRewrite(r.Command),
            HookDecision.Deny d => FormatBlock(d.Reason),
            HookDecision.Ask a => FormatBlock(a.Reason),
            _ => new AgentHookOutput(0, null),
        };
    }

    public bool IsDetected(bool global, string? projectRoot = null) => false;

    public bool IsAvailable() => false;

    public InstallPlan GetInstallPlan(bool global, bool includeMcp, string? projectRoot = null)
    {
        return new InstallPlan([
            new InstallOperation.NotSupported("Copilot VS Code hooks must be configured via VS Code settings UI"),
        ]);
    }

    public UninstallPlan GetUninstallPlan(bool global, string? projectRoot = null)
    {
        return new UninstallPlan([
            new UninstallOperation.NotSupported("Manual config required — remove hooks from VS Code settings manually"),
        ]);
    }

    private static AgentHookOutput FormatRewrite(string command)
    {
        var output = new ClaudeUpdatedInput(new ClaudeUpdatedCommand(command));
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.ClaudeUpdatedInput);
        return new AgentHookOutput(0, json);
    }

    private static AgentHookOutput FormatBlock(string reason)
    {
        var output = new ClaudeBlockDecision("block", reason);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.ClaudeBlockDecision);
        return new AgentHookOutput(0, json);
    }
}
