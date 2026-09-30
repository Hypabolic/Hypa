using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class FilePluginRegistryTests
{
    [Fact]
    public void Load_corrupt_json_returns_fail_and_does_not_throw()
    {
        var root = NewRoot();
        try
        {
            var files = new MemoryPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            files.WriteAllText(paths.RegistryPath, "{not-json");
            var registry = new FilePluginRegistry(files, paths);
            var loaded = registry.Load();
            Assert.False(loaded.IsOk);
            Assert.Equal(PluginError.RegistryLoadFailed, loaded.Error.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_null_row_skips_and_does_not_throw()
    {
        var root = NewRoot();
        try
        {
            var files = new MemoryPluginFiles();
            var paths = new SystemPluginPathRoots(root);
            files.WriteAllText(paths.RegistryPath, "[null]");
            var registry = new FilePluginRegistry(files, paths);
            var loaded = registry.Load();
            Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.Message);
            Assert.Empty(loaded.Value);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task CreateSystem_and_control_plane_start_with_corrupt_registry()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "plugins.json"), "{not-json");
            var host = PluginHostFactory.CreateSystem(configRoot: root);
            var listed = host.List(null);
            Assert.True(listed.IsOk);
            Assert.Empty(listed.Value);
            host.RunStartupHooks();

            var app = new AppState(SessionId.New("plugin-reg"));
            app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
            var cp = new ControlPlaneService(
                app,
                TestPaneFactories.Stub(),
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                plugins: host);
            try
            {
                await cp.FlushPluginStartupHooksAsync(CancellationToken.None);
                var ping = await cp.DispatchAsync(ProtocolMethods.Ping, null, CancellationToken.None);
                Assert.True(ping.GetProperty("ok").GetBoolean());
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CreateEmpty_lists_nothing()
    {
        var host = PluginHostFactory.CreateEmpty();
        var listed = host.List(null);
        Assert.True(listed.IsOk);
        Assert.Empty(listed.Value);
        host.RunStartupHooks();
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "hypa-plugin-reg-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class MemoryPluginFiles : IPluginFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => _dirs.Contains(Path.GetFullPath(path));

        public string ReadAllText(string path) => _files[Path.GetFullPath(path)];

        public void WriteAllText(string path, string contents)
        {
            var full = Path.GetFullPath(path);
            _files[full] = contents;
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                _dirs.Add(parent);
        }

        public void CreateDirectory(string path) => _dirs.Add(Path.GetFullPath(path));

        public bool DeleteFile(string path) => _files.Remove(Path.GetFullPath(path));

        public bool DeleteDirectory(string path)
        {
            _ = path;
            return false;
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);
    }
}
