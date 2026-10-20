using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Attach.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Splash;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The client patch path: a cells delta stamps only its changed rows into
/// the kept host frame, encodes only those rows, and commits after the write.
/// A second attach with the patch path off composes the full redraw. After
/// each step both host frames and both kept frames are equal, cell by cell,
/// over the full host size.
/// </summary>
public sealed class HostPatchPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Patch_path_frame_equals_a_full_redraw_after_each_step(bool desktopTheme)
    {
        using var pair = new Pair(desktopTheme);
        var chrome = MouseTestGeom.Split();
        pair.Do((live, _) => live.Chrome = chrome);
        var p1 = Box(chrome, "p1");
        var p2 = Box(chrome, "p2");

        pair.Feed(Full("p1", p1, 1, ("$ ", 0)));
        pair.Feed(Full("p2", p2, 1, ("right pane", 0)));

        // Typing on the left pane, with the caret after the text.
        var g1 = 1L;
        foreach (var text in new[] { "$ l", "$ ls", "$ ls -", "$ ls -l" })
        {
            pair.Feed(Delta("p1", p1, g1, ++g1, Row(0, text), cursorCol: text.Length));
        }

        // A delta on the other pane.
        var g2 = 1L;
        pair.Feed(Delta("p2", p2, g2, ++g2, Row(1, "output")));

        // A cursor-only delta, then a delta on two rows.
        pair.Feed(Delta("p1", p1, g1, ++g1, cursorCol: 2, cursorRow: 0));
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(1, "total 0"), Row(2, "drwx 2 a b")));

        // Styled CJK and box-drawing cells inside the pane.
        pair.Feed(Delta("p1", p1, g1, ++g1, Wide(3, "日本語"), Row(4, "┌──┐", fg: 0xFF00FF00)));

        // A popup opens over the panes, then closes.
        var popup = new PopupChromeFrame(new CellRect(10, 5, 30, 10), new CellRect(11, 6, 28, 8));
        pair.Do((live, tty) =>
        {
            live.PopupOpen = true;
            live.Chrome = chrome with { PopupFrame = popup };
            AttachSession.PaintChrome(tty, live);
        });
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(5, "under popup")));
        pair.Do((live, tty) =>
        {
            live.PopupOpen = false;
            live.Chrome = chrome;
            AttachSession.PaintAfterPopupClosed(tty, live);
        });
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(5, "after popup")));

        // The alternate screen and back.
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(0, "vim"), activeScreen: "alternate"));
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(1, "~"), activeScreen: "alternate"));
        pair.Feed(Delta("p1", p1, g1, ++g1, Row(0, "$ "), activeScreen: "main"));

        // A host resize: new chrome, full frames at the new boxes, then deltas.
        var wide = LayoutChromeGeometry.Compute(
            100,
            30,
            MouseTestGeom.RightSplit(),
            false,
            null,
            "p1",
            LayoutChromeGeometry.BareInsets,
            2,
            AttachClientMode.Terminal,
            [("t1", "one", true), ("t2", "two", false)]);
        pair.Do((live, tty) =>
        {
            live.Chrome = wide;
            AttachSession.PaintChrome(tty, live);
        });
        var w1 = Box(wide, "p1");
        var w2 = Box(wide, "p2");
        pair.Feed(Full("p1", w1, 50, ("$ ", 0)));
        pair.Feed(Full("p2", w2, 50, ("right", 0)));
        g1 = 50;
        foreach (var text in new[] { "$ e", "$ ec", "$ ech", "$ echo" })
            pair.Feed(Delta("p1", w1, g1, ++g1, Row(0, text), cursorCol: text.Length));

        Assert.True(pair.Patch.Latency.Paint.Patches >= 10, Counters(pair.Patch));
        Assert.Equal(0, pair.Full.Latency.Paint.Patches);
    }

    /// <summary>
    /// The mux sends a full body with no reanchor when it is smaller than the
    /// delta, for example when bulk output changes all rows of a pane. Such a
    /// body of the same size takes the patch path with all rows of its pane.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_body_without_reanchor_of_the_same_size_paints_through_the_patch_path(bool desktopTheme)
    {
        using var pair = new Pair(desktopTheme);
        var chrome = MouseTestGeom.Split();
        pair.Do((live, _) => live.Chrome = chrome);
        var p1 = Box(chrome, "p1");
        var p2 = Box(chrome, "p2");
        pair.Feed(Full("p1", p1, 1, ("$ ", 0)));
        pair.Feed(Full("p2", p2, 1, ("bulk", 0)));
        var fullFrames = pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame);
        var fullComposes = pair.Patch.Latency.Paint.FullComposes;

        var g1 = 1L;
        void Type(string text) =>
            pair.Feed(Delta("p1", p1, g1, ++g1, Row(0, text), cursorCol: text.Length));

        void Patched(TerminalRenderCellsPayload body)
        {
            var before = pair.Patch.Latency.Paint.Patches;
            pair.Feed(body);
            Assert.True(pair.Patch.Latency.Paint.Patches == before + 1, Counters(pair.Patch));
        }

        Type("$ b");

        // Each row of the pane gets new text.
        var dense = Enumerable.Range(0, p2.Rows).Select(r => Row(r, "line " + r + " " + new string((char)('a' + (r % 26)), 20))).ToArray();
        Patched(Restate("p2", p2, 2, dense, cursorCol: 0, cursorRow: p2.Rows - 1));

        // Only two rows: the rows that the body leaves out are blank now.
        Patched(Restate("p2", p2, 3, [Row(0, "cleared"), Row(5, "five")], cursorCol: 4, cursorRow: 5));
        Type("$ ba");

        // Styled, wide and CJK cells, and a cursor move.
        Patched(Restate(
            "p2",
            p2,
            4,
            [Wide(1, "日本語"), Row(2, "┌──┐", fg: 0xFF00FF00), Row(3, "plain")],
            cursorCol: 3,
            cursorRow: 3));

        // A delta that chains on the full body, then a row shift.
        Patched(Delta("p2", p2, 4, 5, Row(4, "after full")));
        Patched(Delta("p2", p2, 5, 6, Row(p2.Rows - 1, "shifted")) with { Shift = 1 });
        Type("$ bat");

        // To the alternate screen is a full compose. A full body that stays
        // on the alternate screen takes the patch path.
        var altBefore = pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.AltScreen);
        pair.Feed(Restate("p2", p2, 7, [Row(0, "vim")], cursorCol: 0, cursorRow: 1, activeScreen: "alternate"));
        Assert.Equal(altBefore + 1, pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.AltScreen));
        Patched(Restate("p2", p2, 8, [Row(0, "vim"), Row(1, "~"), Row(2, "~")], cursorCol: 0, cursorRow: 1, activeScreen: "alternate"));
        Patched(Restate("p2", p2, 9, [Row(0, "back")], cursorCol: 4, cursorRow: 0, activeScreen: "alternate"));

        // One batch: a full body on p2 and a typing delta on p1.
        var batchBefore = pair.Patch.Latency.Paint.Patches;
        pair.FeedBatch(
            Restate("p2", p2, 10, [Row(0, "batch"), Row(1, "rows")], cursorCol: 0, cursorRow: 2, activeScreen: "alternate"),
            Delta("p1", p1, g1, ++g1, Row(0, "$ batc"), cursorCol: 6));
        Assert.Equal(batchBefore + 1, pair.Patch.Latency.Paint.Patches);

        // A scrolled-back view is a full compose.
        var viewportBefore = pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.Viewport);
        pair.Feed(Restate("p2", p2, 11, [Row(0, "history")], cursorCol: 0, cursorRow: 0, activeScreen: "alternate") with { ViewportOrigin = 3 });
        Assert.Equal(viewportBefore + 1, pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.Viewport));

        Assert.Equal(fullFrames, pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame));
        Assert.Equal(fullComposes + 2, pair.Patch.Latency.Paint.FullComposes);
    }

    /// <summary>
    /// A full body and a row shift unpack into a second grid of the pane. A
    /// refused write puts back the frame before it. The host still shows that
    /// frame, so the patches after it must equal a full redraw from the
    /// stored frame.
    /// </summary>
    [Fact]
    public void Patches_after_a_refused_full_body_or_row_shift_equal_a_full_redraw()
    {
        using var pair = new Pair(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        pair.Do((live, _) => live.Chrome = chrome);
        var p1 = Box(chrome, "p1");
        var p2 = Box(chrome, "p2");
        pair.Feed(Full("p1", p1, 1, ("$ ", 0)));
        pair.Feed(Full("p2", p2, 1, ("bulk", 0)));
        var dense = Enumerable.Range(0, p2.Rows).Select(r => Row(r, "line " + r)).ToArray();
        pair.Feed(Restate("p2", p2, 2, dense, cursorCol: 0, cursorRow: p2.Rows - 1));
        pair.Feed(Delta("p2", p2, 2, 3, Row(p2.Rows - 1, "shifted")) with { Shift = 2 });

        void Refused(params TerminalRenderCellsPayload[] batch)
        {
            pair.Do((live, _) => live.PresentationFrozen = true);
            pair.Do((live, tty) => Assert.All(AttachSession.TryWriteCellsBatch(tty, live, batch, reanchorBeforePaint: true), Assert.False));
            pair.Do((live, _) => live.PresentationFrozen = false);
        }

        var other = Enumerable.Range(0, p2.Rows).Select(r => Row(r, "refused " + r)).ToArray();
        // A refused row shift keeps the chain: the next delta chains on 3.
        Refused(Delta("p2", p2, 3, 4, Row(p2.Rows - 1, "lost")) with { Shift = 3 });
        var patches = pair.Patch.Latency.Paint.Patches;
        pair.Feed(Delta("p2", p2, 3, 4, Row(0, "after shift")));
        Assert.Equal(patches + 1, pair.Patch.Latency.Paint.Patches);

        // A refused batch of full bodies and a delta.
        Refused(
            Restate("p2", p2, 5, other, cursorCol: 0, cursorRow: 0),
            Delta("p2", p2, 5, 6, Row(1, "lost")),
            Restate("p2", p2, 7, other.Take(3).ToArray(), cursorCol: 0, cursorRow: 0));
        // The refused full body reset the chain. The next full body has the
        // size of the kept grid, so it takes the patch path.
        patches = pair.Patch.Latency.Paint.Patches;
        pair.Feed(Restate("p2", p2, 8, dense.Skip(2).ToArray(), cursorCol: 0, cursorRow: 2));
        pair.Feed(Delta("p2", p2, 8, 9, Row(p2.Rows - 1, "next")) with { Shift = 1 });
        pair.Feed(Delta("p1", p1, 1, 2, Row(0, "$ a"), cursorCol: 3));
        Assert.Equal(patches + 3, pair.Patch.Latency.Paint.Patches);
    }

    /// <summary>
    /// Another attach owns the size of p2, so the client admits a frame that
    /// is smaller than the pane box. A later full body at the box size has a
    /// new size, so it is a full compose.
    /// </summary>
    [Fact]
    public void Full_body_of_a_new_size_stays_a_full_compose()
    {
        using var pair = new Pair(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        pair.Do((live, _) => live.Chrome = chrome);
        var p1 = Box(chrome, "p1");
        var p2 = Box(chrome, "p2");
        pair.Feed(Full("p1", p1, 1, ("$ ", 0)));
        pair.Feed(Full("p2", p2 with { Cols = p2.Cols - 4 }, 1, ("narrow", 0)));
        pair.Feed(Delta("p1", p1, 1, 2, Row(0, "$ a"), cursorCol: 3));
        var fullFrames = pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame);
        var fullComposes = pair.Patch.Latency.Paint.FullComposes;

        pair.Feed(Restate("p2", p2, 2, [Row(0, "wide"), Row(1, "rows")], cursorCol: 0, cursorRow: 2));
        Assert.Equal(fullFrames + 1, pair.Patch.Latency.Paint.Fallbacks(HostPatchFallback.FullFrame));
        Assert.Equal(fullComposes + 1, pair.Patch.Latency.Paint.FullComposes);

        // The next full body has the size of the kept grid.
        var patches = pair.Patch.Latency.Paint.Patches;
        pair.Feed(Restate("p2", p2, 3, [Row(0, "same size")], cursorCol: 0, cursorRow: 1));
        Assert.Equal(patches + 1, pair.Patch.Latency.Paint.Patches);
    }

    [Fact]
    public void Patch_path_without_chrome_equals_a_full_redraw()
    {
        using var pair = new Pair(desktopTheme: false);
        var box = new CellRect(0, 0, 80, 24);
        pair.Feed(Full("p1", box, 1, ("$ ", 0)));
        var g = 1L;
        foreach (var text in new[] { "$ a", "$ ab", "$ abc" })
            pair.Feed(Delta("p1", box, g, ++g, Row(0, text), cursorCol: text.Length));
        pair.Feed(Delta("p1", box, g, ++g, Row(23, "last row"), cursorCol: 8, cursorRow: 23));

        Assert.True(pair.Patch.Latency.Paint.Patches >= 4, Counters(pair.Patch));
    }

    [Fact]
    public void One_cell_delta_does_not_clear_the_host_or_stamp_the_chrome()
    {
        using var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var live = NewLive(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        live.Chrome = chrome;
        var box = Box(chrome, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, ("$ ", 0)), reanchorBeforePaint: true));

        // A marker in a chrome cell of the host frame. A clear or a chrome
        // stamp would replace it.
        var marker = new AssembledCell("#", 1, false, AssembledStyle.Default);
        live.Host.Stamp(0, 0, marker);
        var mark = output.Length;

        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, Row(0, "$ x")), reanchorBeforePaint: true));

        Assert.Equal(1, live.Latency.Paint.Patches);
        Assert.Equal("#", live.Host.CellAt(0, 0).Text);
        Assert.Equal("x", live.Host.CellAt(box.Col + 2, box.Row).Text);
        var written = Encoding.UTF8.GetString(output.ToArray(), (int)mark, (int)(output.Length - mark));
        Assert.Contains("x", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[2J", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[1;1H", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_patch_write_keeps_the_kept_frame_and_the_next_paint_is_a_full_compose()
    {
        var sink = new FailingStream();
        using var tty = new UnixRawTerminal(sink, 80, 24);
        var live = NewLive(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        live.Chrome = chrome;
        var box = Box(chrome, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, ("$ ", 0)), reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, Row(0, "$ a")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Patches);
        var kept = KeptCells(live);

        sink.Fail = true;
        Assert.ThrowsAny<IOException>(() =>
            AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 3, Row(0, "$ ab")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Patches);
        Assert.Equal(kept, KeptCells(live));
        Assert.Null(live.PatchBaseline);

        // The lost frame did not store its generation, so the next delta
        // chains on the last shown frame. That paint is a full compose, and
        // it shows the newest cells.
        sink.Fail = false;
        var fullBefore = live.Latency.Paint.FullComposes;
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 4, Row(0, "$ abc")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Fallbacks(HostPatchFallback.NoBaseline));
        Assert.Equal(fullBefore + 1, live.Latency.Paint.FullComposes);
        Assert.Equal(1, live.Latency.Paint.Patches);
        Assert.Equal("c", live.Host.CellAt(box.Col + 4, box.Row).Text);
        Assert.True(live.HostEncoder.TryGetKeptCell(box.Col + 4, box.Row, out var keptCell));
        Assert.Equal("c", keptCell.Text);

        // The paint after that full compose takes the patch path again.
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 4, 5, Row(0, "$ abcd")), reanchorBeforePaint: true));
        Assert.Equal(2, live.Latency.Paint.Patches);
    }

    [Fact]
    public void Partial_patch_write_makes_the_next_paint_rewrite_every_cell()
    {
        var sink = new FailingStream();
        using var tty = new UnixRawTerminal(sink, 80, 24);
        var live = NewLive(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        live.Chrome = chrome;
        var box = Box(chrome, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, ("$ ", 0)), reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, Row(0, "$ a")), reanchorBeforePaint: true));
        Assert.False(live.HostEncoder.RepaintPending);

        // The host takes a part of the patch bytes, then the write fails.
        // The host now shows cells that the kept frame does not have.
        sink.AcceptBeforeFail = 12;
        Assert.ThrowsAny<IOException>(() =>
            AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 3, Row(0, "$ ab")), reanchorBeforePaint: true));
        Assert.True(live.HostEncoder.RepaintPending);

        // The next frame has the same cells as the kept frame. A diff against
        // the kept frame finds no change, but the paint must rewrite every
        // cell to repair the host.
        sink.AcceptBeforeFail = null;
        var noOpsBefore = live.Latency.Paint.NoOps;
        var mark = sink.Length;
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 4, Row(0, "$ a")), reanchorBeforePaint: true));
        Assert.Equal(noOpsBefore, live.Latency.Paint.NoOps);
        Assert.False(live.HostEncoder.RepaintPending);
        var written = Encoding.UTF8.GetString(sink.ToArray(), (int)mark, (int)(sink.Length - mark));
        Assert.Contains(HostBlitEncoder.SyncBegin, written, StringComparison.Ordinal);
        Assert.Contains("$ a", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Repaint_request_between_patch_encode_and_commit_stays_pending()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var live = NewLive(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        live.Chrome = chrome;
        var box = Box(chrome, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, ("$ ", 0)), reanchorBeforePaint: true));

        // Another thread asks for a repaint after the patch encode and
        // before its commit.
        tty.BeforeComposedWrite = () =>
        {
            tty.BeforeComposedWrite = null;
            live.HostEncoder.RequestRepaint();
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, Row(0, "$ a")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Patches);
        Assert.True(live.HostEncoder.RepaintPending);

        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 2, 3, Row(0, "$ ab")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Fallbacks(HostPatchFallback.Repaint));
        Assert.False(live.HostEncoder.RepaintPending);
    }

    [Fact]
    public void Rejected_patch_write_leaves_no_baseline_so_the_next_delta_is_a_full_compose()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var live = NewLive(desktopTheme: false);
        var chrome = MouseTestGeom.Split();
        live.Chrome = chrome;
        var box = Box(chrome, "p1");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full("p1", box, 1, ("$ ", 0)), reanchorBeforePaint: true));
        var kept = KeptCells(live);

        // A newer stored frame makes the accept fail inside the write lock.
        tty.BeforeComposedWrite = () =>
        {
            tty.BeforeComposedWrite = null;
            live.TryGetPaneFrame("p1", out var stored);
            live.SetPaneFrame(stored! with { Generation = 99 });
        };
        Assert.False(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 1, 2, Row(0, "$ a")), reanchorBeforePaint: true));
        Assert.Equal(kept, KeptCells(live));
        Assert.Null(live.PatchBaseline);

        live.StoreAppliedBlit("p1", 99, 0);
        Assert.True(AttachSession.TryWriteCells(tty, live, Delta("p1", box, 99, 100, Row(0, "$ b")), reanchorBeforePaint: true));
        Assert.Equal(1, live.Latency.Paint.Fallbacks(HostPatchFallback.NoBaseline));
        Assert.Equal(0, live.Latency.Paint.Patches);
    }

    public static TheoryData<string> FallbackReasons() =>
    [
        nameof(HostPatchFallback.Chrome),
        "Theme",
        nameof(HostPatchFallback.Status),
        "StatusError",
        nameof(HostPatchFallback.HostSize),
        nameof(HostPatchFallback.Repaint),
        nameof(HostPatchFallback.Frozen),
        nameof(HostPatchFallback.FullFrame),
        "Reanchor",
        "NewOccupant",
        nameof(HostPatchFallback.SizeMismatch),
        nameof(HostPatchFallback.AltScreen),
        nameof(HostPatchFallback.PopupOcclusion),
        nameof(HostPatchFallback.Viewport),
        nameof(HostPatchFallback.Selection),
        nameof(HostPatchFallback.Toast),
        nameof(HostPatchFallback.Overlay),
        nameof(HostPatchFallback.Popup),
        nameof(HostPatchFallback.PaintMode),
        nameof(HostPatchFallback.Splash),
        nameof(HostPatchFallback.WideEdge),
        nameof(HostPatchFallback.DrawnCursor),
        nameof(HostPatchFallback.Baseline),
        nameof(HostPatchFallback.OtherPane),
        nameof(HostPatchFallback.Composing),
    ];

    [Theory]
    [MemberData(nameof(FallbackReasons))]
    public void Each_fallback_reason_makes_a_full_compose_and_counts_it(string name)
    {
        var theme = name == "Theme";
        var statusError = name == "StatusError";
        var reanchor = name == "Reanchor";
        var newOccupant = name == "NewOccupant";
        var reason = theme ? HostPatchFallback.Chrome
            : statusError ? HostPatchFallback.Status
            : reanchor || newOccupant ? HostPatchFallback.FullFrame
            : Enum.Parse<HostPatchFallback>(name);
        var chromeOff = reason is HostPatchFallback.HostSize;
        using var pair = new Pair(desktopTheme: false, cols: chromeOff ? 40 : 80, rows: chromeOff ? 12 : 24);
        var chrome = MouseTestGeom.Split();
        CellRect box;
        if (chromeOff)
            box = new CellRect(0, 0, 40, 12);
        else
        {
            pair.Do((live, _) => live.Chrome = chrome);
            box = Box(chrome, "p1");
            pair.Feed(Full("p2", Box(chrome, "p2"), 1, ("right", 0)));
        }

        pair.Feed(Full("p1", box, 1, ("$ ", 0)));
        pair.Feed(Delta("p1", box, 1, 2, Row(0, "$ a")));
        Assert.Equal(1, pair.Patch.Latency.Paint.Patches);

        var delta = Delta("p1", box, 2, 3, Row(1, "next"));
        switch (reason)
        {
            case HostPatchFallback.Chrome when theme:
                // The theme changes its palette in place.
                pair.Do((live, _) => Assert.True(live.Theme.SetName("borland")));
                break;
            case HostPatchFallback.Chrome:
                pair.Do((live, _) => live.Chrome = chrome with { });
                break;
            case HostPatchFallback.Status when statusError:
                pair.Do((live, _) => live.Status.Set(StatusOwner.Command, StatusSeverity.Error, "failed"));
                break;
            case HostPatchFallback.Status:
                pair.Do((live, _) => live.Status.Set(StatusOwner.Command, StatusSeverity.Notice, "saved"));
                break;
            case HostPatchFallback.HostSize:
                box = new CellRect(0, 0, 40, 12);
                pair.ReplaceTty(41, 12);
                delta = Delta("p1", box, 2, 3, Row(1, "next"));
                break;
            case HostPatchFallback.Repaint:
                pair.Do((live, _) => live.HostEncoder.RequestRepaint());
                break;
            case HostPatchFallback.Frozen:
                pair.Do((live, _) =>
                {
                    live.PresentationFrozen = true;
                    live.FrozenChromePaintArmed = true;
                });
                break;
            case HostPatchFallback.FullFrame when reanchor:
                delta = Full("p1", box, 3, ("$ a", 0), ("next", 1)) with { Full = false, Reanchor = true };
                break;
            case HostPatchFallback.FullFrame when newOccupant:
                delta = Full("p1", box, 3, ("$ a", 0), ("next", 1)) with { OccupantGeneration = 2 };
                break;
            case HostPatchFallback.FullFrame:
                // A chain break: the mux sends a full body that is a reanchor.
                delta = Full("p1", box, 3, ("$ a", 0), ("next", 1)) with { Reanchor = true };
                break;
            case HostPatchFallback.SizeMismatch:
                delta = Delta("p1", box with { Cols = box.Cols + 4 }, 2, 3, Row(1, "next"));
                break;
            case HostPatchFallback.AltScreen:
                delta = Delta("p1", box, 2, 3, Row(1, "next"), activeScreen: "alternate");
                break;
            case HostPatchFallback.PopupOcclusion:
                pair.Do((live, _) =>
                {
                    live.PopupOpen = true;
                    live.Chrome = chrome with
                    {
                        PopupFrame = new PopupChromeFrame(
                            new CellRect(box.Col + 1, box.Row + 1, 6, 4),
                            new CellRect(box.Col + 2, box.Row + 2, 4, 2)),
                    };
                });
                break;
            case HostPatchFallback.Viewport:
                delta = delta with { ViewportOrigin = 3 };
                break;
            case HostPatchFallback.Selection:
                pair.Do((live, _) => live.Mouse.Selection.BeginHost(1, 1));
                break;
            case HostPatchFallback.Toast:
                pair.Do((live, _) => live.Toasts = [new ToastHit("p2", new CellRect(60, 20, 10, 1), "done")]);
                break;
            case HostPatchFallback.Overlay:
                pair.Do((live, _) => live.Overlay.ApplyServerShow(
                    "p9",
                    1,
                    new PopupGeometryResult
                    {
                        OuterCol = 50,
                        OuterRow = 4,
                        OuterCols = 20,
                        OuterRows = 10,
                        InnerCol = 51,
                        InnerRow = 5,
                        InnerCols = 18,
                        InnerRows = 8,
                    }));
                break;
            case HostPatchFallback.Popup:
                pair.Do((live, _) => live.PopupOpen = true);
                break;
            case HostPatchFallback.PaintMode:
                pair.Do((live, _) => live.Worktrees.ShowCreate("w1", "repo", "/tmp/repo-wt", 7));
                break;
            case HostPatchFallback.Splash:
                pair.Do((live, _) => live.Splash = StartupSplashSurface.Start(loadLogoOverlay: false));
                break;
            case HostPatchFallback.WideEdge:
                delta = Delta("p1", box, 2, 3, WideAt(1, box.Cols - 1, "日"));
                break;
            case HostPatchFallback.DrawnCursor:
                pair.Do((live, _) => live.Ui = live.Ui with { HostCursor = HostCursorMode.Drawn });
                break;
            case HostPatchFallback.Baseline:
                pair.Do((live, _) =>
                {
                    live.TryGetPaneFrame("p1", out var stored);
                    live.SetPaneFrame(stored! with { });
                });
                break;
            case HostPatchFallback.OtherPane:
                pair.Do((live, _) =>
                {
                    live.TryGetPaneFrame("p2", out var stored);
                    live.SetPaneFrame(stored! with { });
                });
                break;
            case HostPatchFallback.Composing:
                break;
        }

        var fullBefore = pair.Patch.Latency.Paint.FullComposes;
        var totalBefore = pair.Patch.Latency.Paint.TotalFallbacks();
        var reasonBefore = pair.Patch.Latency.Paint.Fallbacks(reason);
        if (reason is HostPatchFallback.Composing)
        {
            pair.Do((live, tty) => tty.CaptureWrites(() =>
                AttachSession.TryWriteCells(tty, live, delta, reanchorBeforePaint: true)));
        }
        else
            pair.FeedNoCompare(delta);

        var counters = pair.Patch.Latency.Paint;
        Assert.True(counters.Fallbacks(reason) == reasonBefore + 1, Counters(pair.Patch));
        Assert.Equal(1, counters.Patches);
        Assert.Equal(reasonBefore + 1, counters.Fallbacks(reason));
        Assert.Equal(totalBefore + 1, counters.TotalFallbacks());
        Assert.Equal(fullBefore + 1, counters.FullComposes);
        if (reason is not (HostPatchFallback.Frozen or HostPatchFallback.Composing))
            pair.AssertSame();
    }

    private static string Counters(AttachLiveState live) =>
        string.Join(", ", live.Latency.Paint.Report().Where(kv => kv.Value > 0).Select(kv => kv.Key + "=" + kv.Value));

    private static CellRect Box(LayoutChromeGeometry chrome, string paneId) =>
        Assert.Single(chrome.Panes, pane => pane.PaneId == paneId).Content;

    private static AssembledCell[] KeptCells(AttachLiveState live)
    {
        var cells = new AssembledCell[live.Host.Cols * live.Host.Rows];
        for (var r = 0; r < live.Host.Rows; r++)
        {
            for (var c = 0; c < live.Host.Cols; c++)
            {
                Assert.True(live.HostEncoder.TryGetKeptCell(c, r, out var cell));
                cells[r * live.Host.Cols + c] = cell;
            }
        }

        return cells;
    }

    private static TerminalRenderCellRow Row(int row, string text, uint fg = 0) => new()
    {
        I = row,
        T = text,
        S = fg == 0 ? null : [new TerminalRenderStyleRun { C = 0, N = text.Length, Fg = fg }],
    };

    private static TerminalRenderCellRow Wide(int row, string text) => new()
    {
        I = row,
        T = text,
        W = text.Select(_ => 2).ToArray(),
        S = [new TerminalRenderStyleRun { C = 0, N = text.Length * 2, Fg = 0xFFFF0000, Bold = true }],
    };

    private static TerminalRenderCellRow WideAt(int row, int col, string text) => new()
    {
        I = row,
        T = new string(' ', col) + text,
        W = Enumerable.Repeat(1, col).Concat(text.Select(_ => 2)).ToArray(),
    };

    private static TerminalRenderCellsPayload Full(
        string paneId,
        CellRect box,
        long generation,
        params (string Text, int Row)[] rows) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = true,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = generation,
            Rows = rows.Select(r => new TerminalRenderCellRow { I = r.Row, T = r.Text }).ToList(),
            Cursor = new TerminalRenderCursorPayload
            {
                Col = rows.Length > 0 ? rows[0].Text.Length : 0,
                Row = 0,
                Visible = true,
            },
        };

    /// <summary>A full body with no reanchor, as the mux sends it when it is smaller than the delta.</summary>
    private static TerminalRenderCellsPayload Restate(
        string paneId,
        CellRect box,
        long generation,
        TerminalRenderCellRow[] rows,
        int cursorCol,
        int cursorRow,
        string? activeScreen = null) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = true,
            Reanchor = false,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = generation,
            ActiveScreen = activeScreen,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = cursorCol, Row = cursorRow, Visible = true },
        };

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        params TerminalRenderCellRow[] rows) =>
        Delta(paneId, box, baseGeneration, generation, rows, null, 0, null);

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        TerminalRenderCellRow row,
        int cursorCol,
        int cursorRow = 0) =>
        Delta(paneId, box, baseGeneration, generation, [row], cursorCol, cursorRow, null);

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        int cursorCol,
        int cursorRow) =>
        Delta(paneId, box, baseGeneration, generation, [], cursorCol, cursorRow, null);

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        TerminalRenderCellRow row,
        string activeScreen) =>
        Delta(paneId, box, baseGeneration, generation, [row], null, 0, activeScreen);

    private static TerminalRenderCellsPayload Delta(
        string paneId,
        CellRect box,
        long baseGeneration,
        long generation,
        TerminalRenderCellRow[] rows,
        int? cursorCol,
        int cursorRow,
        string? activeScreen) => new()
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = paneId,
            Full = false,
            GridCols = box.Cols,
            GridRows = box.Rows,
            BaseGeneration = baseGeneration,
            Generation = generation,
            ActiveScreen = activeScreen,
            Rows = rows,
            Cursor = cursorCol is { } col
            ? new TerminalRenderCursorPayload { Col = col, Row = cursorRow, Visible = true }
            : null,
        };

    private static AttachLiveState NewLive(bool desktopTheme)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new MouseRecordingPort(), "w1", "t1", "p1", "lease-r"),
            PaneId = "p1",
            SessionName = "patch",
        };
        if (desktopTheme)
            live.Theme = ThemeRuntime.FromConfig(new AttachThemeConfig { Name = "borland" }).Value;
        return live;
    }

    /// <summary>
    /// Two attaches that get the same input. <see cref="Patch"/> uses the
    /// patch path. <see cref="Full"/> composes the full redraw for each paint.
    /// </summary>
    private sealed class FailingStream : MemoryStream
    {
        public bool Fail { get; set; }

        /// <summary>When set, the next write keeps this many bytes, then fails.</summary>
        public int? AcceptBeforeFail { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail)
                throw new IOException("host write failed");
            if (AcceptBeforeFail is { } keep)
            {
                base.Write(buffer, offset, Math.Min(keep, count));
                throw new IOException("host write failed after a partial write");
            }

            base.Write(buffer, offset, count);
        }
    }

    private sealed class Pair : IDisposable
    {
        private UnixRawTerminal _patchTty;
        private UnixRawTerminal _fullTty;

        public Pair(bool desktopTheme, int cols = 80, int rows = 24)
        {
            _patchTty = new UnixRawTerminal(new MemoryStream(), cols, rows);
            _fullTty = new UnixRawTerminal(new MemoryStream(), cols, rows);
            Patch = NewLive(desktopTheme);
            Full = NewLive(desktopTheme);
            Full.PatchPaintEnabled = false;
        }

        public AttachLiveState Patch { get; }

        public AttachLiveState Full { get; }

        /// <summary>A host terminal of a new size for both attaches.</summary>
        public void ReplaceTty(int cols, int rows)
        {
            _patchTty.Dispose();
            _fullTty.Dispose();
            _patchTty = new UnixRawTerminal(new MemoryStream(), cols, rows);
            _fullTty = new UnixRawTerminal(new MemoryStream(), cols, rows);
        }

        public void Do(Action<AttachLiveState, UnixRawTerminal> action)
        {
            action(Patch, _patchTty);
            action(Full, _fullTty);
        }

        public void Feed(TerminalRenderCellsPayload cells)
        {
            FeedNoCompare(cells);
            AssertSame();
        }

        public void FeedBatch(params TerminalRenderCellsPayload[] batch)
        {
            var patched = AttachSession.TryWriteCellsBatch(_patchTty, Patch, batch, reanchorBeforePaint: true);
            var full = AttachSession.TryWriteCellsBatch(_fullTty, Full, batch, reanchorBeforePaint: true);
            Assert.Equal(full, patched);
            AssertSame();
        }

        public void FeedNoCompare(TerminalRenderCellsPayload cells)
        {
            var patched = AttachSession.TryWriteCells(_patchTty, Patch, cells, reanchorBeforePaint: true);
            var full = AttachSession.TryWriteCells(_fullTty, Full, cells, reanchorBeforePaint: true);
            Assert.Equal(full, patched);
        }

        public void AssertSame()
        {
            Assert.Equal(Full.Host.Cols, Patch.Host.Cols);
            Assert.Equal(Full.Host.Rows, Patch.Host.Rows);
            for (var r = 0; r < Full.Host.Rows; r++)
            {
                for (var c = 0; c < Full.Host.Cols; c++)
                {
                    var want = Full.Host.CellAt(c, r);
                    var got = Patch.Host.CellAt(c, r);
                    Assert.True(
                        HostFrame.CellsEqual(want, got),
                        $"host cell {c},{r}: full '{want.Text}' {want.Style} patch '{got.Text}' {got.Style}");
                    Assert.Equal(
                        Full.HostEncoder.TryGetKeptCell(c, r, out var keptWant),
                        Patch.HostEncoder.TryGetKeptCell(c, r, out var keptGot));
                    Assert.True(
                        HostFrame.CellsEqual(keptWant, keptGot),
                        $"kept cell {c},{r}: full '{keptWant.Text}' patch '{keptGot.Text}'");
                }
            }

            Assert.Equal(Full.Host.Cursor, Patch.Host.Cursor);
        }

        public void Dispose()
        {
            _patchTty.Dispose();
            _fullTty.Dispose();
        }
    }
}
