using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SidebarSectionSplitPreferenceTests
{
    [Fact]
    public void RestoreAndPersist_KeepManualSplitAcrossReloadShape()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-split-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileClientViewPreferencesStore(
                new MapEnv(new Dictionary<string, string>
                {
                    [FileAttachConfigLoader.XdgConfigHomeVariable] = root,
                }, home: "/unused-home"));
            var live = Live(store);

            AttachSession.RestoreSidebarSectionSplit(live);
            Assert.Equal(SidebarTwoPaneLayoutPolicy.DefaultSplitRatio, live.SidebarSectionSplit);
            Assert.Equal(SidebarSectionSplitSource.Config, live.SidebarSectionSplitSource);

            live.SidebarSectionSplit = 0.8f;
            live.SidebarSectionSplitSource = SidebarSectionSplitSource.Manual;
            AttachSession.PersistSidebarSectionSplit(live);

            var reattached = Live(store);
            AttachSession.RestoreSidebarSectionSplit(reattached);
            Assert.Equal(0.8f, reattached.SidebarSectionSplit);
            Assert.Equal(SidebarSectionSplitSource.Manual, reattached.SidebarSectionSplitSource);

            var collapsed = SidebarTwoPaneLayoutPolicy.Compact(new CellRect(0, 0, 26, 20));
            Assert.True(collapsed.Spaces.Rows > 0);
            Assert.Equal(0.8f, reattached.SidebarSectionSplit);

            var resized = SidebarTwoPaneLayoutPolicy.Expanded(new CellRect(0, 0, 26, 40), reattached.SidebarSectionSplit);
            Assert.True(resized.Spaces.Rows > resized.Agents.Rows);
            Assert.Equal(0.8f, reattached.SidebarSectionSplit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AttachLiveState Live(IClientViewPreferencesStore store)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ClientViewPreferences = store,
        };
    }

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome { get; } = home;
        public string? AppData => null;
        public bool IsWindows => false;
        public bool IsMacOs => false;
    }
}
