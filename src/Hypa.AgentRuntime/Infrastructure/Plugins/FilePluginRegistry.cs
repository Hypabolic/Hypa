using System.Text.Json;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// reads, atomic save. User-global Hypa config. Load never throws.
/// the server still starts. Mutations still use a strict read.
/// </summary>
public sealed class FilePluginRegistry : IPluginRegistry
{
    private readonly IPluginFiles _files;
    private readonly IPluginPathRoots _paths;
    private readonly object _gate = new();

    public FilePluginRegistry(IPluginFiles files, IPluginPathRoots paths)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public PluginResult<IReadOnlyList<InstalledPlugin>> Load()
    {
        lock (_gate)
            return LoadUnlocked();
    }

    public PluginResult<IReadOnlyList<InstalledPlugin>> Update(
        Func<List<InstalledPlugin>, PluginResult<IReadOnlyList<InstalledPlugin>>> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            var loaded = LoadUnlocked();
            if (!loaded.IsOk)
                return loaded;
            var list = loaded.Value.ToList();
            var result = mutation(list);
            if (!result.IsOk)
                return result;
            var sorted = result.Value.OrderBy(p => p.PluginId, StringComparer.Ordinal).ToList();
            var saved = SaveUnlocked(sorted);
            if (!saved.IsOk)
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(saved.Error);
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(sorted);
        }
    }

    private PluginResult<IReadOnlyList<InstalledPlugin>> LoadUnlocked()
    {
        try
        {
            if (!_files.FileExists(_paths.RegistryPath))
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok([]);
            var json = _files.ReadAllText(_paths.RegistryPath);
            if (string.IsNullOrWhiteSpace(json))
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok([]);
            var list = JsonSerializer.Deserialize(json, PluginRegistryJsonContext.Default.ListInstalledPlugin);
            if (list is null || list.Count == 0)
                return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok([]);
            var normalized = new List<InstalledPlugin>(list.Count);
            foreach (var plugin in list)
            {
                if (plugin is null)
                    continue;
                try
                {
                    normalized.Add(plugin.WithNonNullCollections());
                }
                catch
                {
                    // Skip one bad row. Do not fail the whole registry.
                }
            }

            return PluginResult<IReadOnlyList<InstalledPlugin>>.Ok(normalized);
        }
        catch (Exception ex)
        {
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(
                PluginError.RegistryLoadFailed,
                ex.Message);
        }
    }

    private PluginResult<bool> SaveUnlocked(List<InstalledPlugin> plugins)
    {
        try
        {
            var json = JsonSerializer.Serialize(plugins, PluginRegistryJsonContext.Default.ListInstalledPlugin);
            _files.CreateDirectory(_paths.ConfigRoot);
            _files.WriteAllText(_paths.RegistryPath, json);
            return PluginResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult<bool>.Fail(PluginError.RegistrySaveFailed, ex.Message);
        }
    }
}
