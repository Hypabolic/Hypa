using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ClientViewPreferencesStoreTests
{
    [Fact]
    public void ResolvePath_UsesHypaConfigSiblingThenXdg()
    {
        var explicitStore = new FileClientViewPreferencesStore(
            new MapEnv(new Dictionary<string, string>
            {
                [FileAttachConfigLoader.ConfigPathVariable] = "/explicit/config.toml",
                [FileAttachConfigLoader.XdgConfigHomeVariable] = "/xdg",
            }, home: "/home/me"));
        Assert.Equal(
            FileAttachConfigLoader.CanonicalizePath("/explicit/client-view.json"),
            explicitStore.ResolvePath());

        var xdgStore = new FileClientViewPreferencesStore(
            new MapEnv(new Dictionary<string, string>
            {
                [FileAttachConfigLoader.XdgConfigHomeVariable] = "/xdg",
            }, home: "/home/me"));
        Assert.Equal(
            FileAttachConfigLoader.CanonicalizePath("/xdg/hypa/client-view.json"),
            xdgStore.ResolvePath());
    }

    [Fact]
    public void TempDirectory_RoundTripsManualSplitAndIgnoresMalformedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-client-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileClientViewPreferencesStore(
                new MapEnv(new Dictionary<string, string>
                {
                    [FileAttachConfigLoader.XdgConfigHomeVariable] = root,
                }, home: "/unused-home"));

            Assert.Null(store.Load().SidebarSectionSplit);
            Assert.True(store.Save(new ClientViewPreferences { SidebarSectionSplit = 0.8f }));
            Assert.Equal(0.8f, store.Load().SidebarSectionSplit);
            Assert.StartsWith(root, store.ResolvePath(), StringComparison.Ordinal);
            Assert.DoesNotContain("/unused-home", store.ResolvePath(), StringComparison.Ordinal);

            File.WriteAllText(store.ResolvePath(), "{ not-json");
            Assert.Null(store.Load().SidebarSectionSplit);

            Assert.True(store.Save(new ClientViewPreferences { SidebarSectionSplit = 1.4f }));
            Assert.Equal(0.9f, store.Load().SidebarSectionSplit);

            Assert.True(store.Save(new ClientViewPreferences
            {
                SidebarSectionSplit = 0.8f,
                CollapsedGroups = ["repo-b", "repo-a", "repo-a", ""],
            }));
            var loaded = store.Load();
            Assert.Equal(0.8f, loaded.SidebarSectionSplit);
            Assert.Equal(["repo-a", "repo-b"], loaded.CollapsedGroups ?? []);

            Assert.True(store.Save(new ClientViewPreferences { CollapsedGroups = ["repo-a"] }));
            var groupsOnly = store.Load();
            Assert.Null(groupsOnly.SidebarSectionSplit);
            Assert.Equal(["repo-a"], groupsOnly.CollapsedGroups ?? []);

            Assert.True(store.Save(new ClientViewPreferences { LastPlacementId = " plc_docker " }));
            var lastOnly = store.Load();
            Assert.Null(lastOnly.SidebarSectionSplit);
            Assert.Null(lastOnly.CollapsedGroups);
            Assert.Equal("plc_docker", lastOnly.LastPlacementId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home,
        string? appData = null,
        bool isWindows = false) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome { get; } = home;
        public string? AppData { get; } = appData;
        public bool IsWindows { get; } = isWindows;
        public bool IsMacOs => false;
    }
}
