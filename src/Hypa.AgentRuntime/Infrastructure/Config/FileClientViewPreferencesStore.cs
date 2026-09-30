using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// store. Path follows Hypa attach config: <c>HYPA_CONFIG_PATH</c>
/// sibling, then <c>XDG_CONFIG_HOME/hypa</c>, then
/// <c>~/.config/hypa</c>.
/// </summary>
public sealed class FileClientViewPreferencesStore : IClientViewPreferencesStore
{
    public const string FileName = "client-view.json";

    private readonly IAttachConfigEnvironment _env;
    private readonly IAttachConfigFiles? _files;

    public FileClientViewPreferencesStore(
        IAttachConfigEnvironment? env = null,
        IAttachConfigFiles? files = null)
    {
        _env = env ?? new SystemAttachConfigEnvironment();
        _files = files;
    }

    public string ResolvePath()
    {
        var explicitConfig = _env.GetVariable(FileAttachConfigLoader.ConfigPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitConfig))
        {
            var directory = Path.GetDirectoryName(explicitConfig.Trim());
            if (string.IsNullOrWhiteSpace(directory))
                directory = ".";
            return FileAttachConfigLoader.CanonicalizePath(Path.Combine(directory, FileName));
        }

        var xdg = _env.GetVariable(FileAttachConfigLoader.XdgConfigHomeVariable);
        if (!string.IsNullOrWhiteSpace(xdg))
            return FileAttachConfigLoader.CanonicalizePath(Path.Combine(xdg.Trim(), "hypa", FileName));

        if (_env.IsWindows && !string.IsNullOrWhiteSpace(_env.AppData))
            return FileAttachConfigLoader.CanonicalizePath(Path.Combine(_env.AppData, "hypa", FileName));

        return FileAttachConfigLoader.CanonicalizePath(
            Path.Combine(_env.UserHome, ".config", "hypa", FileName));
    }

    public ClientViewPreferences Load()
    {
        var path = ResolvePath();
        if (!Exists(path))
            return ClientViewPreferences.Empty;

        string text;
        try
        {
            text = Read(path);
        }
        catch (Exception)
        {
            return ClientViewPreferences.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
            return ClientViewPreferences.Empty;

        try
        {
            var loaded = JsonSerializer.Deserialize(
                text,
                ClientViewPreferencesJsonContext.Default.ClientViewPreferences);
            if (loaded is null)
                return ClientViewPreferences.Empty;
            return Normalize(loaded);
        }
        catch (JsonException)
        {
            return ClientViewPreferences.Empty;
        }
    }

    public bool Save(ClientViewPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var path = ResolvePath();
        var normalized = Normalize(preferences);
        string json;
        try
        {
            json = JsonSerializer.Serialize(
                normalized,
                ClientViewPreferencesJsonContext.Default.ClientViewPreferences);
        }
        catch (JsonException)
        {
            return false;
        }

        try
        {
            WriteAtomic(path, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ClientViewPreferences Normalize(ClientViewPreferences preferences)
    {
        float? split = preferences.SidebarSectionSplit is { } value
            ? SidebarTwoPaneLayoutPolicy.ClampSplitRatio(value)
            : null;
        string[]? groups = null;
        if (preferences.CollapsedGroups is { Length: > 0 } raw)
        {
            var unique = raw
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            if (unique.Length > 0)
                groups = unique;
        }

        var lastPlacement = string.IsNullOrWhiteSpace(preferences.LastPlacementId)
            ? null
            : preferences.LastPlacementId.Trim();
        if (split is null && groups is null && lastPlacement is null)
            return ClientViewPreferences.Empty;
        return new ClientViewPreferences
        {
            SidebarSectionSplit = split,
            CollapsedGroups = groups,
            LastPlacementId = lastPlacement,
        };
    }

    private bool Exists(string path) =>
        _files?.FileExists(path) ?? File.Exists(path);

    private string Read(string path) =>
        _files is not null ? _files.ReadAllText(path) : File.ReadAllText(path);

    private void WriteAtomic(string path, string json)
    {
        if (_files is not null)
        {
            _files.WriteAllText(path, json);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        File.WriteAllText(temp, json);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
