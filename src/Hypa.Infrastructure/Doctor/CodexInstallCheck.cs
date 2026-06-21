using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Doctor;

public sealed class CodexInstallCheck : IDoctorCheck
{
    private readonly IFileSystem _fileSystem;
    private readonly string? _stateFilePath;

    public CodexInstallCheck(IFileSystem fileSystem) : this(fileSystem, null) { }

    internal CodexInstallCheck(IFileSystem fileSystem, string? stateFilePath)
    {
        _fileSystem = fileSystem;
        _stateFilePath = stateFilePath;
    }

    public string Category => "Codex";

    public DoctorCheckResult Run()
    {
        var initWithMcp = _stateFilePath is null
            ? InstallStateReader.ReadInitWithMcp()
            : InstallStateReader.ReadInitWithMcp(_stateFilePath);

        var configRoot = CodexConfigPaths.ResolveHome();
        var hooksPath = Path.Combine(configRoot, "hooks.json");
        var configPath = Path.Combine(configRoot, "config.toml");

        var hooksExist = _fileSystem.FileExists(hooksPath);
        var configExists = _fileSystem.FileExists(configPath);

        if (!hooksExist && !configExists)
            return new DoctorCheckResult("Codex install", "not configured", DoctorStatus.Ok);

        var warnings = new List<string>();

        if (hooksExist)
        {
            var hooks = _fileSystem.ReadAllText(hooksPath);
            if (!hooks.Contains("hypa hook", StringComparison.Ordinal))
                warnings.Add("hooks.json has no Hypa PreToolUse hook; run `hypa init --global --agent codex`");
            else if (!HasBroadMatcher(hooks))
                warnings.Add("hook matcher is narrow; run `hypa init --global --agent codex`");
        }
        else
        {
            warnings.Add("hooks.json not found; run `hypa init --global --agent codex`");
        }

        var mcpPresent = false;
        if (configExists)
        {
            var config = _fileSystem.ReadAllText(configPath);
            if (!CodexHooksFeatureEnabled(config))
                warnings.Add("`[features] hooks = true` not set; run `hypa init --global --agent codex`");

            mcpPresent = HasMcpServer(config);
            if (initWithMcp && !mcpPresent)
                warnings.Add("MCP server not registered; run `hypa init --global --agent codex --with-mcp`");
        }
        else
        {
            warnings.Add("config.toml not found; run `hypa init --global --agent codex`");
        }

        if (warnings.Count > 0)
            return new DoctorCheckResult("Codex install", "incomplete", DoctorStatus.Warn,
                string.Join("; ", warnings));

        var okMessage = (initWithMcp && mcpPresent) ? "hooks and MCP registered" : "hooks registered";
        return new DoctorCheckResult("Codex install", okMessage, DoctorStatus.Ok);
    }

    private static bool HasBroadMatcher(string hooksJson) =>
        hooksJson.Contains("exec_command", StringComparison.Ordinal);

    private static bool HasMcpServer(string configToml)
    {
        var inSection = false;
        var hasCommand = false;
        var hasServeArg = false;

        foreach (var rawLine in configToml.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed == "[mcp_servers.hypa]";
                continue;
            }
            if (!inSection) continue;
            var withoutComment = trimmed.Split('#')[0].Trim();
            if (withoutComment.StartsWith("command", StringComparison.Ordinal) && withoutComment.Contains('='))
                hasCommand = true;
            if (withoutComment.StartsWith("args", StringComparison.Ordinal) &&
                withoutComment.Contains("\"serve\"", StringComparison.Ordinal))
                hasServeArg = true;
        }

        return hasCommand && hasServeArg;
    }

    private static bool CodexHooksFeatureEnabled(string configContent)
    {
        var currentSection = "";
        foreach (var rawLine in configContent.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed.TrimStart('[').TrimEnd(']').Trim();
                continue;
            }
            if (currentSection != "features") continue;
            var withoutComment = trimmed.Split('#')[0].Trim();
            if (IsTomlBoolAssignment(withoutComment, "hooks", expected: true))
                return true;
        }
        return false;
    }

    private static bool IsTomlBoolAssignment(string line, string key, bool expected)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        var remainder = line[key.Length..].TrimStart();
        if (!remainder.StartsWith('=')) return false;
        var value = remainder[1..].Trim();
        return string.Equals(value, expected ? "true" : "false", StringComparison.OrdinalIgnoreCase);
    }
}
