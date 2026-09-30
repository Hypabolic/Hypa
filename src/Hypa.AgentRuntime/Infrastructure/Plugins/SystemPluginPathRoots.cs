using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// User-global under <c>~/.hypa</c>.
/// </summary>
public sealed class SystemPluginPathRoots : IPluginPathRoots
{
    public SystemPluginPathRoots(string? configRoot = null)
    {
        ConfigRoot = string.IsNullOrWhiteSpace(configRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".hypa")
            : Path.GetFullPath(configRoot);
    }

    public string ConfigRoot { get; }

    public string RegistryPath => Path.Combine(ConfigRoot, "plugins.json");

    public string RegistryLockPath => Path.Combine(ConfigRoot, ".plugins.lock");

    public string PluginConfigDir(string pluginId) =>
        Path.Combine(ConfigRoot, "plugins", "config", Sanitize(pluginId));

    public string PluginStateDir(string pluginId) =>
        Path.Combine(ConfigRoot, "plugins", "state", Sanitize(pluginId));

    private static string Sanitize(string pluginId)
    {
        var chars = pluginId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
                continue;
            chars[i] = '_';
        }

        var value = new string(chars);
        return value.Length == 0 ? "plugin" : value;
    }
}
