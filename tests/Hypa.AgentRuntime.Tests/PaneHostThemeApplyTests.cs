using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PaneHostThemeApplyTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public PaneHostThemeApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-pane-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Create_applies_theme_before_start()
    {
        var factory = new HostThemeRpcTests.ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            await SetThemeAsync(cp);
            var ws = await CreateWorkspaceAsync(cp);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var runtime = factory.Created.Single(r => r.Id.Value == paneId);
            Assert.Equal(["apply", "start"], runtime.Order);
            Assert.Equal(new HostRgb(10, 20, 30), runtime.LastTheme.Background);
            Assert.Equal(new HostRgb(10, 20, 30), runtime.Options.HostTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hidden_create_applies_theme_before_start()
    {
        var factory = new HostThemeRpcTests.ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            await SetThemeAsync(cp);
            var ws = await CreateWorkspaceAsync(cp);
            var workspaceId = ws.GetProperty("workspace_id").GetString()!;
            var hidden = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var hiddenId = hidden.GetProperty("pane_id").GetString()!;
            var runtime = factory.Created.Single(r => r.Id.Value == hiddenId);
            Assert.Equal(["apply", "start"], runtime.Order);
            Assert.Equal(new HostRgb(10, 20, 30), runtime.Options.HostTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Replacement_applies_theme_before_start()
    {
        var factory = new HostThemeRpcTests.ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            await SetThemeAsync(cp);
            var ws = await CreateWorkspaceAsync(cp);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            _ = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["command"] = "/bin/true",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var replacement = factory.Created.Last(r => r.Id.Value == paneId);
            Assert.Equal(["apply", "start"], replacement.Order);
            Assert.Equal(new HostRgb(10, 20, 30), replacement.Options.HostTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Popup_applies_theme_before_start()
    {
        var factory = new HostThemeRpcTests.ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            await SetThemeAsync(cp);
            _ = await CreateWorkspaceAsync(cp);
            _ = await cp.DispatchAsync(
                ProtocolMethods.PopupOpen,
                JsonSerializer.SerializeToElement(
                    new PopupOpenParams
                    {
                        Command = "/bin/echo",
                        AreaCols = 80,
                        AreaRows = 24,
                    },
                    ProtocolJsonContext.Default.PopupOpenParams),
                CancellationToken.None);
            var popup = factory.Created.Single(r => r.Id.Value == ControlPlaneService.PopupRuntimeKey);
            Assert.Equal(["apply", "start"], popup.Order);
            Assert.Equal(new HostRgb(10, 20, 30), popup.Options.HostTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Create_replacement_and_popup_receive_palette_before_start()
    {
        var factory = new HostThemeRpcTests.ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            _ = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse(
                    """{"bg":{"r":10,"g":20,"b":30},"palette":[{"i":0,"r":1,"g":2,"b":3},{"i":255,"r":4,"g":5,"b":6}]}""").RootElement,
                CancellationToken.None);
            var ws = await CreateWorkspaceAsync(cp);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var created = factory.Created.Single(r => r.Id.Value == paneId);
            Assert.Equal(["apply", "start"], created.Order);
            Assert.Equal(new HostRgb(1, 2, 3), created.Options.HostTheme.Palette[0]);
            Assert.Equal(new HostRgb(4, 5, 6), created.Options.HostTheme.Palette[255]);

            _ = await cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["command"] = "/bin/true",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var replacement = factory.Created.Last(r => r.Id.Value == paneId);
            Assert.Equal(["apply", "start"], replacement.Order);
            Assert.Equal(new HostRgb(1, 2, 3), replacement.Options.HostTheme.Palette[0]);

            _ = await cp.DispatchAsync(
                ProtocolMethods.PopupOpen,
                JsonSerializer.SerializeToElement(
                    new PopupOpenParams
                    {
                        Command = "/bin/echo",
                        AreaCols = 80,
                        AreaRows = 24,
                    },
                    ProtocolJsonContext.Default.PopupOpenParams),
                CancellationToken.None);
            var popup = factory.Created.Single(r => r.Id.Value == ControlPlaneService.PopupRuntimeKey);
            Assert.Equal(["apply", "start"], popup.Order);
            Assert.Equal(new HostRgb(1, 2, 3), popup.Options.HostTheme.Palette[0]);
            Assert.Equal(new HostRgb(4, 5, 6), popup.Options.HostTheme.Palette[255]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Child_query_gets_host_background_unless_owned()
    {
        await using var runtime = NewRuntime();
        var host = new HostTerminalTheme(new HostRgb(1, 2, 3), new HostRgb(10, 20, 30), HostAppearance.Dark);
        runtime.ApplyHostTerminalTheme(host);
        var query = Encoding.ASCII.GetBytes("\u001b]11;?\u001b\\");
        var replies = runtime.HandleChildBytesForTests(query);
        Assert.Single(replies);
        Assert.Equal(
            HostThemeParser.OscRgbResponse(HostDefaultColorKind.Background, new HostRgb(10, 20, 30)),
            replies[0]);
    }

    [Fact]
    public async Task Owned_query_does_not_return_host_background()
    {
        await using var runtime = NewRuntime();
        var hostRgb = new HostRgb(10, 20, 30);
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, hostRgb, null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        var replies = runtime.HandleChildBytesForTests("\u001b]11;?\u001b\\"u8);
        var hostReply = HostThemeParser.OscRgbResponse(HostDefaultColorKind.Background, hostRgb);
        Assert.DoesNotContain(replies, r => r.AsSpan().SequenceEqual(hostReply));
    }

    [Fact]
    public async Task Owned_channel_is_not_overwritten_by_later_host_apply()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_own"), Cwd = "/", Command = "/bin/true" },
            vt);
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(10, 20, 30), null));
        var set = Encoding.ASCII.GetBytes("\u001b]11;rgb:aa/bb/cc\u001b\\");
        _ = runtime.HandleChildBytesForTests(set);
        Assert.True(runtime.ChildBackgroundOwned);
        var before = vt.Feeds.Count;
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(1, 2, 3), null));
        Assert.True(runtime.ChildBackgroundOwned);
        var afterHost = vt.Feeds.Skip(before).Select(f => Encoding.ASCII.GetString(f)).ToArray();
        Assert.DoesNotContain("]11;rgb:01/02/03", string.Join('\n', afterHost), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multi_value_osc_10_owns_background_across_host_apply()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_osc10_pair"), Cwd = "/", Command = "/bin/true" },
            vt);
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(
            new HostRgb(1, 2, 3),
            new HostRgb(10, 20, 30),
            null));
        _ = runtime.HandleChildBytesForTests("\u001b]10;rgb:11/22/33;rgb:44/55/66\u001b\\"u8);
        Assert.True(runtime.ChildForegroundOwned);
        Assert.True(runtime.ChildBackgroundOwned);
        var before = vt.Feeds.Count;
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(
            new HostRgb(9, 9, 9),
            new HostRgb(1, 2, 3),
            null));
        Assert.True(runtime.ChildForegroundOwned);
        Assert.True(runtime.ChildBackgroundOwned);
        var afterHost = string.Join('\n', vt.Feeds.Skip(before).Select(f => Encoding.ASCII.GetString(f)));
        Assert.DoesNotContain("]10;rgb:09/09/09", afterHost, StringComparison.Ordinal);
        Assert.DoesNotContain("]11;rgb:01/02/03", afterHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_value_osc_10_marks_each_owned_channel()
    {
        var tracker = new DefaultColorOscTracker();
        tracker.Observe(
            "\u001b]10;rgb:11/22/33;rgb:44/55/66\u001b\\\u001b]10;?;rgb:77/88/99\u001b\\\u001b]10;;rgb:aa/bb/cc\u001b\\"u8);
        var events = tracker.DrainPending();
        Assert.Equal(4, events.Count);
        Assert.Equal(HostDefaultColorKind.Foreground, events[0].Channel);
        Assert.Equal(new HostRgb(0x11, 0x22, 0x33), events[0].Color);
        Assert.Equal(HostDefaultColorKind.Background, events[1].Channel);
        Assert.Equal(new HostRgb(0x44, 0x55, 0x66), events[1].Color);
        Assert.Equal(HostDefaultColorKind.Background, events[2].Channel);
        Assert.Equal(new HostRgb(0x77, 0x88, 0x99), events[2].Color);
        Assert.Equal(HostDefaultColorKind.Foreground, events[3].Channel);
        Assert.Equal(new HostRgb(0xaa, 0xbb, 0xcc), events[3].Color);
        Assert.All(events, ev => Assert.Equal(DefaultColorOscKind.Set, ev.Kind));
    }

    [Fact]
    public void Osc_12_set_is_not_tracked()
    {
        var tracker = new DefaultColorOscTracker();
        tracker.Observe("\u001b]12;rgb:11/22/33\u0007"u8);
        Assert.Empty(tracker.DrainPending());
    }

    [Fact]
    public async Task Reset_unmarks_and_refeeds_host()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_reset"), Cwd = "/", Command = "/bin/true" },
            vt);
        var hostSeq = HostThemeParser.OscSetDefaultColorSequence(
            HostDefaultColorKind.Background,
            new HostRgb(10, 20, 30));
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(10, 20, 30), null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        _ = runtime.HandleChildBytesForTests("\u001b]111\u001b\\after"u8);
        Assert.False(runtime.ChildBackgroundOwned);
        var feeds = vt.Feeds.Select(f => Encoding.ASCII.GetString(f)).ToArray();
        Assert.Equal("after", feeds[^1]);
        Assert.Equal(hostSeq, feeds[^2]);
        Assert.Contains("]111", feeds[^3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_versioned_apply_does_not_overwrite_newer_theme()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_stale"), Cwd = "/", Command = "/bin/true" },
            vt);
        var older = new HostTerminalTheme(null, new HostRgb(1, 2, 3), null);
        var newer = new HostTerminalTheme(null, new HostRgb(9, 8, 7), null);
        runtime.ApplyHostTerminalTheme(older, version: 1);
        runtime.ApplyHostTerminalTheme(newer, version: 2);
        var before = vt.Feeds.Count;
        runtime.ApplyHostTerminalTheme(older, version: 1);
        Assert.Equal(newer, runtime.HostThemeSnapshot);
        Assert.Equal(2, runtime.AppliedHostThemeVersion);
        Assert.Equal(before, vt.Feeds.Count);
    }

    [Fact]
    public async Task Restore_then_stale_apply_keeps_newer_host_theme()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_restore_stale"), Cwd = "/", Command = "/bin/true" },
            vt)
        {
            TestShellPid = 100,
            TestForegroundGroup = 200,
        };
        var older = new HostTerminalTheme(null, new HostRgb(1, 2, 3), null);
        var newer = new HostTerminalTheme(null, new HostRgb(9, 8, 7), null);
        runtime.ApplyHostTerminalTheme(older, version: 1);
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        runtime.ApplyHostTerminalTheme(newer, version: 2);
        runtime.TestForegroundGroup = 100;
        runtime.RestoreHostThemeForTests(100);
        Assert.False(runtime.ChildBackgroundOwned);
        Assert.Equal(newer, runtime.HostThemeSnapshot);
        Assert.Contains(
            HostThemeParser.OscSetDefaultColorSequence(
                HostDefaultColorKind.Background,
                new HostRgb(9, 8, 7)),
            vt.Feeds.Select(f => Encoding.ASCII.GetString(f)));
        var before = vt.Feeds.Count;
        runtime.ApplyHostTerminalTheme(older, version: 1);
        Assert.Equal(newer, runtime.HostThemeSnapshot);
        Assert.Equal(before, vt.Feeds.Count);
        Assert.DoesNotContain(
            HostThemeParser.OscSetDefaultColorSequence(
                HostDefaultColorKind.Background,
                new HostRgb(1, 2, 3)),
            vt.Feeds.Skip(before).Select(f => Encoding.ASCII.GetString(f)));
    }

    [Fact]
    public async Task Restore_when_shell_is_foreground()
    {
        var vt = new StubVtEngine(20, 6);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_restore"), Cwd = "/", Command = "/bin/true" },
            vt)
        {
            TestShellPid = 100,
            TestForegroundGroup = 200,
        };
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(10, 20, 30), null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        runtime.TestForegroundGroup = 100;
        runtime.RestoreHostThemeForTests(100);
        Assert.False(runtime.ChildBackgroundOwned);
        Assert.Contains(
            HostThemeParser.OscSetDefaultColorSequence(
                HostDefaultColorKind.Background,
                new HostRgb(10, 20, 30)),
            vt.Feeds.Select(f => Encoding.ASCII.GetString(f)));
    }

    [Fact]
    public async Task Restore_when_probe_reports_shell_is_foreground()
    {
        var vt = new StubVtEngine(20, 6);
        var probe = new MutableForegroundProbe { Group = 200 };
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_probe"), Cwd = "/", Command = "/bin/true" },
            vt)
        {
            TestShellPid = 100,
            ProcessInfoProbe = probe,
        };
        var hostSeq = HostThemeParser.OscSetDefaultColorSequence(
            HostDefaultColorKind.Background,
            new HostRgb(10, 20, 30));
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(10, 20, 30), null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        probe.Group = 100;
        _ = runtime.HandleChildBytesForTests("later"u8);
        Assert.False(runtime.ChildBackgroundOwned);
        var feeds = vt.Feeds.Select(f => Encoding.ASCII.GetString(f)).ToArray();
        Assert.Equal(hostSeq, feeds[^1]);
        Assert.Contains("later", feeds);
    }

    [Fact]
    public async Task Restore_skips_alternate_screen()
    {
        var vt = new StubVtEngine(20, 6) { IsAlternateScreen = true };
        var probe = new MutableForegroundProbe { Group = 200 };
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_alt"), Cwd = "/", Command = "/bin/true" },
            vt)
        {
            TestShellPid = 100,
            ProcessInfoProbe = probe,
        };
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, new HostRgb(10, 20, 30), null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        probe.Group = 100;
        _ = runtime.HandleChildBytesForTests("still-alt"u8);
        Assert.True(runtime.ChildBackgroundOwned);
        vt.IsAlternateScreen = false;
        _ = runtime.HandleChildBytesForTests("main"u8);
        Assert.False(runtime.ChildBackgroundOwned);
    }

    [Fact]
    public async Task Factory_assigns_process_info_probe()
    {
        var probe = new MutableForegroundProbe { Group = 7 };
        var factory = new PaneRuntimeFactory(
            vtEngineFactory: new StubVtEngineFactory(),
            processInfoProbe: probe);
        await using var runtime = (PaneRuntime)factory.Create(new PaneSpawnOptions
        {
            Id = new PaneId("p_factory"),
            Cwd = "/",
            Command = "/bin/true",
        });
        Assert.Same(probe, runtime.ProcessInfoProbe);
    }

    [SkippableFact]
    public async Task Reset_on_ghostty_leaves_host_blank_cells()
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        GhosttyTestRequire.RequireNativeLibrary(lib);
        using var vt = new Hypa.Terminal.Vt.Ghostty.GhosttyVtEngine(12, 4, libraryPathOverride: lib);
        await using var runtime = new PaneRuntime(
            new PaneSpawnOptions { Id = new PaneId("p_gty"), Cwd = "/", Command = "/bin/true" },
            vt);
        var host = new HostRgb(0x12, 0x34, 0x56);
        runtime.ApplyHostTerminalTheme(new HostTerminalTheme(null, host, null));
        _ = runtime.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        _ = runtime.HandleChildBytesForTests("\u001b]111\u001b\\"u8);
        Assert.False(runtime.ChildBackgroundOwned);
        var paint = vt.CapturePaintSnapshot();
        Assert.True(string.IsNullOrEmpty(paint.Cells[0][0].Style.Bg));
    }

    private static PaneRuntime NewRuntime() =>
        new(
            new PaneSpawnOptions { Id = new PaneId("p_theme"), Cwd = "/", Command = "/bin/true" },
            new StubVtEngine(20, 6));

    private static Task<JsonElement> SetThemeAsync(ControlPlaneService cp) =>
        cp.DispatchAsync(
            ProtocolMethods.ClientHostThemeSet,
            JsonDocument.Parse("""{"bg":{"r":10,"g":20,"b":30},"appearance":"dark"}""").RootElement,
            CancellationToken.None);

    private static Task<JsonElement> CreateWorkspaceAsync(ControlPlaneService cp) =>
        cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/echo",
            }.ToJsonString()).RootElement,
            CancellationToken.None);

    private async Task<(ControlPlaneService Cp, EventSubscriptionHub Hub)> NewPlaneAsync(
        IPaneRuntimeFactory factory)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-theme"));
        state.UpdateSession(s => s with { Name = "pane-theme", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        return (cp, hub);
    }

    private sealed class MutableForegroundProbe : IPaneProcessInfoProbe
    {
        public int? Group { get; set; }

        public int? TryGetForegroundGroup(int shellPid)
        {
            _ = shellPid;
            return Group;
        }

        public PaneForegroundInfo? TryGetForegroundInfo(int shellPid)
        {
            var group = TryGetForegroundGroup(shellPid);
            if (group is null)
                return null;
            return new PaneForegroundInfo { GroupId = group.Value };
        }
    }
}
