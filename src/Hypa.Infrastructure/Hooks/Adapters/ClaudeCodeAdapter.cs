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

        if (toolName == "Bash")
        {
            if (!json.TryGetProperty("tool_input", out var toolInput))
                return null;
            if (!toolInput.TryGetProperty("command", out var commandEl))
                return null;
            var command = commandEl.GetString();
            if (command is null)
                return null;
            return new AgentHookInput(toolName, command, json);
        }

        if (toolName is "Read" or "Grep")
        {
            var path = json.TryGetProperty("tool_input", out var readInput)
                && readInput.TryGetProperty("path", out var pathEl)
                ? pathEl.GetString() ?? ""
                : "";
            return new AgentHookInput(toolName, "", json, Path: path);
        }

        // All other tools: return a passthrough input so Hypa can observe them.
        return new AgentHookInput(toolName ?? "Unknown", "", json);
    }

    public AgentHookOutput Format(HookDecision decision, AgentHookInput input)
    {
        return decision switch
        {
            HookDecision.Rewrite r => FormatRewrite(r.Command),
            HookDecision.Redirect r => FormatRedirect(r.TempPath),
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
            var claudeMdPath = Path.Combine(home, ".claude", "CLAUDE.md");

            ops.Add(new InstallOperation.PatchJsonHook(
                settingsPath,
                "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}"""));

            ops.Add(new InstallOperation.WriteFile(skillPath, skillRenderer.Render(fullSections: true)));

            ops.Add(new InstallOperation.PatchJsonObject(
                settingsPath,
                "mcpServers",
                "hypa",
                """{"type":"stdio","command":"hypa","args":["serve"]}"""));

            ops.Add(new InstallOperation.InjectFencedBlock(
                claudeMdPath,
                "hypa",
                ClaudeMdBlock));
        }
        else
        {
            var root = projectRoot ?? Directory.GetCurrentDirectory();
            var settingsPath = Path.Combine(root, ".claude", "settings.local.json");

            ops.Add(new InstallOperation.PatchJsonHook(
                settingsPath,
                "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}"""));
        }

        return new InstallPlan(ops);
    }

    public UninstallPlan GetUninstallPlan(bool global, string? projectRoot = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (global)
        {
            var settingsPath = Path.Combine(home, ".claude", "settings.json");
            var skillDir = Path.Combine(home, ".claude", "skills", "hypa");
            var claudeMdPath = Path.Combine(home, ".claude", "CLAUDE.md");

            return new UninstallPlan([
                new UninstallOperation.RemoveJsonHook(
                    settingsPath,
                    "PreToolUse",
                    """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}"""),
                new UninstallOperation.DeleteDirectory(skillDir),
                new UninstallOperation.RemoveJsonObject(settingsPath, "mcpServers", "hypa"),
                new UninstallOperation.RemoveFencedBlock(claudeMdPath, "hypa"),
            ]);
        }
        else
        {
            var root = projectRoot ?? Directory.GetCurrentDirectory();
            var settingsPath = Path.Combine(root, ".claude", "settings.local.json");

            return new UninstallPlan([
                new UninstallOperation.RemoveJsonHook(
                    settingsPath,
                    "PreToolUse",
                    """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}"""),
            ]);
        }
    }

    private static AgentHookOutput FormatRewrite(string command)
    {
        var output = new ClaudeUpdatedInput(new ClaudeUpdatedCommand(command));
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.ClaudeUpdatedInput);
        return new AgentHookOutput(0, json);
    }

    private static AgentHookOutput FormatRedirect(string tempPath)
    {
        var output = new ClaudeRedirectInput(new ClaudeRedirectPath(tempPath));
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.ClaudeRedirectInput);
        return new AgentHookOutput(0, json);
    }

    private static AgentHookOutput FormatBlock(string reason)
    {
        var output = new ClaudeBlockDecision("block", reason);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.ClaudeBlockDecision);
        return new AgentHookOutput(0, json);
    }

    private const string ClaudeMdBlock = """
        <!-- hypa -->
        ## Hypa — Command Compression

        Hypa is active as a PreToolUse hook.
        - **Bash**: Commands are rewritten for compressed, token-efficient output.
        - **Read**: Large code files are redirected through Hypa's smart reader (outline mode). Use `hypa_read` MCP tool for explicit control over read mode (full | outline | signatures | pruned | smart).

        Run `hypa doctor` to verify setup.
        <!-- /hypa -->
        """;
}

internal sealed record ClaudeUpdatedInput(ClaudeUpdatedCommand UpdatedInput);
internal sealed record ClaudeUpdatedCommand(string Command);
internal sealed record ClaudeBlockDecision(string Decision, string Reason);
internal sealed record ClaudeRedirectInput(ClaudeRedirectPath UpdatedInput);
internal sealed record ClaudeRedirectPath(string Path);
