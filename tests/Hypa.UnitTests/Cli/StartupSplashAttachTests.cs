using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Splash;
using HypaCube;
using Ttfx;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class StartupSplashAttachTests
{
    private static AttachLiveState Live(bool splashEnabled)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            Ui = new AttachUiConfig { StartupSplash = splashEnabled },
        };
    }

    [Fact]
    public void Skip_dismisses_at_once()
    {
        var session = new StartupSplashSession();
        session.Skip();
        Assert.True(session.ShouldDismiss(TimeSpan.Zero, overlayFinished: false));
    }

    [Fact]
    public void Mux_not_ready_keeps_splash_before_failsafe()
    {
        var session = new StartupSplashSession();
        Assert.False(session.ShouldDismiss(TimeSpan.FromSeconds(2), overlayFinished: true));
        Assert.True(session.ShouldDismiss(StartupSplashSession.Failsafe, overlayFinished: false));
    }

    [Fact]
    public void Mux_ready_waits_until_overlay_finishes()
    {
        var session = new StartupSplashSession();
        session.NoteMuxReady();
        Assert.False(session.ShouldDismiss(TimeSpan.FromMilliseconds(500), overlayFinished: true));
        Assert.True(session.ShouldDismiss(StartupSplashSession.MinVisible, overlayFinished: true));
        Assert.False(session.ShouldDismiss(StartupSplashSession.MinVisible, overlayFinished: false));
        Assert.False(session.ShouldDismiss(TimeSpan.FromSeconds(8), overlayFinished: false));
        Assert.True(session.ShouldDismiss(StartupSplashSession.Failsafe, overlayFinished: false));
    }

    [Fact]
    public void Missing_overlay_is_not_finished()
    {
        var composer = new CubeSplashComposer();
        Assert.False(composer.OverlayFinished);
        composer.AttachOverlay(null);
        Assert.True(composer.OverlayFinished);
    }

    [Fact]
    public void Tick_fills_non_blank_cube_cells()
    {
        var composer = new CubeSplashComposer();
        composer.Resize(80, 24);
        for (var i = 0; i < 8; i++)
            composer.Tick(1f / 60f);

        var cells = composer.Engine.Cells;
        Assert.Equal(80, cells.Cols);
        Assert.Equal(24, cells.Rows);
        var painted = 0;
        foreach (var ch in cells.Ch)
        {
            if (ch != 0 && ch != 32)
                painted++;
        }

        Assert.True(painted > 20, $"painted={painted}");
        Assert.Equal("phosphor", composer.Engine.Options.Theme.Id);
        Assert.False(composer.Engine.Options.Hud);
        Assert.False(composer.Engine.Options.Faces);
    }

    [Fact]
    public void Overlay_player_parses_short_text()
    {
        var player = TtfxOverlayPlayer.TryCreate(
            "wipe",
            "HYPA\n",
            seed: 42,
            loop: false,
            holdFinal: true,
            OverlayAnchor.Center,
            out var error);
        Assert.True(player is not null, error ?? "null player");
        Assert.True(player!.FrameCount > 0);

        var engine = new CubeEngine();
        engine.Options.Hud = false;
        engine.Resize(80, 24);
        engine.Tick(1f / 60f);
        for (var i = 0; i < player.FrameCount + 2; i++)
            player.Apply(engine, 1.0 / 60.0);

        Assert.True(player.Finished);
        var masked = 0;
        foreach (var m in engine.Overlay.Mask)
        {
            if (m != 0)
                masked++;
        }

        Assert.True(masked > 0, "overlay mask empty");
    }

    [Fact]
    public void Pick_effect_uses_ttfx_catalog()
    {
        var first = CubeSplashComposer.PickEffect(new Random(1));
        var second = CubeSplashComposer.PickEffect(new Random(1));
        Assert.Equal(first, second);
        Assert.True(TextEffects.Exists(first));
    }

    [Fact]
    public void Pick_effect_varies_across_seeds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 40; seed++)
            seen.Add(CubeSplashComposer.PickEffect(new Random(seed)));
        Assert.True(seen.Count > 1, $"seen={string.Join(",", seen)}");
    }

    [Fact]
    public void Painter_stamps_rgb_cells_into_host_frame()
    {
        var composer = new CubeSplashComposer();
        composer.Resize(80, 24);
        for (var i = 0; i < 8; i++)
            composer.Tick(1f / 60f);

        var host = new HostFrame();
        host.Resize(80, 24);
        var sink = new HostFrameCellSink(host);
        StartupSplashPainter.Stamp(sink, composer, 80, 24);

        var painted = 0;
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 80; col++)
            {
                var cell = host.CellAt(col, row);
                if (cell.Style.Fg != 0 || cell.Style.Bg != 0)
                    painted++;
            }
        }

        Assert.True(painted > 20, $"painted={painted}");
        Assert.Equal(HostCursor.None, host.Cursor);
    }

    [Fact]
    public void Resize_matches_host_size_not_a_corner_clamp()
    {
        var composer = new CubeSplashComposer();
        composer.Resize(160, 50);
        for (var i = 0; i < 8; i++)
            composer.Tick(1f / 60f);

        var cells = composer.Engine.Cells;
        Assert.Equal(160, cells.Cols);
        Assert.Equal(50, cells.Rows);

        var host = new HostFrame();
        host.Resize(160, 50);
        StartupSplashPainter.Stamp(new HostFrameCellSink(host), composer, 160, 50);

        var far = 0;
        for (var row = 42; row < 50; row++)
        {
            for (var col = 140; col < 160; col++)
            {
                var cell = host.CellAt(col, row);
                if (cell.Style.Bg != 0)
                    far++;
            }
        }

        Assert.True(far > 0, $"far={far}");
    }

    [Fact]
    public void Logo_resource_loads()
    {
        var text = CubeSplashComposer.LoadLogoText();
        Assert.Contains("████", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_config_does_not_start_splash()
    {
        var live = Live(splashEnabled: false);
        AttachSession.TryStartStartupSplash(live, loadLogoOverlay: false);
        Assert.Null(live.Splash);
    }

    [Fact]
    public void Enabled_config_starts_playing_splash()
    {
        var live = Live(splashEnabled: true);
        AttachSession.TryStartStartupSplash(live, loadLogoOverlay: false);
        Assert.NotNull(live.Splash);
        Assert.True(live.Splash!.IsPlaying);
    }

    [Fact]
    public void Key_or_click_skips_splash()
    {
        var live = Live(splashEnabled: true);
        AttachSession.TryStartStartupSplash(live, loadLogoOverlay: false);
        Assert.False(AttachSession.TryConsumeStartupSplashInput(live, [], []));
        Assert.NotNull(live.Splash);

        Assert.False(AttachSession.TryConsumeStartupSplashInput(
            live,
            [],
            [new MouseEvent(MouseButton.None, MouseAction.Move, 1, 1)]));
        Assert.NotNull(live.Splash);

        Assert.True(AttachSession.TryConsumeStartupSplashInput(live, [(byte)'x'], []));
        Assert.Null(live.Splash);

        AttachSession.TryStartStartupSplash(live, loadLogoOverlay: false);
        Assert.True(AttachSession.TryConsumeStartupSplashInput(
            live,
            [],
            [new MouseEvent(MouseButton.Left, MouseAction.Press, 2, 2)]));
        Assert.Null(live.Splash);
    }

    [Fact]
    public void Splash_skip_is_a_single_consume()
    {
        var live = Live(splashEnabled: true);
        AttachSession.TryStartStartupSplash(live, loadLogoOverlay: false);
        Assert.True(AttachSession.TryConsumeStartupSplashInput(live, [(byte)0x1b, (byte)'[', (byte)'I'], []));
        Assert.Null(live.Splash);
        Assert.False(AttachSession.TryConsumeStartupSplashInput(live, [(byte)'q'], []));
    }

    [Fact]
    public void Replay_does_not_start_splash()
    {
        using var replay = new M6AttachReplay("mouse-only");
        Assert.Null(replay.Live.Splash);
    }

    [Fact]
    public void Idle_poll_is_faster_while_splash_animates()
    {
        Assert.Equal(50, AttachSession.StdinPollTimeoutMs([]));
        Assert.Equal(16, AttachSession.StdinPollTimeoutMs([], animateIdle: true));
    }
}
