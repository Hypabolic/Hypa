using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxRestartTests
{
    private const string Current = """{"ok":true,"protocol":1,"version":"1.0.6","install_present":true}""";
    private const string Removed = """{"ok":true,"protocol":1,"version":"1.0.3","install_present":false}""";
    private const string Socket = "/run/hypa.sock";

    private static readonly SidebarUpdateNotice Notice = new(
        SidebarUpdateNoticeKind.RestartRequired,
        MuxRestartCommands.SidebarAction,
        MuxUpdateNoticePolicy.RestartPrompt);

    [Fact]
    public void Stale_mux_gets_the_restart_row()
    {
        var notice = MuxUpdateNoticePolicy.Evaluate(Removed, "1.0.6", clientInstallPresent: true);

        Assert.NotNull(notice);
        Assert.Equal(SidebarUpdateNoticeKind.RestartRequired, notice.Kind);
        Assert.Equal(MuxRestartCommands.SidebarAction, notice.Label);
    }

    [Fact]
    public void Current_mux_and_client_get_no_row() =>
        Assert.Null(MuxUpdateNoticePolicy.Evaluate(Current, "1.0.6", clientInstallPresent: true));

    [Fact]
    public void Upgraded_client_install_gets_the_restart_row() =>
        Assert.NotNull(MuxUpdateNoticePolicy.Evaluate(Current, "1.0.6", clientInstallPresent: false));

    [Fact]
    public void Update_section_is_hidden_without_a_notice()
    {
        var frame = SidebarSectionComposer.Compose(Input(notice: null));

        var pane = Assert.Single(frame.Panes, p => p.Id == UpdateChromeSectionStrategy.SectionId);
        Assert.False(pane.Visible);
        Assert.DoesNotContain(
            UpdateChromeSectionStrategy.SectionId,
            LayoutChromeGeometry.VisibleResourceSectionIds(frame));
    }

    [Fact]
    public void Update_section_shows_one_row_above_cubes()
    {
        var frame = SidebarSectionComposer.Compose(Input(Notice));

        var pane = Assert.Single(frame.Panes, p => p.Id == UpdateChromeSectionStrategy.SectionId);
        Assert.True(pane.Visible);
        var row = Assert.Single(pane.Rows);
        Assert.Equal(SidebarRowKind.Notice, row.Kind);
        Assert.Equal(MuxRestartCommands.SidebarAction, row.Label);
        Assert.Equal(UpdateChromeSectionStrategy.SectionId, LayoutChromeGeometry.VisibleResourceSectionIds(frame)[0]);
    }

    [Fact]
    public void Plugins_cannot_take_the_update_section_id() =>
        Assert.False(ChromeSectionRegistry.Core().TryRegister(new UpdateChromeSectionStrategy()));

    [Fact]
    public void Clicking_the_row_opens_the_restart_prompt()
    {
        var plan = ChromeHitApply.Apply(new ChromeHit(ChromeHitKind.SidebarUpdateNotice), 0, 0);

        Assert.True(plan.OpenUpdateNotice);
        Assert.Null(plan.Request);
    }

    [Fact]
    public async Task Accepted_prompt_detaches_to_restart()
    {
        var live = Live();
        live.UpdateNotice = Notice;

        await AttachSession.PromptMuxRestartAsync(live, new SilentPort(), tty: null, CancellationToken.None);
        Assert.Equal(AttachClientMode.ConfirmMoveWork, live.Engine.Mode);
        Assert.Equal(MuxUpdateNoticePolicy.RestartPrompt, live.Engine.PromptText);

        await AttachSession.ApplyMoveWorkConfirmDecisionAsync(
            live, new SilentPort(), tty: null, accept: true, CancellationToken.None);

        Assert.True(live.MuxRestartRequested);
        Assert.True(live.DetachRequested);
        Assert.False(live.PendingMuxRestart);
    }

    [Fact]
    public async Task Declined_prompt_keeps_attach_running()
    {
        var live = Live();
        live.UpdateNotice = Notice;

        await AttachSession.PromptMuxRestartAsync(live, new SilentPort(), tty: null, CancellationToken.None);
        await AttachSession.ApplyMoveWorkConfirmDecisionAsync(
            live, new SilentPort(), tty: null, accept: false, CancellationToken.None);

        Assert.False(live.MuxRestartRequested);
        Assert.False(live.DetachRequested);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
    }

    [Fact]
    public void Heartbeat_shows_the_row_for_the_local_mux()
    {
        var live = Live();
        live.UpdateNotices = new FixedNotices(Notice);

        AttachSession.RefreshUpdateNotice(live, tty: null, Pong());

        Assert.Equal(Notice, live.UpdateNotice);
    }

    [Fact]
    public void Heartbeat_hides_the_row_while_a_cube_is_on_screen()
    {
        var live = Live();
        live.UpdateNotices = new FixedNotices(Notice);
        live.UpdateNotice = Notice;
        live.ActiveMuxSocketPath = "/run/cube.sock";

        AttachSession.RefreshUpdateNotice(live, tty: null, Pong());

        Assert.Null(live.UpdateNotice);
    }

    [Fact]
    public async Task Confirmed_restart_stops_the_mux_and_attaches_again()
    {
        var supervisor = new CountingSupervisor();
        var driver = new RestartOnceDriver();
        var stops = new List<MuxRestartRequest>();
        var attach = new MuxAttachService(supervisor, driver)
        {
            RestartSteps = new MuxRestartSteps(
                restart =>
                {
                    stops.Add(restart);
                    return Task.FromResult(true);
                },
                _ => false),
        };

        var exit = await attach.AttachAsync("work", null, false, null, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal([new MuxRestartRequest("work", Socket)], stops);
        Assert.Equal(2, supervisor.Calls);
        Assert.Equal(2, driver.Runs);
    }

    [Fact]
    public async Task Restart_execs_the_installed_hypa_when_it_can()
    {
        var supervisor = new CountingSupervisor();
        var driver = new RestartOnceDriver();
        var execs = new List<string>();
        var attach = new MuxAttachService(supervisor, driver)
        {
            RestartSteps = new MuxRestartSteps(
                _ => Task.FromResult(true),
                session =>
                {
                    execs.Add(session);
                    return true;
                }),
        };

        Assert.Equal(0, await attach.AttachAsync("work", null, false, null, CancellationToken.None));
        Assert.Equal(["work"], execs);
        Assert.Equal(1, driver.Runs);
    }

    [Fact]
    public async Task Failed_stop_ends_attach()
    {
        var driver = new RestartOnceDriver();
        var attach = new MuxAttachService(new CountingSupervisor(), driver)
        {
            RestartSteps = new MuxRestartSteps(_ => Task.FromResult(false), _ => true),
        };

        Assert.Equal(1, await attach.AttachAsync("work", null, false, null, CancellationToken.None));
        Assert.Equal(1, driver.Runs);
    }

    [Theory]
    [InlineData("/run/hypa.sock", "/run/hypa.sock", true)]
    [InlineData("/run/hypa.sock", "/run/other.sock", false)]
    [InlineData("/run/hypa.sock", null, false)]
    public void Restart_refuses_from_inside_its_own_mux(string socket, string? env, bool inside) =>
        Assert.Equal(inside, MuxRestarter.RunsInside(socket, env));

    [Fact]
    public void Relaunch_skips_test_hosts_and_source_builds()
    {
        using var dir = new TempDir();
        var apphost = dir.Touch("hypa");
        dir.Touch("hypa.dll");

        Assert.Null(MuxRestarter.ResolveRelaunchBinary("/usr/bin/testhost", dir.Path));
        Assert.Null(MuxRestarter.ResolveRelaunchBinary(apphost, dir.Path));
    }

    [Fact]
    public void Relaunch_prefers_the_hypa_beside_this_one()
    {
        using var install = new TempDir();
        using var path = new TempDir();
        var attach = install.Touch("hypa-attach");
        var sibling = install.Touch("hypa");
        path.Touch("hypa");

        Assert.Equal(sibling, MuxRestarter.ResolveRelaunchBinary(attach, path.Path));
    }

    [Fact]
    public void Relaunch_falls_back_to_path_when_the_install_was_removed()
    {
        using var path = new TempDir();
        var onPath = path.Touch("hypa");

        Assert.Equal(
            onPath,
            MuxRestarter.ResolveRelaunchBinary("/gone/1.0.5/hypa-attach (deleted)", path.Path));
    }

    private static JsonElement Pong() => JsonDocument.Parse(Removed).RootElement.Clone();

    private static SidebarComposeInput Input(SidebarUpdateNotice? notice) =>
        new()
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            UpdateNotice = notice,
        };

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "ws-1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "ws-1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-i",
            ChromeEnabled = true,
            Chrome = MouseTestGeom.Split(),
            SourceMuxSocketPath = Socket,
        };
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(default(JsonElement));
    }

    private sealed class FixedNotices(SidebarUpdateNotice? notice) : IMuxUpdateNoticeSource
    {
        public SidebarUpdateNotice? Evaluate(string? pingJson) => notice;
    }

    private sealed class CountingSupervisor : IMuxSupervisor
    {
        public int Calls { get; private set; }

        public Task<MuxReadyInfo> EnsureReadyAsync(
            string session,
            string? cwd,
            string? socketOverride,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MuxReadyInfo(session, Socket, Current));
        }
    }

    /// <summary>Asks for one restart on its first run, then detaches normally.</summary>
    private sealed class RestartOnceDriver : IMuxAttachDriver, IMuxRestartSource
    {
        private MuxRestartRequest? _pending;

        public int Runs { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
        {
            Runs++;
            if (Runs == 1)
                _pending = new MuxRestartRequest(ready.Session, ready.SocketPath);
            return Task.FromResult(0);
        }

        public MuxRestartRequest? TakeRestartRequest()
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("hypa-restart-").FullName;
        }

        public string Path { get; }

        public string Touch(string name)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, "");
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
