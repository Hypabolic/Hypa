using System.Text;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Attach enables DECSET 2004 on the host TTY. Pasted text bypasses
/// keybindings and reaches the pane verbatim, wrapped in paste markers only
/// when the pane enabled bracketed paste itself.
/// </summary>
public sealed class BracketedPasteTests
{
    private const byte CtrlB = 0x02;

    [Fact]
    public async Task Paste_into_bracketed_pane_is_wrapped_and_not_executed_per_line()
    {
        var live = Live(paneBracketed: true);

        var sent = await FeedHostAsync(live, Pasted("echo one\recho two\r"));

        Assert.Equal(Wrapped("echo one\recho two\r"), sent);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
        Assert.False(live.Engine.PasteActive);
    }

    [Fact]
    public async Task Paste_into_plain_pane_is_forwarded_without_markers()
    {
        var live = Live(paneBracketed: false);

        var sent = await FeedHostAsync(live, Pasted("line one\rline two"));

        Assert.Equal(Encoding.UTF8.GetBytes("line one\rline two"), sent);
    }

    [Fact]
    public async Task Pasted_prefix_chord_is_text_not_a_binding()
    {
        var live = Live(paneBracketed: false);
        var paste = Pasted("a" + (char)CtrlB + "c");

        var sent = await FeedHostAsync(live, paste);

        Assert.Equal(new byte[] { (byte)'a', CtrlB, (byte)'c' }, sent);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);

        // Typed (unbracketed) prefix still works after the paste.
        var gate = new SemaphoreSlim(1, 1);
        await AttachSession.FeedKeysAfterControlGateAsync(live, [CtrlB], gate, CancellationToken.None);
        Assert.Equal(AttachClientMode.Prefix, live.Engine.Mode);
    }

    [Fact]
    public void Paste_split_across_reads_stays_one_paste()
    {
        var engine = Engine();
        engine.BracketedPaste = true;

        var events = new List<KeyEngineEvent>();
        events.AddRange(engine.Feed([.. KeyEngine.PasteStart, .. "abc"u8]));
        Assert.True(engine.PasteActive);
        events.AddRange(engine.Feed([(byte)'d', CtrlB, (byte)'\r']));
        events.AddRange(engine.Feed([.. "g"u8, .. KeyEngine.PasteEnd, (byte)'x']));

        Assert.False(engine.PasteActive);
        Assert.Equal(AttachClientMode.Terminal, engine.Mode);
        Assert.Equal(
            [.. KeyEngine.PasteStart, .. "abcd"u8, CtrlB, (byte)'\r', (byte)'g', .. KeyEngine.PasteEnd, (byte)'x'],
            Concat(events, KeyEngineEventKind.SendPaneBytes));
    }

    [Fact]
    public void Paste_while_in_prefix_mode_leaves_prefix_and_pastes()
    {
        var engine = Engine();
        engine.Feed([CtrlB]);
        Assert.Equal(AttachClientMode.Prefix, engine.Mode);

        var events = engine.Feed(Pasted("hi"));

        Assert.Equal(AttachClientMode.Terminal, engine.Mode);
        Assert.Equal("hi"u8.ToArray(), Concat(events, KeyEngineEventKind.SendPaneBytes));
    }

    [Fact]
    public void Paste_into_popup_routes_to_popup_with_popup_mode()
    {
        var engine = Engine();
        engine.PopupOpen = true;
        engine.PopupBracketedPaste = true;
        engine.BracketedPaste = false;

        var events = engine.Feed(Pasted("p"));

        Assert.DoesNotContain(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.All(events, e => Assert.Equal("popup", e.TargetId));
        Assert.Equal(Wrapped("p"), Concat(events, KeyEngineEventKind.SendPopupBytes));
    }

    [Fact]
    public void Paste_in_a_picker_is_typed_not_sent_to_the_pane()
    {
        var engine = Engine();
        engine.EnterNavigate();
        Assert.Equal(AttachClientMode.Navigate, engine.Mode);

        var events = engine.Feed(Pasted("zz"));

        Assert.DoesNotContain(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.False(engine.PasteActive);
    }

    [Fact]
    public void Missing_end_marker_times_out_so_bindings_recover()
    {
        long now = 0;
        var engine = Engine(() => now);
        engine.BracketedPaste = true;

        engine.Feed([.. KeyEngine.PasteStart, .. "abc"u8]);
        Assert.True(engine.PasteActive);

        now += KeyEngine.PasteIdleTimeoutMs + 1;
        var events = engine.Feed([CtrlB]);

        Assert.False(engine.PasteActive);
        Assert.Equal(AttachClientMode.Prefix, engine.Mode);
        Assert.Equal([.. KeyEngine.PasteEnd], Concat(events, KeyEngineEventKind.SendPaneBytes));
    }

    [Fact]
    public void Host_decode_passes_paste_markers_through_intact()
    {
        var pending = new List<byte>();
        var paste = Pasted("x");

        var first = AttachSession.DecodeInput(paste.AsSpan(0, 4), pending, out _);
        var second = AttachSession.DecodeInput(paste.AsSpan(4), pending, out _);

        Assert.Equal(paste, first.Concat(second).ToArray());
        Assert.Empty(pending);
    }

    [Fact]
    public void Detach_restore_disables_host_bracketed_paste()
    {
        Assert.Contains(SnapshotPainter.DisableBracketedPaste, SnapshotPainter.RestoreSequence, StringComparison.Ordinal);
    }

    private static byte[] Pasted(string text) =>
        [.. KeyEngine.PasteStart, .. Encoding.UTF8.GetBytes(text), .. KeyEngine.PasteEnd];

    private static byte[] Wrapped(string text) => Pasted(text);

    private static byte[] Concat(IEnumerable<KeyEngineEvent> events, KeyEngineEventKind kind) =>
        [.. events.Where(e => e.Kind == kind).SelectMany(e => e.Bytes ?? [])];

    private static async Task<byte[]> FeedHostAsync(AttachLiveState live, byte[] host)
    {
        var gate = new SemaphoreSlim(1, 1);
        var (events, _) = await AttachSession.FeedKeysAfterControlGateAsync(
            live,
            [.. host],
            gate,
            CancellationToken.None);
        var merged = AttachSession.CoalescePaneBytes(events);
        var send = Assert.Single(merged, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        return send.Bytes!;
    }

    private static KeyEngine Engine(Func<long>? ticks = null) =>
        new(
            KeyBindingTable.CompileOrThrow(KeysConfig.Default()),
            chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default),
            ticks: ticks);

    private static AttachLiveState Live(bool paneBracketed)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = null!,
        };
        live.PaneId = "p1";
        live.SetPaneFrame(new AssembledSnapshot(
            "p1",
            1,
            1,
            "ghostty",
            "main",
            [],
            default,
            AssembledCursor.Default,
            BracketedPaste: paneBracketed));
        return live;
    }
}
