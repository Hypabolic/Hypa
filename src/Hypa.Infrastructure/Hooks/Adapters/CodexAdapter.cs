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
        if (global)
        {
            var codexHome = CodexConfigPaths.ResolveHome();
            return Directory.Exists(codexHome) ||
                   File.Exists(Path.Combine(codexHome, "config.toml")) ||
                   File.Exists(Path.Combine(codexHome, "hooks.json")) ||
                   File.Exists(Path.Combine(codexHome, "AGENTS.md"));
        }

        var root = projectRoot ?? throw new ArgumentException(
            "Project root is required for project-scoped Codex detection.",
            nameof(projectRoot));
        return Directory.Exists(Path.Combine(root, ".codex")) || File.Exists(Path.Combine(root, "AGENTS.md"));
    }

    public bool IsAvailable()
    {
        var codexHome = CodexConfigPaths.ResolveHome();
        return Directory.Exists(codexHome) ||
               File.Exists(Path.Combine(codexHome, "config.toml")) ||
               File.Exists(Path.Combine(codexHome, "hooks.json")) ||
               File.Exists(Path.Combine(codexHome, "AGENTS.md")) ||
               CommandExists("codex");
    }

    public InstallPlan GetInstallPlan(bool global, string? projectRoot = null)
    {
        if (!global && projectRoot is null)
            throw new ArgumentException(
                "Project root is required for project-scoped Codex install plans.",
                nameof(projectRoot));

        var root = CodexConfigPaths.ResolveRoot(global, projectRoot);
        var configRoot = global ? root : Path.Combine(root, ".codex");
        var hypaDocPath = Path.Combine(root, "HYPA.md");
        var hypaDocRef = global ? "@" + hypaDocPath : "@HYPA.md";
        var hookJson = CreateHookJson(ResolveHypaHookCommand());
        var ops = new List<InstallOperation>
        {
            new InstallOperation.PatchJsonHook(
                Path.Combine(configRoot, "hooks.json"),
                "PreToolUse",
                hookJson),

            new InstallOperation.EnsureCodexHooksFeature(
                Path.Combine(configRoot, "config.toml")),

            new InstallOperation.WriteFile(
                hypaDocPath,
                skillRenderer.GetRulesContent()),

            new InstallOperation.InjectLine(
                Path.Combine(root, "AGENTS.md"),
                hypaDocRef,
                CreateIfMissing: true),
        };

        return new InstallPlan(ops);
    }

    public UninstallPlan GetUninstallPlan(bool global, string? projectRoot = null)
    {
        var root = CodexConfigPaths.ResolveRoot(global, projectRoot);
        var configRoot = global ? root : Path.Combine(root, ".codex");
        var hooksPath = Path.Combine(configRoot, "hooks.json");
        var configPath = Path.Combine(configRoot, "config.toml");
        var hypaDocPath = Path.Combine(root, "HYPA.md");
        var hypaDocRef = global ? "@" + hypaDocPath : "@HYPA.md";
        return new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(
                hooksPath,
                "PreToolUse",
                CreateHookJson(ResolveHypaHookCommand())),
            new UninstallOperation.RemoveJsonHook(
                hooksPath,
                "PreToolUse",
                CreateHookJson("hypa hook --agent codex")),
            new UninstallOperation.RemoveCodexHooksFeatureIfUnused(configPath, hooksPath),
            new UninstallOperation.DeleteFile(hypaDocPath),
            new UninstallOperation.RemoveLine(Path.Combine(root, "AGENTS.md"), hypaDocRef),
        ]);
    }

    private static AgentHookOutput FormatDeny(string reason)
    {
        var specific = new CodexHookSpecificOutput("PreToolUse", "deny", reason);
        var output = new CodexHookOutput(specific);
        var json = JsonSerializer.Serialize(output, HooksJsonContext.Default.CodexHookOutput);
        return new AgentHookOutput(0, json);
    }

    private static string CreateHookJson(string command)
    {
        var encodedCommand = JsonEncodedText.Encode(command).ToString();
        return $$"""{"matcher":"Bash","hooks":[{"type":"command","command":"{{encodedCommand}}","timeout":30}]}""";
    }

    private static string ResolveHypaHookCommand()
    {
        var binary = ResolveHypaBinaryPath();
        return $"{QuoteShellToken(binary)} hook --agent codex";
    }

    private static string ResolveHypaBinaryPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return "hypa";

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "hypa");
            if (File.Exists(candidate))
                return candidate;

            if (OperatingSystem.IsWindows())
            {
                var exeCandidate = Path.Combine(dir, "hypa.exe");
                if (File.Exists(exeCandidate))
                    return exeCandidate;
            }
        }

        return "hypa";
    }

    private static string QuoteShellToken(string value)
    {
        if (value.Length == 0 || value.Any(NeedsShellQuoting))
            return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

        return value;
    }

    private static bool NeedsShellQuoting(char c) =>
        !(char.IsLetterOrDigit(c) || c is '/' or '\\' or ':' or '.' or '_' or '-');

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, command)))
                return true;

            if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, command + ".exe")))
                return true;
        }

        return false;
    }
}

internal sealed record CodexHookOutput(CodexHookSpecificOutput HookSpecificOutput);
internal sealed record CodexHookSpecificOutput(
    string HookEventName,
    string PermissionDecision,
    string PermissionDecisionReason);
