using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class CopilotCliAdapter : IAgentHarnessAdapter
{
    // Only Copilot-documented shell runtimes (bash/powershell) and the Claude-mapped
    // PreToolUse name "Bash" (matched via OrdinalIgnoreCase — do NOT copy Codex's
    // broader set: Shell, command, exec_command, functions.exec_command, etc.).
    // Omitted on purpose: pwsh, cmd, and any non-shell tool (view, edit, …).
    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash",
        "powershell",
    };

    public string Key => "copilot-cli";
    public HarnessCapability Capability => HarnessCapability.PreToolUse;

    public AgentHookInput? Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return null;

        // --- Native camelCase (preToolUse) ---
        // No event-name gate: native payloads often omit an event field; the host
        // only invokes this hook for the registered preToolUse event (symmetric to
        // the PascalCase branch which checks hook_event_name == "PreToolUse").
        if (json.TryGetProperty("toolName", out var toolNameEl) &&
            toolNameEl.ValueKind == JsonValueKind.String)
        {
            var toolName = toolNameEl.GetString();
            if (toolName is null || !ShellToolNames.Contains(toolName))
                return null;

            if (!TryExtractCommandFromToolArgs(json, out var command))
                return null;

            // Normalise to the canonical "Bash" tool name so HookService's shell
            // gate fires (Copilot CLI reports the shell tool as lowercase "bash"
            // or "powershell").
            return new AgentHookInput("Bash", command, json);
        }

        // --- PascalCase / VS Code-compatible (PreToolUse) ---
        // Require the PreToolUse event name (case-sensitive per Copilot docs).
        // Pre-existing Claude adapter only checked presence of hook_event_name;
        // validating the value here is free safety so PostToolUse shells are not rewritten.
        if (!json.TryGetProperty("hook_event_name", out var eventEl) ||
            eventEl.ValueKind != JsonValueKind.String ||
            !string.Equals(eventEl.GetString(), "PreToolUse", StringComparison.Ordinal))
            return null;

        // Real Claude payloads must not be claimed here (mutual refusal with ClaudeCodeAdapter).
        if (ClaudePayloadMarkers.HasClaudeMarker(json))
            return null;

        if (!json.TryGetProperty("tool_name", out var snakeToolEl) ||
            snakeToolEl.ValueKind != JsonValueKind.String)
            return null;

        var snakeTool = snakeToolEl.GetString();
        if (snakeTool is null || !ShellToolNames.Contains(snakeTool))
            return null;

        if (!json.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetCommand(toolInput, out var cmd))
            return null;

        return new AgentHookInput("Bash", cmd, json);
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

    private static bool TryExtractCommandFromToolArgs(JsonElement json, out string command)
    {
        command = "";
        if (!json.TryGetProperty("toolArgs", out var toolArgsEl))
            return false;

        switch (toolArgsEl.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = toolArgsEl.GetString();
                if (string.IsNullOrEmpty(s))
                    return false;
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    return TryGetCommand(doc.RootElement, out command);
                }
                catch (JsonException)
                {
                    return false;
                }
            }
            case JsonValueKind.Object:
                return TryGetCommand(toolArgsEl, out command);
            default:
                return false;
        }
    }

    private static bool TryGetCommand(JsonElement argsRoot, out string command)
    {
        command = "";
        if (!argsRoot.TryGetProperty("command", out var commandEl) ||
            commandEl.ValueKind != JsonValueKind.String)
            return false;

        var c = commandEl.GetString();
        if (string.IsNullOrEmpty(c))
            return false;

        command = c;
        return true;
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
