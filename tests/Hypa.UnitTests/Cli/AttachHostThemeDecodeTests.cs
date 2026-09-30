using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachHostThemeDecodeTests
{
    [Fact]
    public void Decode_swallows_osc_11_and_does_not_emit_keys()
    {
        var csi = new List<byte>();
        var keys = AttachSession.DecodeInput(
            "\u001b]11;rgb:ffff/ffff/ffff\u0007ab"u8,
            csi,
            out bool focusIn,
            out List<MouseEvent> mouse,
            out HostThemeDecode theme);
        Assert.False(focusIn);
        Assert.Empty(mouse);
        Assert.Empty(csi);
        Assert.Equal("ab"u8.ToArray(), keys.ToArray());
        Assert.Equal(HostAppearance.Light, theme.Inferred);
        Assert.Null(theme.Explicit);
        Assert.Equal(new HostRgb(255, 255, 255), theme.Background);
        Assert.Null(theme.Foreground);
    }

    [Fact]
    public void Decode_retains_osc4_palette_rgb()
    {
        var csi = new List<byte>();
        var keys = AttachSession.DecodeInput(
            "\u001b]4;0;rgb:0101/0202/0303\u001b\\\u001b]4;255;#fcfdfe\u0007ab"u8,
            csi,
            out bool _,
            out List<MouseEvent> _,
            out HostThemeDecode theme);
        Assert.Equal("ab"u8.ToArray(), keys.ToArray());
        Assert.Equal(new HostRgb(1, 2, 3), theme.Palette[0]);
        Assert.Equal(new HostRgb(0xfc, 0xfd, 0xfe), theme.Palette[255]);
        Assert.Null(theme.Palette[1]);
        Assert.Null(theme.Foreground);
        Assert.Null(theme.Background);
    }

    [Fact]
    public void Host_theme_json_includes_sparse_palette()
    {
        var payload = AttachSession.HostThemeJson(new HostTerminalTheme(
            new HostRgb(1, 2, 3),
            new HostRgb(4, 5, 6),
            HostAppearance.Dark)
        {
            Palette = default(HostPalette)
                .WithColor(0, new HostRgb(9, 8, 7))
                .WithColor(255, new HostRgb(1, 0, 2)),
        });
        Assert.Equal(
            """{"fg":{"r":1,"g":2,"b":3},"bg":{"r":4,"g":5,"b":6},"appearance":"dark","palette":[{"i":0,"r":9,"g":8,"b":7},{"i":255,"r":1,"g":0,"b":2}]}""",
            payload.ToJsonString());
    }

    [Fact]
    public void Decode_retains_osc_10_foreground_rgb()
    {
        var csi = new List<byte>();
        var keys = AttachSession.DecodeInput(
            "\u001b]10;rgb:cccc/dddd/eeee\u001b\\"u8,
            csi,
            out bool _,
            out List<MouseEvent> _,
            out HostThemeDecode theme);
        Assert.Empty(keys);
        Assert.Equal(new HostRgb(0xcc, 0xdd, 0xee), theme.Foreground);
        Assert.Null(theme.Background);
        Assert.Null(theme.Inferred);
    }

    [Fact]
    public void Decode_swallows_csi_997()
    {
        var csi = new List<byte>();
        var keys = AttachSession.DecodeInput(
            "\u001b[?997;1nxy"u8,
            csi,
            out bool _,
            out List<MouseEvent> mouse,
            out HostThemeDecode theme);
        Assert.Empty(mouse);
        Assert.Empty(csi);
        Assert.Equal("xy"u8.ToArray(), keys.ToArray());
        Assert.Equal(HostAppearance.Dark, theme.Explicit);
    }

    [Fact]
    public void Decode_keeps_mouse_and_arrows()
    {
        var csi = new List<byte>();
        var arrows = AttachSession.DecodeInput(
            "\u001b[A"u8,
            csi,
            out bool _,
            out List<MouseEvent> mouse,
            out HostThemeDecode _);
        Assert.Empty(mouse);
        Assert.Equal("\u001b[A"u8.ToArray(), arrows.ToArray());

        var sgr = AttachSession.DecodeInput(
            "\u001b[<0;1;1M"u8,
            csi,
            out bool _,
            out mouse,
            out HostThemeDecode _);
        Assert.Empty(sgr);
        Assert.Single(mouse);
        Assert.Equal(MouseButton.Left, mouse[0].Button);
    }

    [Fact]
    public void Incomplete_osc_is_dropped_not_flushed_as_keys()
    {
        var csi = new List<byte>();
        var first = AttachSession.DecodeInput(
            "\u001b]11;rgb:ff"u8,
            csi,
            out bool _,
            out List<MouseEvent> _,
            out HostThemeDecode _);
        Assert.Empty(first);
        Assert.True(AttachSession.IsOscPending(csi));
        var flushed = AttachSession.FlushPendingCsi(csi);
        Assert.Empty(flushed);
        Assert.Empty(csi);
    }

    [Fact]
    public void Explicit_997_wins_over_inferred_osc_11()
    {
        var live = new AttachLiveState
        {
            Engine = new Hypa.Cli.Attach.Keys.KeyEngine(
                Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
                    Hypa.Cli.Attach.Keys.KeysConfig.Default())),
            Table = Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
                Hypa.Cli.Attach.Keys.KeysConfig.Default()),
            Dispatcher = null!,
            Theme = ThemeRuntime.FromConfig(new Hypa.AgentRuntime.Domain.AttachConfig.AttachThemeConfig
            {
                Name = "tokyo-night",
                AutoSwitch = true,
            }).Value,
        };
        var first = AttachSession.ApplyHostThemeDecode(live, new HostThemeDecode
        {
            Explicit = HostAppearance.Light,
        });
        Assert.True(first.ChromeChanged);
        Assert.Equal("tokyo-night-day", live.Theme.Name);
        Assert.True(live.Theme.AppearanceExplicit);
        var inferred = AttachSession.ApplyHostThemeDecode(live, new HostThemeDecode
        {
            Inferred = HostAppearance.Dark,
        });
        Assert.False(inferred.ChromeChanged);
        Assert.Equal("tokyo-night-day", live.Theme.Name);
        var second = AttachSession.ApplyHostThemeDecode(live, new HostThemeDecode
        {
            Explicit = HostAppearance.Dark,
        });
        Assert.True(second.ChromeChanged);
        Assert.Equal("tokyo-night", live.Theme.Name);
    }

    [Fact]
    public void Apply_keeps_osc_rgb_and_merges_incremental_channels()
    {
        var live = new AttachLiveState
        {
            Engine = new Hypa.Cli.Attach.Keys.KeyEngine(
                Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
                    Hypa.Cli.Attach.Keys.KeysConfig.Default())),
            Table = Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
                Hypa.Cli.Attach.Keys.KeysConfig.Default()),
            Dispatcher = null!,
            Theme = ThemeRuntime.Default,
        };
        var fg = AttachSession.ApplyHostThemeDecode(live, new HostThemeDecode
        {
            Foreground = new HostRgb(1, 2, 3),
        });
        Assert.True(fg.MuxThemeChanged);
        Assert.False(fg.ChromeChanged);
        Assert.Equal(new HostRgb(1, 2, 3), live.HostTheme.Foreground);
        Assert.Null(live.HostTheme.Background);

        var bg = AttachSession.ApplyHostThemeDecode(live, new HostThemeDecode
        {
            Background = new HostRgb(4, 5, 6),
            Inferred = HostAppearance.Dark,
        });
        Assert.True(bg.MuxThemeChanged);
        Assert.Equal(new HostRgb(1, 2, 3), live.HostTheme.Foreground);
        Assert.Equal(new HostRgb(4, 5, 6), live.HostTheme.Background);
    }

    [Fact]
    public void Restore_sequence_disables_mode_2031()
    {
        Assert.Contains(HostThemeParser.DisableReports, SnapshotPainter.RestoreSequence, StringComparison.Ordinal);
        Assert.Contains(SnapshotPainter.ResetCursorShape, SnapshotPainter.RestoreSequence, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_theme_json_is_snake_case_fg_bg_appearance()
    {
        var payload = AttachSession.HostThemeJson(new HostTerminalTheme(
            new HostRgb(1, 2, 3),
            new HostRgb(4, 5, 6),
            HostAppearance.Dark));
        Assert.Equal(
            """{"fg":{"r":1,"g":2,"b":3},"bg":{"r":4,"g":5,"b":6},"appearance":"dark"}""",
            payload.ToJsonString());
        Assert.Null(payload["Fg"]);
        Assert.Null(payload["Bg"]);
        Assert.Null(payload["Appearance"]);
    }

    [Fact]
    public void Host_theme_json_round_trips_source_generated_host_theme_set_params()
    {
        var theme = new HostTerminalTheme(
            new HostRgb(1, 2, 3),
            new HostRgb(4, 5, 6),
            HostAppearance.Dark)
        {
            Palette = default(HostPalette)
                .WithColor(0, new HostRgb(9, 8, 7))
                .WithColor(255, new HostRgb(1, 0, 2)),
        };
        var typed = AttachSession.ToHostThemeSetParams(theme);
        var fromContext = JsonSerializer.Serialize(
            typed, ProtocolJsonContext.Default.HostThemeSetParams);
        Assert.Equal(fromContext, AttachSession.HostThemeJson(theme).ToJsonString());
        var line = ControlPlaneClient.FormatRpcRequest(
            "1", ProtocolMethods.ClientHostThemeSet, AttachSession.HostThemeJson(theme));
        var req = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RpcRequest);
        Assert.NotNull(req);
        Assert.Equal("1", req.Id);
        Assert.Equal(ProtocolMethods.ClientHostThemeSet, req.Method);
        var roundTrip = JsonSerializer.Deserialize(
            req.Params!.Value, ProtocolJsonContext.Default.HostThemeSetParams);
        Assert.NotNull(roundTrip);
        Assert.Equal(1, roundTrip.Fg!.R);
        Assert.Equal(4, roundTrip.Bg!.R);
        Assert.Equal("dark", roundTrip.Appearance);
        Assert.Equal(2, roundTrip.Palette!.Length);
        Assert.Equal(0, roundTrip.Palette[0].I);
        Assert.Equal(255, roundTrip.Palette[1].I);
    }

    [Fact]
    public async Task Stdin_path_sends_client_host_theme_set_when_mux_theme_changed()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort();
        using var gate = new SemaphoreSlim(1, 1);
        var decode = new HostThemeDecode
        {
            Foreground = new HostRgb(1, 2, 3),
            Background = new HostRgb(4, 5, 6),
            Explicit = HostAppearance.Dark,
        };
        await AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);
        Assert.Equal(new HostRgb(1, 2, 3), live.HostTheme.Foreground);
        Assert.Equal(new HostRgb(4, 5, 6), live.HostTheme.Background);
        Assert.Equal(HostAppearance.Dark, live.HostTheme.Appearance);
        var call = Assert.Single(port.Calls);
        Assert.Equal(ProtocolMethods.ClientHostThemeSet, call.Method);
        Assert.Equal(
            """{"fg":{"r":1,"g":2,"b":3},"bg":{"r":4,"g":5,"b":6},"appearance":"dark"}""",
            call.Params!.ToJsonString());
    }

    [Fact]
    public async Task Failed_host_theme_rpc_does_not_commit_until_retry_succeeds()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort { FailuresRemaining = 1 };
        using var gate = new SemaphoreSlim(1, 1);
        var decode = new HostThemeDecode { Background = new HostRgb(4, 5, 6) };
        await AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);
        Assert.Null(live.HostTheme.Background);
        Assert.False(string.IsNullOrEmpty(live.StatusError));
        Assert.Single(port.Calls);

        live.StatusError = null;
        await AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);
        Assert.Equal(new HostRgb(4, 5, 6), live.HostTheme.Background);
        Assert.Null(live.StatusError);
        Assert.Equal(2, port.Calls.Count);
        Assert.All(port.Calls, c => Assert.Equal(ProtocolMethods.ClientHostThemeSet, c.Method));
    }

    [Fact]
    public async Task Failed_host_theme_rpc_does_not_commit_chrome()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort { FailuresRemaining = 1 };
        using var gate = new SemaphoreSlim(1, 1);
        var name = live.Theme.Name;
        var decode = new HostThemeDecode
        {
            Background = new HostRgb(250, 250, 250),
            Explicit = HostAppearance.Light,
        };
        await AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);
        Assert.Null(live.HostTheme.Background);
        Assert.Null(live.HostTheme.Appearance);
        Assert.Equal(name, live.Theme.Name);
        Assert.False(live.Theme.AppearanceExplicit);
        Assert.False(string.IsNullOrEmpty(live.StatusError));
    }

    [Fact]
    public async Task Cancelled_host_theme_rpc_does_not_commit()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort();
        port.HoldCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource();
        var decode = new HostThemeDecode
        {
            Background = new HostRgb(4, 5, 6),
            Explicit = HostAppearance.Light,
        };
        var name = live.Theme.Name;
        var task = AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, cts.Token);
        await port.WaitCallStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Null(live.HostTheme.Background);
        Assert.Equal(name, live.Theme.Name);
        Assert.False(live.Theme.AppearanceExplicit);
        port.ReleaseCall();
    }

    [Fact]
    public async Task Unexpected_host_theme_rpc_exception_does_not_commit()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort
        {
            Unexpected = new InvalidOperationException("transport reset"),
        };
        using var gate = new SemaphoreSlim(1, 1);
        var name = live.Theme.Name;
        var decode = new HostThemeDecode
        {
            Background = new HostRgb(4, 5, 6),
            Explicit = HostAppearance.Light,
        };
        await AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);
        Assert.Null(live.HostTheme.Background);
        Assert.Equal(name, live.Theme.Name);
        Assert.False(live.Theme.AppearanceExplicit);
        Assert.False(string.IsNullOrEmpty(live.StatusError));
    }

    [Fact]
    public void Ghostty_palette_flood_does_not_emit_keys()
    {
        var replies = new System.Text.StringBuilder();
        replies.Append("\u001b]10;rgb:6565/7b7b/8383\u001b\\");
        replies.Append("\u001b]11;rgb:2424/2727/3a3a\u001b\\");
        for (var index = 0; index <= 255; index++)
            replies.Append($"\u001b]4;{index};rgb:8787/afaf/ffff\u001b\\");

        var framer = new AttachHostInputFramer();
        framer.NoteHostColorQuerySent();
        var keys = AttachSession.DecodeInput(
            System.Text.Encoding.ASCII.GetBytes(replies.ToString()),
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out HostThemeDecode theme,
            mouseReports: null,
            framer);
        Assert.Empty(keys);
        Assert.Empty(framer.Pending);
        Assert.Equal(258, theme.ColorReports);
        Assert.Equal(0, framer.HostColorRepliesAwaited);
        Assert.Equal(new HostRgb(0x87, 0xaf, 0xff), theme.Palette[0]);
        Assert.Equal(new HostRgb(0x87, 0xaf, 0xff), theme.Palette[255]);
    }

    [Fact]
    public void Oversized_osc_tail_is_discarded_not_flushed_as_keys()
    {
        var body = new System.Text.StringBuilder("\u001b]4;0;rgb:8787/afaf/ffff");
        body.Append('x', AttachSession.MaxOscBytes);
        body.Append("8787/afaf/ffff\u001b\\ab");

        var framer = new AttachHostInputFramer();
        var keys = AttachSession.DecodeInput(
            System.Text.Encoding.ASCII.GetBytes(body.ToString()),
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out HostThemeDecode theme,
            mouseReports: null,
            framer);
        Assert.Equal("ab"u8.ToArray(), keys.ToArray());
        Assert.True(theme.OscDropped);
        Assert.False(framer.DiscardUntilOscTerminator);
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Awaiting_host_color_replies_keeps_split_osc4_across_idle_flush()
    {
        var framer = new AttachHostInputFramer();
        framer.NoteHostColorQuerySent();
        var first = AttachSession.DecodeInput(
            "\u001b]4;0;rgb:8787"u8,
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out HostThemeDecode _,
            mouseReports: null,
            framer);
        Assert.Empty(first);
        Assert.True(AttachSession.IsOscPending(framer.Pending));

        var flushed = AttachSession.FlushPendingCsi(framer.Pending, framer);
        Assert.Empty(flushed);
        Assert.True(AttachSession.IsOscPending(framer.Pending));

        var second = AttachSession.DecodeInput(
            "/afaf/ffff\u001b\\"u8,
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out HostThemeDecode theme,
            mouseReports: null,
            framer);
        Assert.Empty(second);
        Assert.Empty(framer.Pending);
        Assert.Equal(new HostRgb(0x87, 0xaf, 0xff), theme.Palette[0]);
        Assert.Equal(1, theme.ColorReports);
    }

    [Fact]
    public void Idle_flush_without_query_window_still_drops_incomplete_osc()
    {
        var csi = new List<byte>();
        var first = AttachSession.DecodeInput(
            "\u001b]4;0;rgb:8787"u8,
            csi,
            out bool _,
            out List<MouseEvent> _,
            out HostThemeDecode _);
        Assert.Empty(first);
        var flushed = AttachSession.FlushPendingCsi(csi);
        Assert.Empty(flushed);
        Assert.Empty(csi);
    }

    [Fact]
    public async Task Concurrent_host_theme_reports_merge_after_serialized_rpc()
    {
        var live = NewLive();
        var port = new RecordingHostThemePort();
        port.HoldCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new SemaphoreSlim(1, 1);
        var first = AttachSession.ApplyDecodedHostThemeAsync(
            live,
            new HostThemeDecode { Foreground = new HostRgb(1, 2, 3) },
            port,
            gate,
            tty: null,
            CancellationToken.None);
        await port.WaitCallStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var second = AttachSession.ApplyDecodedHostThemeAsync(
            live,
            new HostThemeDecode { Background = new HostRgb(4, 5, 6) },
            port,
            gate,
            tty: null,
            CancellationToken.None);
        Assert.Null(live.HostTheme.Foreground);
        Assert.Null(live.HostTheme.Background);
        port.ReleaseCall();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new HostRgb(1, 2, 3), live.HostTheme.Foreground);
        Assert.Equal(new HostRgb(4, 5, 6), live.HostTheme.Background);
        Assert.Equal(2, port.Calls.Count);
    }


    private static AttachLiveState NewLive() => new()
    {
        Engine = new Hypa.Cli.Attach.Keys.KeyEngine(
            Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
                Hypa.Cli.Attach.Keys.KeysConfig.Default())),
        Table = Hypa.Cli.Attach.Keys.KeyBindingTable.CompileOrThrow(
            Hypa.Cli.Attach.Keys.KeysConfig.Default()),
        Dispatcher = null!,
        Theme = ThemeRuntime.Default,
    };


    private sealed class RecordingHostThemePort : IAttachCommandPort
    {
        private readonly TaskCompletionSource _callStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Method, JsonObject? Params)> Calls { get; } = [];
        public int FailuresRemaining { get; set; }
        public Exception? Unexpected { get; set; }
        public TaskCompletionSource? HoldCall { get; set; }

        public Task WaitCallStartedAsync() => _callStarted.Task;

        public void ReleaseCall() => HoldCall?.TrySetResult();

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            _callStarted.TrySetResult();
            if (HoldCall is { } hold)
                await hold.Task.WaitAsync(ct).ConfigureAwait(false);
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InternalError,
                    "mux rejected host theme");
            }

            if (Unexpected is { } unexpected)
                throw unexpected;

            return default;
        }
    }
}
