using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Status;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Plugins;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ChromeSlotGoldenTests
{
    [Theory]
    [InlineData("mouse-slots", M6AttachReplay.InputMouse)]
    [InlineData("prefix-slots", M6AttachReplay.InputPrefix)]
    public async Task Replays_slots_suite_and_detaches(string suite, string inputKind)
    {
        using var replay = new M6AttachReplay(suite);
        var meta = M6AttachReplay.LoadMeta(suite);
        var expected = M6AttachReplay.LoadExpected(suite);
        var script = M6AttachReplay.LoadScript(suite);
        Assert.Equal(inputKind, meta.InputKind);
        Assert.Equal(M6AttachReplay.CaptureKind, meta.CaptureKind);
        replay.OnCheckpoint = name =>
        {
            AssertLiveSlot(name, replay, inputKind);
            return Task.CompletedTask;
        };
        await replay.ReplayAsync(script);
        Assert.Equal(expected.Checkpoints, replay.Checkpoints);
        Assert.True(replay.Live.DetachRequested);
        Assert.Equal(expected.DetachSource, replay.DetachSource);

        foreach (var method in expected.ForbiddenRpcMethods)
            Assert.DoesNotContain(method, replay.RpcMethods);
        Assert.DoesNotContain("server.stop", replay.RpcMethods);
    }

    private static void AssertLiveSlot(string name, M6AttachReplay replay, string inputKind)
    {
        var plugins = replay.Live.LinkedPlugins;
        switch (name)
        {
            case "attach":
                Assert.Contains("slots", replay.Live.LinkedPluginIds!);
                Assert.Contains(plugins, p => p.PluginId == "slots" && p.Enabled);
                break;
            case "sidebar":
                Assert.True(replay.Live.SidebarOpen);
                Assert.Contains(replay.Live.SidebarFrame?.Panes ?? [], p => p.Id == "slots");
                break;
            case "chip":
                var chips = StatusResourceChipComposer.Compose(
                    replay.Ui.TabBarRight,
                    AttachSessionPluginResources(replay));
                var chip = Assert.Single(chips);
                Assert.Equal(StatusChipKind.Resource, chip.Kind);
                Assert.Equal("2 waiting", chip.Text);
                break;
            case "command":
                Assert.Contains(
                    PluginChromeCatalog.PaletteTargets(plugins),
                    row => row.Label == "Open slot item");
                if (string.Equals(inputKind, M6AttachReplay.InputPrefix, StringComparison.Ordinal))
                {
                    Assert.Equal(AttachClientMode.Navigator, replay.Engine.Mode);
                    Assert.Contains(
                        replay.Engine.Navigator.Visible,
                        row => row.Target.Label == "Open slot item");
                }

                break;
            case "menu":
                Assert.Contains(GlobalMenuModel.Items(plugins), item => item.Label == "Reload slots");
                if (string.Equals(inputKind, M6AttachReplay.InputMouse, StringComparison.Ordinal))
                {
                    Assert.Equal(AttachClientMode.GlobalMenu, replay.Engine.Mode);
                    var menu = replay.Live.Mouse.Menu ?? replay.Live.MouseMenu;
                    Assert.NotNull(menu);
                    Assert.Contains(menu.Items, item => item.Label == "Reload slots");
                }

                break;
            case "settings":
                Assert.Equal(AttachClientMode.Settings, replay.Engine.Mode);
                Assert.Contains(replay.Engine.Settings.Pages, p => p.Id == "plugin:slots");
                break;
            case "pane":
                var plugin = Assert.Single(plugins, p => p.PluginId == "slots");
                Assert.Contains(plugin.Panes, p => p.Id == "board");
                break;
            case "toast":
                Assert.NotEmpty(replay.Live.Toasts);
                break;
            case "detach":
                Assert.True(replay.Live.DetachRequested);
                break;
            default:
                throw new InvalidOperationException("unknown slot checkpoint " + name);
        }
    }

    private static IReadOnlyList<Hypa.AgentRuntime.Protocol.Models.PluginResourceDto> AttachSessionPluginResources(
        M6AttachReplay replay) =>
        AttachSession.PluginResourcesOf(replay.Live);
}
