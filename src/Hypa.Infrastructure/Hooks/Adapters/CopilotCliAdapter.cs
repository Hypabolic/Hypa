using System.Text.Json;
using System.Text.Json.Serialization;
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

            // Normalise to the canonical "Bash" tool name so HookService's shell
            // gate fires (Copilot CLI reports the shell tool as lowercase "bash").
            return new AgentHookInput("Bash", command, json);
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
            HookDecision.Rewrite r => FormatModifiedArgs(r.Command),
            HookDecision.Deny d => FormatPermission("deny", d.Reason),
            HookDecision.Ask a => FormatPermission("ask", a.Reason),
            _ => new AgentHookOutput(0, null),
        };
    }

    public bool IsDetected(bool global, string? projectRoot = null) => false;

    public bool IsAvailable() => false;

    public InstallPlan GetInstallPlan(bool global, bool includeMcp, string? projectRoot = null)
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

    private static AgentHookOutput FormatModifiedArgs(string command)
    {
        var output = new CopilotCliOutput(null, null, new CopilotCliModifiedArgs(command));
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.CopilotCliOutput);
        return new AgentHookOutput(0, json);
    }

    private static AgentHookOutput FormatPermission(string decision, string reason)
    {
        var output = new CopilotCliOutput(decision, reason, null);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.CopilotCliOutput);
        return new AgentHookOutput(0, json);
    }

}

internal sealed record CopilotCliOutput(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionDecision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionDecisionReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CopilotCliModifiedArgs? ModifiedArgs);

internal sealed record CopilotCliModifiedArgs(string Command);
