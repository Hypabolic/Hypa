using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The host color reply window closes 1 s after the query or the last reply.
/// After that, Escape and <c>ESC ]</c> are keys again. A half-received OSC 10
/// or OSC 11 reply cannot hold input for more than 1 s or 128 bytes.
/// </summary>
public sealed class AttachHostReplyWindowTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void Lone_escape_flushes_on_the_first_idle_cycle_after_the_reply_deadline()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Decode(framer, "\u001b"u8, out _));
        var keys = AttachSession.FlushPendingCsi(framer.Pending, framer);

        Assert.Equal([0x1b], keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Lone_escape_is_held_one_idle_cycle_while_the_window_is_open()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        Decode(framer, "\u001b"u8, out _);
        Assert.Empty(AttachSession.FlushPendingCsi(framer.Pending, framer));
        Assert.Equal([0x1b], AttachSession.FlushPendingCsi(framer.Pending, framer).ToArray());
    }

    [Fact]
    public void Awaited_reply_count_is_zero_after_the_deadline_with_no_replies()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();
        Assert.Equal(AttachHostInputFramer.HostColorQueryReplies, framer.HostColorRepliesAwaited);

        time.Advance(Second);

        Assert.Equal(0, framer.HostColorRepliesAwaited);
        Assert.False(framer.AwaitingHostColorReplies);
    }

    [Fact]
    public void Osc_introducer_and_typed_text_become_keys_after_the_deadline()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        time.Advance(TimeSpan.FromSeconds(2));
        Decode(framer, "\u001b]"u8, out _);
        var keys = new List<byte>(AttachSession.FlushPendingCsi(framer.Pending, framer));
        keys.AddRange(Decode(framer, "abc \u007f"u8, out _));

        Assert.Equal("\u001b]abc \u007f"u8.ToArray(), keys.ToArray());
        Assert.Equal(0, framer.HostColorRepliesAwaited);
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Alt_bracket_alone_reaches_the_pane_after_one_idle_flush()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        Assert.Equal(0, framer.HostColorRepliesAwaited);

        Assert.Empty(Decode(framer, "\u001b]"u8, out _));
        var keys = AttachSession.FlushPendingCsi(framer.Pending, framer);

        Assert.Equal("\u001b]"u8.ToArray(), keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Osc_introducer_followed_by_text_in_the_window_is_keys_at_once()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        var keys = Decode(framer, "\u001b]ls\r"u8, out _);

        Assert.Equal("\u001b]ls\r"u8.ToArray(), keys.ToArray());
        Assert.Empty(framer.Pending);
        Assert.True(framer.AwaitingHostColorReplies);
    }

    [Fact]
    public void Begin_attempt_after_a_quiet_second_ends_the_wait_for_missing_replies()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();
        Decode(
            framer,
            "\u001b]10;rgb:6565/7b7b/8383\u001b\\\u001b]11;rgb:2424/2727/3a3a\u001b\\"u8,
            out var theme);
        Assert.Equal(2, theme.ColorReports);
        Assert.Equal(256, framer.HostColorRepliesAwaited);

        time.Advance(Second);
        framer.BeginAttempt();

        Assert.False(framer.AwaitingHostColorReplies);
        Assert.Equal(0, framer.HostColorRepliesAwaited);
    }

    [Fact]
    public void Begin_attempt_inside_the_quiet_window_keeps_waiting_for_replies()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        time.Advance(TimeSpan.FromMilliseconds(200));
        framer.BeginAttempt();

        Assert.True(framer.AwaitingHostColorReplies);
    }

    [Fact]
    public void Each_reply_starts_the_quiet_window_again()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        time.Advance(TimeSpan.FromMilliseconds(900));
        Decode(framer, "\u001b]4;0;rgb:8787/afaf/ffff\u001b\\"u8, out var theme);
        Assert.Equal(1, theme.ColorReports);

        time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(framer.AwaitingHostColorReplies);

        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(framer.AwaitingHostColorReplies);
    }

    [Fact]
    public void Truncated_osc11_reply_does_not_hold_later_input()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        Assert.Empty(Decode(framer, "\u001b]11;rgb:1e1e/1e"u8, out _));
        time.Advance(Second);
        var keys = Decode(framer, "echo ok\r"u8, out _);

        Assert.Equal("echo ok\r"u8.ToArray(), keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Idle_flush_drops_a_truncated_osc11_reply_after_the_idle_limit()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        Decode(framer, "\u001b]11;rgb:1e1e/1e"u8, out _);
        // Inside the limit the reply waits for its tail.
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Empty(AttachSession.FlushPendingCsi(framer.Pending, framer));
        Assert.NotEmpty(framer.Pending);

        time.Advance(TimeSpan.FromMilliseconds(950));
        Assert.Empty(AttachSession.FlushPendingCsi(framer.Pending, framer));
        Assert.Empty(framer.Pending);

        Assert.Equal("x"u8.ToArray(), Decode(framer, "x"u8, out _).ToArray());
    }

    [Fact]
    public void Osc11_prefix_that_grows_to_the_tail_limit_is_dropped()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        var prefix = "\u001b]11;rgb:" + new string('1', AttachHostInputFramer.MaxDiscardedOscTailBytes);
        var keys = Decode(framer, System.Text.Encoding.ASCII.GetBytes(prefix[..60]), out _);
        Assert.Empty(keys);
        keys = Decode(framer, System.Text.Encoding.ASCII.GetBytes(prefix[60..]), out _);
        keys.AddRange(Decode(framer, "ok"u8, out _));

        // The bytes up to 128 are the dropped reply. The rest are keys.
        var tail = prefix[AttachHostInputFramer.MaxDiscardedOscTailBytes..] + "ok";
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(tail), keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Osc11_reply_split_into_three_reads_inside_the_limit_still_parses()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        Assert.Empty(Decode(framer, "\u001b]11;rgb:"u8, out _));
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Empty(AttachSession.FlushPendingCsi(framer.Pending, framer));
        Assert.Empty(Decode(framer, "2424/27"u8, out _));
        time.Advance(TimeSpan.FromMilliseconds(400));
        var keys = Decode(framer, "27/3a3a\u001b\\"u8, out var theme);

        Assert.Empty(keys);
        Assert.Empty(framer.Pending);
        Assert.Equal(new HostRgb(0x24, 0x27, 0x3a), theme.Background);
        Assert.Equal(1, theme.ColorReports);
    }

    [Fact]
    public void Osc4_like_keys_after_the_deadline_are_not_swallowed()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Decode(framer, "\u001b]4;ab"u8, out _));
        var keys = AttachSession.FlushPendingCsi(framer.Pending, framer);

        Assert.Equal("\u001b]4;ab"u8.ToArray(), keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Split_osc4_reply_waits_across_idle_while_the_window_is_open()
    {
        var time = new ManualTimeProvider();
        var framer = new AttachHostInputFramer(time);
        framer.NoteHostColorQuerySent();

        Decode(framer, "\u001b]4;7;rgb:8787"u8, out _);
        time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Empty(AttachSession.FlushPendingCsi(framer.Pending, framer));
        Assert.Empty(Decode(framer, "/afaf/ffff\u001b\\"u8, out var theme));
        Assert.Equal(new HostRgb(0x87, 0xaf, 0xff), theme.Palette[7]);
    }

    private static List<byte> Decode(
        AttachHostInputFramer framer,
        ReadOnlySpan<byte> raw,
        out HostThemeDecode theme) =>
        AttachSession.DecodeInput(
            raw,
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out theme,
            mouseReports: null,
            framer);
}
