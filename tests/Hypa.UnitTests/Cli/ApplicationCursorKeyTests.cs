using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ApplicationCursorKeyTests
{
    [Fact]
    public async Task Focused_pane_application_cursor_selects_ss3_or_csi()
    {
        var live = Live();
        live.PaneId = "p1";
        live.SetPaneFrame(Frame(applicationCursor: true));
        live.Engine.ApplicationCursor = false;

        Assert.Equal(Ss3('A'), await FeedHostAsync(live, Csi('A')));
        Assert.Equal(Ss3('H'), await FeedHostAsync(live, Csi('H')));
        Assert.Equal(Ss3('F'), await FeedHostAsync(live, Csi('F')));
        Assert.Equal(Ss3('B'), await FeedHostAsync(live, Csi('B')));
        Assert.Equal(Ss3('C'), await FeedHostAsync(live, Csi('C')));
        Assert.Equal(Ss3('D'), await FeedHostAsync(live, Csi('D')));

        live.SetPaneFrame(Frame(applicationCursor: false));
        Assert.Equal(Csi('A'), await FeedHostAsync(live, Csi('A')));
        Assert.Equal(Csi('H'), await FeedHostAsync(live, Csi('H')));
        Assert.Equal(Csi('F'), await FeedHostAsync(live, Csi('F')));
    }

    [Fact]
    public async Task Split_ss3_prefix_translates_after_the_final_byte()
    {
        var pending = new List<byte>();
        var first = AttachSession.DecodeInput(new byte[] { 0x1B, (byte)'O' }, pending, out _);
        Assert.Empty(first);
        Assert.Equal(new byte[] { 0x1B, (byte)'O' }, pending.ToArray());

        var second = AttachSession.DecodeInput(new byte[] { (byte)'A' }, pending, out _);
        Assert.Equal(Ss3('A'), second.ToArray());
        Assert.Empty(pending);

        var live = Live();
        live.PaneId = "p1";
        live.SetPaneFrame(Frame(applicationCursor: false));
        live.Engine.ApplicationCursor = true;
        Assert.Equal(Csi('A'), await FeedHostAsync(live, second.ToArray()));

        live.SetPaneFrame(Frame(applicationCursor: true));
        live.Engine.ApplicationCursor = false;
        Assert.Equal(Ss3('A'), await FeedHostAsync(live, second.ToArray()));
    }

    [Fact]
    public void Synthetic_cursor_chord_follows_the_same_mode()
    {
        var live = Live();
        live.PaneId = "p1";
        live.SetPaneFrame(Frame(applicationCursor: true));

        Assert.Equal(Ss3('A'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, false, "up"))));
        Assert.Equal(Ss3('H'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, false, "home"))));
        Assert.Equal(Ss3('F'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, false, "end"))));

        live.SetPaneFrame(Frame(applicationCursor: false));
        Assert.Equal(Csi('A'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, false, "up"))));
        Assert.Equal(XtermShift('A'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, true, "up"))));

        live.SetPaneFrame(Frame(applicationCursor: true));
        Assert.Equal(XtermShift('A'), PaneBytes(live.Engine.Feed(new KeyChord(false, false, false, true, "up"))));
    }

    [Fact]
    public void Ss3_cursor_decodes_as_the_named_key()
    {
        AssertCursor("\u001bOA", "up");
        AssertCursor("\u001bOB", "down");
        AssertCursor("\u001bOC", "right");
        AssertCursor("\u001bOD", "left");
        AssertCursor("\u001bOH", "home");
        AssertCursor("\u001bOF", "end");
    }

    [Fact]
    public void Cells_body_replaces_input_modes_from_each_payload()
    {
        var on = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            GridCols = 1,
            GridRows = 1,
            Rows = [],
            BracketedPaste = true,
            ApplicationCursor = true,
        };
        var first = VtCellUnpacker.Apply(on, previous: null);
        Assert.True(first.ApplicationCursor);
        Assert.True(first.BracketedPaste);

        var off = on with { BracketedPaste = false, ApplicationCursor = false };
        var second = VtCellUnpacker.Apply(off, first);
        Assert.False(second.ApplicationCursor);
        Assert.False(second.BracketedPaste);
    }

    private static void AssertCursor(string bytes, string key)
    {
        var decoded = KeyEventDecoder.Decode(System.Text.Encoding.ASCII.GetBytes(bytes));
        var one = Assert.Single(decoded);
        Assert.Equal(key, one.Chord.Key);
        Assert.False(one.Chord.Alt);
        Assert.False(one.Chord.Ctrl);
        Assert.False(one.Chord.Shift);
    }

    private static async Task<byte[]> FeedHostAsync(AttachLiveState live, byte[] host)
    {
        var gate = new SemaphoreSlim(1, 1);
        var (events, _) = await AttachSession.FeedKeysAfterControlGateAsync(
            live,
            [.. host],
            gate,
            CancellationToken.None);
        return PaneBytes(events);
    }

    private static byte[] PaneBytes(IReadOnlyList<KeyEngineEvent> events)
    {
        var send = Assert.Single(events, e => e.Kind == KeyEngineEventKind.SendPaneBytes);
        Assert.NotNull(send.Bytes);
        return send.Bytes;
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = null!,
        };
    }

    private static AssembledSnapshot Frame(bool applicationCursor) =>
        new(
            "p1",
            1,
            1,
            "ghostty",
            "main",
            [],
            default,
            AssembledCursor.Default,
            ApplicationCursor: applicationCursor);

    private static byte[] Csi(char letter) => [0x1B, (byte)'[', (byte)letter];

    private static byte[] Ss3(char letter) => [0x1B, (byte)'O', (byte)letter];

    private static byte[] XtermShift(char letter) =>
        System.Text.Encoding.ASCII.GetBytes($"\u001b[1;2{letter}");
}
