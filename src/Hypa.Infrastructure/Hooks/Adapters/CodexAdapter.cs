using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class CodexAdapter(ISkillRenderer skillRenderer) : IAgentHarnessAdapter
{
    public string Key => "codex";
    public HarnessCapability Capability => HarnessCapability.PreToolUse | HarnessCapability.RulesFileSupport;

    public AgentHookInput? Parse(JsonElement json)
    {
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
            HookDecision.Rewrite r => FormatDeny($"Use: {r.Command}"),
            HookDecision.Deny d => FormatDeny(d.Reason),
            HookDecision.Ask a => FormatDeny(a.Reason),
            _ => new AgentHookOutput(0, null),
        };
    }

    public bool IsDetected(bool global, string? projectRoot = null)
    {
        if (global) return false;
        var root = projectRoot ?? Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(root, ".codex")) || File.Exists(Path.Combine(root, "AGENTS.md"));
    }

    public InstallPlan GetInstallPlan(bool global, string? projectRoot = null)
    {
        if (global)
        {
            return new InstallPlan([
                new InstallOperation.NotSupported("Codex is project-scoped; run `hypa init` without --global"),
            ]);
        }

        var root = projectRoot ?? Directory.GetCurrentDirectory();
        var ops = new List<InstallOperation>
        {
            new InstallOperation.PatchJsonHook(
                Path.Combine(root, ".codex", "hooks.json"),
                "PreToolUse",
                """{"matcher":"^Bash$","hooks":[{"type":"command","command":"hypa hook --agent codex","timeout":30}]}"""),

            new InstallOperation.PatchTomlKey(
                Path.Combine(root, ".codex", "config.toml"),
                "features",
                "codex_hooks",
                "true"),

            new InstallOperation.WriteFile(
                Path.Combine(root, "HYPA.md"),
                skillRenderer.GetRulesContent()),

            new InstallOperation.InjectLine(
                Path.Combine(root, "AGENTS.md"),
                "@HYPA.md",
                CreateIfMissing: true),
        };

        return new InstallPlan(ops);
    }

    private static AgentHookOutput FormatDeny(string reason)
    {
        var specific = new CodexHookSpecificOutput("PreToolUse", "deny", reason);
        var output = new CodexHookOutput(specific);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.CodexHookOutput);
        return new AgentHookOutput(0, json);
    }
}

internal sealed record CodexHookOutput(CodexHookSpecificOutput HookSpecificOutput);
internal sealed record CodexHookSpecificOutput(
    string HookEventName,
    string PermissionDecision,
    string PermissionDecisionReason);
