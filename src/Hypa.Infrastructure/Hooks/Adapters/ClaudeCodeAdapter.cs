using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class ClaudeCodeAdapter(ISkillRenderer skillRenderer) : IAgentHarnessAdapter
{
    public string Key => "claude";
    public HarnessCapability Capability => HarnessCapability.PreToolUse | HarnessCapability.McpServer;

    public AgentHookInput? Parse(JsonElement json)
    {
        // hook_event_name is always present in Claude Code payloads; its absence means this is not a Claude payload.
        if (!json.TryGetProperty("hook_event_name", out _))
            return null;

        if (!json.TryGetProperty("tool_name", out var toolNameEl))
            return null;

        var toolName = toolNameEl.GetString();
        if (toolName != "Bash")
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

    public bool IsDetected(bool global, string? projectRoot = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (global)
            return Directory.Exists(Path.Combine(home, ".claude"));
        var root = projectRoot ?? Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(root, ".claude"));
    }

    public InstallPlan GetInstallPlan(bool global, string? projectRoot = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ops = new List<InstallOperation>();

        if (global)
        {
            var settingsPath = Path.Combine(home, ".claude", "settings.json");
            var skillPath = Path.Combine(home, ".claude", "skills", "hypa", "SKILL.md");

            ops.Add(new InstallOperation.PatchJsonHook(
                settingsPath,
                "PreToolUse",
                """{"type":"command","command":"hypa hook","timeout":5}"""));

            ops.Add(new InstallOperation.WriteFile(skillPath, skillRenderer.Render(fullSections: true)));

            ops.Add(new InstallOperation.PatchJsonObject(
                settingsPath,
                "mcpServers",
                "hypa",
                """{"type":"stdio","command":"hypa","args":["serve"]}"""));
        }
        else
        {
            var root = projectRoot ?? Directory.GetCurrentDirectory();
            var settingsPath = Path.Combine(root, ".claude", "settings.json");

            ops.Add(new InstallOperation.PatchJsonHook(
                settingsPath,
                "PreToolUse",
                """{"type":"command","command":"hypa hook","timeout":5}"""));
        }

        return new InstallPlan(ops);
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

internal sealed record ClaudeUpdatedInput(ClaudeUpdatedCommand UpdatedInput);
internal sealed record ClaudeUpdatedCommand(string Command);
internal sealed record ClaudeBlockDecision(string Decision, string Reason);
