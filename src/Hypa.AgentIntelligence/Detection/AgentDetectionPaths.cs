using Hypa.AgentRuntime.Application;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>
/// Local: ~/.config/hypa/agent-detection/&lt;agent&gt;.toml
/// Remote cache: state/hypa/agent-detection/remote/&lt;agent&gt;.toml
/// </summary>
internal sealed class AgentDetectionPaths
{
    public const string CatalogUrlVariable = "HYPA_AGENT_DETECTION_MANIFEST_CATALOG_URL";
    public const string StateDirVariable = "HYPA_AGENT_DETECTION_STATE_DIR";
    public const string XdgConfigHomeVariable = "XDG_CONFIG_HOME";
    public const string XdgStateHomeVariable = "XDG_STATE_HOME";

    private readonly IAttachConfigEnvironment _env;

    public AgentDetectionPaths(IAttachConfigEnvironment env)
    {
        _env = env;
    }

    public string ConfigDirectory
    {
        get
        {
            var xdg = _env.GetVariable(XdgConfigHomeVariable);
            if (!string.IsNullOrWhiteSpace(xdg))
                return Path.Combine(xdg.Trim(), "hypa");
            if (_env.IsWindows && !string.IsNullOrWhiteSpace(_env.AppData))
                return Path.Combine(_env.AppData, "hypa");
            return Path.Combine(_env.UserHome, ".config", "hypa");
        }
    }

    public string StateDirectory
    {
        get
        {
            var explicitDir = _env.GetVariable(StateDirVariable);
            if (!string.IsNullOrWhiteSpace(explicitDir))
                return explicitDir.Trim();
            var xdg = _env.GetVariable(XdgStateHomeVariable);
            if (!string.IsNullOrWhiteSpace(xdg))
                return Path.Combine(xdg.Trim(), "hypa", "agent-detection");
            if (_env.IsWindows && !string.IsNullOrWhiteSpace(_env.AppData))
                return Path.Combine(_env.AppData, "hypa", "agent-detection");
            return Path.Combine(_env.UserHome, ".local", "state", "hypa", "agent-detection");
        }
    }

    public string OverridePath(string agentId) =>
        Path.Combine(ConfigDirectory, "agent-detection", agentId + ".toml");

    public string RemotePath(string agentId) =>
        Path.Combine(StateDirectory, "remote", agentId + ".toml");

    public string StatusPath => Path.Combine(StateDirectory, "status.toml");

    public string? CatalogUrl
    {
        get
        {
            var value = _env.GetVariable(CatalogUrlVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
