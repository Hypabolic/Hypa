using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

public static class PluginHostFactory
{
    public static PluginHostService CreateSystem(
        string? configRoot = null,
        string? binPath = null,
        string? socketPath = null,
        IPluginContextSource? context = null)
    {
        var files = new SystemPluginFiles();
        var clock = new SystemPluginClock();
        var paths = new SystemPluginPathRoots(configRoot);
        var parser = new PluginManifestParser();
        var registry = new FilePluginRegistry(files, paths);
        var processRegistry = new PluginProcessRegistry();
        return new PluginHostService(
            files,
            clock,
            paths,
            parser,
            registry,
            new ProcessPluginLauncher(processRegistry),
            context,
            binPath,
            socketPath,
            processRegistry,
            new ProcessPluginRefreshRunner(processRegistry));
    }

    /// <summary>
    /// In-memory empty host. Used when the user registry cannot start.
    /// </summary>
    public static PluginHostService CreateEmpty(IPluginContextSource? context = null)
    {
        var files = new EmptyPluginFiles();
        var clock = new SystemPluginClock();
        var paths = new SystemPluginPathRoots(
            Path.Combine(Path.GetTempPath(), "hypa-plugins-empty"));
        return new PluginHostService(
            files,
            clock,
            paths,
            new PluginManifestParser(),
            new EmptyPluginRegistry(),
            new ProcessPluginLauncher(),
            context);
    }

    private sealed class EmptyPluginRegistry : IPluginRegistry
    {
        public PluginResult<IReadOnlyList<InstalledPlugin>> Load() =>
            PluginResult<IReadOnlyList<InstalledPlugin>>.Ok([]);

        public PluginResult<IReadOnlyList<InstalledPlugin>> Update(
            Func<List<InstalledPlugin>, PluginResult<IReadOnlyList<InstalledPlugin>>> mutation)
        {
            _ = mutation;
            return PluginResult<IReadOnlyList<InstalledPlugin>>.Fail(
                PluginError.RegistryLoadFailed,
                "plugin registry unavailable");
        }
    }

    private sealed class EmptyPluginFiles : IPluginFiles
    {
        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => false;

        public string ReadAllText(string path) => "";

        public void WriteAllText(string path, string contents)
        {
            _ = path;
            _ = contents;
        }

        public void CreateDirectory(string path) => _ = path;

        public bool DeleteFile(string path)
        {
            _ = path;
            return false;
        }

        public bool DeleteDirectory(string path)
        {
            _ = path;
            return false;
        }

        public string GetFullPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(".") : Path.GetFullPath(path);
    }
}
