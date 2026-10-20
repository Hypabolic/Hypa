using System.Text;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The Ghostty engine answers terminal queries through <c>write_pty</c> and
/// the device attributes and XTVERSION callbacks. Only a live child feed
/// collects the replies.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public class GhosttyQueryReplyTests
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public void Cursor_position_report_gives_the_cursor_row_and_column()
    {
        using var vt = NewEngine();
        var replies = new List<byte[]>();

        vt.FeedChildOutput("\u001b[3;5H\u001b[6n"u8, replies);

        Assert.Equal(["\u001b[3;5R"], Text(replies));
    }

    [SkippableFact]
    public void Cursor_position_report_follows_printed_text()
    {
        using var vt = NewEngine();
        var replies = new List<byte[]>();

        vt.FeedChildOutput("ab\r\ncde\u001b[6n"u8, replies);

        Assert.Equal(["\u001b[2;4R"], Text(replies));
    }

    [SkippableFact]
    public void Device_attributes_report_vt220_with_ansi_colour()
    {
        using var vt = NewEngine();

        Assert.Equal(["\u001b[?62;22c"], Live(vt, "\u001b[c"));
        Assert.Equal(["\u001b[>1;0;0c"], Live(vt, "\u001b[>c"));
        Assert.Equal(["\u001bP!|00000000\u001b\\"], Live(vt, "\u001b[=c"));
    }

    [SkippableFact]
    public void Xtversion_reports_hypa()
    {
        using var vt = NewEngine();

        Assert.Equal(["\u001bP>|hypa\u001b\\"], Live(vt, "\u001b[>q"));
    }

    [SkippableFact]
    public void Mode_and_status_queries_get_replies_in_stream_order()
    {
        using var vt = NewEngine();

        Assert.Equal(
            ["\u001b[0n", "\u001b[?2026;2$y", "\u001b[?62;22c"],
            Live(vt, "\u001b[5n\u001b[?2026$p\u001b[c"));
    }

    [SkippableFact]
    public void Plain_feed_sends_no_reply_and_leaves_nothing_for_the_next_live_feed()
    {
        using var vt = NewEngine();

        vt.Feed("\u001b[6n\u001b[c\u001b[>q"u8);
        vt.Feed("\u001b[c".AsSpan());

        Assert.Empty(Live(vt, "plain text"));
    }

    [SkippableFact]
    public void Replies_still_work_after_a_full_reset()
    {
        using var vt = NewEngine();

        vt.Feed("\u001bc"u8);
        Assert.Equal(["\u001b[1;1R"], Live(vt, "\u001b[6n"));

        vt.Reset();
        Assert.Equal(["\u001b[?62;22c"], Live(vt, "\u001b[c"));
    }

    [SkippableFact]
    public void Ghostty_answers_a_background_query_only_after_a_background_is_set()
    {
        using var vt = NewEngine();

        Assert.Empty(Live(vt, "\u001b]11;?\u001b\\"));

        vt.Feed("\u001b]11;rgb:0a/14/1e\u001b\\"u8);

        Assert.Equal(["\u001b]11;rgb:0a0a/1414/1e1e\u001b\\"], Live(vt, "\u001b]11;?\u001b\\"));
    }

    [SkippableFact]
    public void Many_engines_create_feed_and_dispose()
    {
        RequireLibrary();
        for (var i = 0; i < 64; i++)
        {
            using var vt = new GhosttyVtEngine(20, 6, maxScrollback: 100, libraryPathOverride: LibPath);
            Assert.Equal(["\u001b[1;1R"], Live(vt, "\u001b[6n"));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static GhosttyVtEngine NewEngine()
    {
        RequireLibrary();
        return new GhosttyVtEngine(20, 6, maxScrollback: 100, libraryPathOverride: LibPath);
    }

    private static string[] Live(GhosttyVtEngine vt, string bytes)
    {
        var replies = new List<byte[]>();
        vt.FeedChildOutput(Encoding.ASCII.GetBytes(bytes), replies);
        return Text(replies);
    }

    private static string[] Text(List<byte[]> replies) =>
        replies.Select(r => Encoding.ASCII.GetString(r)).ToArray();

    private static void RequireLibrary() => GhosttyTestRequire.RequireNativeLibrary(LibPath);
}
