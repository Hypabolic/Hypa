using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.History;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The live capture keeps a retained grid and stamps only the rows that
/// Ghostty marks dirty. These tests compare every captured frame with a full
/// stamp of a second engine that read the same bytes.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class LiveDirtyRowStampTests(ITestOutputHelper output)
{
    private const string FixtureDirectory = "tests/Hypa.AgentRuntime.Tests/Fixtures/typing";

    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    private static readonly string[] FixtureNames =
    [
        "claude-code",
        "zsh-f",
        "zsh-p10k",
        "bash-norc",
        "vim-insert",
        "sync-echo-tui",
    ];

    private static readonly string[] StreamNames =
    [
        "scroll",
        "erase",
        "alternate-screen",
        "resize",
        "scroll-region",
        "wide-glyphs",
        "scroll-offset",
        "colours",
        "prompt-shrink",
        "prune",
    ];

    public static TheoryData<string, int> CorpusCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in FixtureNames)
        {
            // Seed 0 keeps each recorded read whole. 11 and 23 are the
            // typing harness split seeds.
            data.Add("fixture:" + name, 0);
            data.Add("fixture:" + name, 11);
            data.Add("fixture:" + name, 23);
        }

        foreach (var name in StreamNames)
        {
            data.Add("stream:" + name, 1);
            data.Add("stream:" + name, 2);
        }

        return data;
    }

    public static TheoryData<LiveStampFallbackReason> FallbackReasons()
    {
        var data = new TheoryData<LiveStampFallbackReason>();
        foreach (var reason in LiveStampFallbackReasons.All)
        {
            // The attach route counts this reason, not a live stamp.
            // SnapshotRouteFailClosedTests covers it.
            if (reason != LiveStampFallbackReason.AttachCaptureLimit)
                data.Add(reason);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(CorpusCases))]
    public async Task Dirty_row_stamp_matches_full_stamp_on_unmarked_rows(string source, int seed)
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        var script = LoadScript(source, seed);
        var run = await ReplayAsync(script, seed, verifyInterval: int.MaxValue);
        output.WriteLine(
            $"{source} seed {seed}: captures {run.Captures}, dirty-row stamps {run.Counters.DirtyRowStamps}, "
            + $"hinted packs {run.HintedPacks}, mismatched rows {run.MismatchedRows}");
        foreach (var line in run.Mismatches.Take(8))
            output.WriteLine("  " + line);

        Assert.True(run.MismatchedRows == 0, string.Join("; ", run.Mismatches.Take(4)));
        Assert.True(run.PackMismatches == 0, string.Join("; ", run.PackNotes.Take(4)));
        Assert.True(run.Counters.DirtyRowStamps > 0, "the stream never took the dirty-row path");

        // The check frame must agree with the dirty-row stamp on the same run.
        var checkedRun = await ReplayAsync(script, seed, verifyInterval: 2);
        Assert.Equal(0, checkedRun.MismatchedRows);
        Assert.Equal(0, checkedRun.Counters.VerifyMismatchRows);
        if (run.Counters.DirtyRowStamps >= 8)
            Assert.True(checkedRun.Counters.Fallbacks[LiveStampFallbackReasons.Name(LiveStampFallbackReason.VerifyFrame)] > 0);
    }

    [SkippableTheory]
    [InlineData(80, 24)]
    [InlineData(200, 50)]
    [InlineData(300, 80)]
    public async Task One_row_echo_reads_cols_times_dirty_rows_cells(int cols, int rows)
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows);
        pane.LiveStampVerifyInterval = int.MaxValue;
        pane.FeedVtForTests("line one\r\nline two\r\n$ ");
        CaptureAndCommit(pane);
        pane.FeedVtForTests("e");
        CaptureAndCommit(pane);

        pane.FeedVtForTests("c");
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out var kind));
        Assert.Equal(PaneVtPaintKind.Full, kind);
        var delta = Assert.IsType<VtFrameGridDelta>(frame!.Grid.Delta);
        Assert.Contains(2, delta.Rows);
        Assert.InRange(delta.Rows.Length, 1, 2);
        Assert.Equal(cols * delta.Rows.Length, pane.LastLiveStampCellsVisited);
        Assert.Equal("c", frame.CellAt(2, 3).Text);
    }

    [SkippableTheory]
    [InlineData("default")]
    [InlineData("host-theme")]
    [InlineData("osc-11")]
    public void Blank_cell_shortcut_matches_the_slow_stamp(string colours)
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 40;
        const int rows = 12;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        if (colours == "host-theme")
        {
            vt.SetHostDefaultColors(new HostRgb(0xE0, 0xE0, 0xD0), new HostRgb(0x10, 0x14, 0x20));
            var palette = default(HostPalette)
                .WithColor(0, new HostRgb(0x11, 0x11, 0x11))
                .WithColor(1, new HostRgb(0xCC, 0x22, 0x22))
                .WithColor(4, new HostRgb(0x22, 0x44, 0xCC));
            vt.SetDefaultPalette(palette);
        }

        vt.Feed(Encoding.UTF8.GetBytes(BlankCellScreen()));
        if (colours == "osc-11")
            vt.Feed("\u001b]11;rgb:20/30/40\u001b\\\u001b]10;rgb:f0/e0/d0\u001b\\"u8);

        var slow = new StampBuffer(cols, rows);
        var fast = new StampBuffer(cols, rows);
        vt.DisableBlankCellShortcut = true;
        var hitsBefore = vt.BlankCellShortcutHits;
        Assert.True(vt.TryStampPaintSnapshot(slow.Cells, slow.Tables, out _, out _, out _));
        Assert.Equal(hitsBefore, vt.BlankCellShortcutHits);
        vt.DisableBlankCellShortcut = false;
        Assert.True(vt.TryStampPaintSnapshot(fast.Cells, fast.Tables, out _, out _, out _));
        Assert.True(vt.BlankCellShortcutHits > hitsBefore, "the shortcut never ran");

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var want = slow.ViewAt(r, c);
                var got = fast.ViewAt(r, c);
                Assert.True(
                    want.VisuallyEquals(got),
                    $"cell {c},{r}: slow '{want.Text}' w{want.Width} cont {want.IsContinuation} "
                    + $"fg {want.Fg} bg {want.Bg} mod {want.Modifier} ul {want.UnderlineColor}/{want.UnderlineStyle}; "
                    + $"fast '{got.Text}' w{got.Width} cont {got.IsContinuation} "
                    + $"fg {got.Fg} bg {got.Bg} mod {got.Modifier} ul {got.UnderlineColor}/{got.UnderlineStyle}");
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(FallbackReasons))]
    public async Task Each_stamp_fallback_increments_its_named_counter(LiveStampFallbackReason reason)
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 30;
        const int rows = 8;
        var store = reason == LiveStampFallbackReason.DeepScroll
            ? new InMemoryPaneHistoryStore(new BrotliPaneHistoryCodec(), 100_000, 50_000_000, 64)
            : null;
        using var vt = new GhosttyVtEngine(
            cols,
            rows,
            maxScrollback: reason == LiveStampFallbackReason.DeepScroll ? 1 : GhosttyVtEngine.DefaultMaxScrollback,
            libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows, store);
        pane.LiveStampVerifyInterval = int.MaxValue;
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);

        if (reason != LiveStampFallbackReason.NoRetainedGrid)
            await PrimeAsync(pane, reason);

        var before = counters.Snapshot();
        await TriggerAsync(pane, vt, reason);
        if (reason == LiveStampFallbackReason.RenderStateUpdate)
        {
            // Styles from an old render state are wrong, so nothing posts.
            Assert.False(pane.TryCaptureLivePaintFrame(out _, out _, out _));
        }
        else
        {
            Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
            Assert.NotNull(frame);
        }

        var after = counters.Snapshot();

        var changed = string.Join(
            ", ",
            LiveStampFallbackReasons.All
                .Select(LiveStampFallbackReasons.Name)
                .Where(name => after.Fallbacks[name] != before.Fallbacks[name])
                .Select(name => $"{name} +{after.Fallbacks[name] - before.Fallbacks[name]}"));
        foreach (var other in LiveStampFallbackReasons.All)
        {
            var name = LiveStampFallbackReasons.Name(other);
            var expected = other == reason ? 1 : 0;
            Assert.True(
                after.Fallbacks[name] - before.Fallbacks[name] == expected,
                $"{name}: want +{expected}; changed: [{changed}], dirty-row stamps +{after.DirtyRowStamps - before.DirtyRowStamps}");
        }

        Assert.Equal(before.DirtyRowStamps, after.DirtyRowStamps);
        vt.ForceLiveStampFault = LiveStampFallbackReason.None;
        vt.ForceRenderStateUpdateFailure = false;
        // The next paint after the failure is a full stamp that posts.
        Assert.True(pane.TryCaptureLivePaintFrame(out var next, out _, out _));
        Assert.NotNull(next);
    }

    [SkippableFact]
    public async Task Full_stamp_with_a_failed_row_lookup_posts_nothing_and_the_next_paint_is_whole()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 30;
        const int rows = 8;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows, null);
        pane.FeedVtForTests("first line\r\nsecond line");

        // No retained grid, so this paint is a full stamp. Row 1 fails.
        vt.ForceFullStampRowFailure = 1;
        Assert.False(pane.TryCaptureLivePaintFrame(out _, out _, out _));

        vt.ForceFullStampRowFailure = -1;
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
        Assert.NotNull(frame);
        Assert.Contains("second line", RowText(frame!, 1), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Hint_is_used_only_against_the_grid_that_the_dirty_set_is_relative_to()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows);
        pane.LiveStampVerifyInterval = int.MaxValue;
        pane.FeedVtForTests("aaaa\r\nbbbb\r\ncccc");
        var first = CaptureAndCommit(pane);
        // Row 0 changes. The cursor goes back to row 2.
        pane.FeedVtForTests("\u001b[1;1HXX\u001b[3;5H");
        var second = CaptureAndCommit(pane);
        pane.FeedVtForTests("YY");
        Assert.True(pane.TryCaptureLivePaintFrame(out var third, out _, out _));

        // The dirty set of the third capture is relative to the second grid.
        var delta = Assert.IsType<VtFrameGridDelta>(third!.Grid.Delta);
        Assert.Same(second.Grid.Identity, delta.BaseIdentity);
        Assert.DoesNotContain(0, delta.Rows);

        // A subscriber that still holds the first grid is behind by row 0.
        // The packer must not trust the hint for that baseline.
        var lagging = VtCellPacker.Prepare(third, first, dirtyRows: null);
        Assert.NotNull(lagging);
        Assert.Contains(lagging.Value.Rows, row => row.I == 0);
        Assert.Contains(lagging.Value.Rows, row => row.I == 2);
        Assert.Equal(PackBytes(Unhinted(third), first), PackBytes(third, first));

        // The current subscriber gets the same body with the hint as without.
        var current = VtCellPacker.Prepare(third, second, dirtyRows: null);
        Assert.NotNull(current);
        Assert.DoesNotContain(current.Value.Rows, row => row.I == 0);
        Assert.Equal(PackBytes(Unhinted(third), second), PackBytes(third, second));
    }

    [SkippableFact]
    public async Task No_hint_names_a_grid_from_before_an_uncommitted_full_stamp()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows);
        pane.LiveStampVerifyInterval = int.MaxValue;
        pane.FeedVtForTests("\u001b[31mred\u001b[0m\r\nplain\r\n$ ");
        CaptureAndCommit(pane);
        pane.FeedVtForTests("a");
        var committed = CaptureAndCommit(pane);

        // The child sets palette entry 1. Every red cell now resolves to the
        // new RGB value, but no row is dirty. The capture restamps every row.
        // The post of that frame does not commit.
        pane.FeedVtForTests("\u001b]4;1;rgb:10/c0/20\u001b\\");
        Assert.True(pane.TryCaptureLivePaintFrame(out var recoloured, out _, out _));
        Assert.Null(recoloured!.Grid.Delta);
        Assert.NotEqual(committed.CellAt(0, 0).Fg, recoloured.CellAt(0, 0).Fg);

        pane.FeedVtForTests("b");
        Assert.True(pane.TryCaptureLivePaintFrame(out var echo, out _, out _));
        Assert.True(
            echo!.Grid.Delta is null || !ReferenceEquals(echo.Grid.Delta.BaseIdentity, committed.Grid.Identity),
            "the hint names the grid from before the colour change");
        var packed = VtCellPacker.Prepare(echo, committed, dirtyRows: null);
        Assert.NotNull(packed);
        Assert.Contains(packed.Value.Rows, row => row.I == 0);
    }

    [SkippableFact]
    public async Task A_commit_after_another_capture_forces_a_full_stamp()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows);
        pane.LiveStampVerifyInterval = int.MaxValue;
        pane.FeedVtForTests("one\r\ntwo");
        CaptureAndCommit(pane);
        pane.FeedVtForTests("\u001b[1;1HZZ");
        // The attach capture reads the render state, then its post commits.
        // The retained live grid does not hold row 0 yet.
        Assert.True(pane.TryCaptureAttachFrame(out _, out _));
        pane.CommitPostedPaint();

        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
        Assert.Null(frame!.Grid.Delta);
        Assert.Equal("Z", frame.CellAt(0, 0).Text);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.DirtyFlagsCleared));
    }

    private static VtFrame CaptureAndCommit(PaneRuntime pane)
    {
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
        Assert.NotNull(frame);
        pane.CommitPostedPaint();
        return frame!;
    }

    private static async Task PrimeAsync(PaneRuntime pane, LiveStampFallbackReason reason)
    {
        // Deep scroll needs rows in the history store that Ghostty no longer
        // holds. Feed in batches so that each drain copies them first.
        var batches = reason == LiveStampFallbackReason.DeepScroll ? 10 : 1;
        for (var batch = 0; batch < batches; batch++)
        {
            var lines = new StringBuilder();
            var count = reason == LiveStampFallbackReason.DeepScroll ? 500 : 30;
            for (var i = 0; i < count; i++)
                lines.Append("prime line ").Append((batch * count) + i).Append("\r\n");
            pane.FeedVtForTests(lines.ToString());
        }

        pane.FeedVtForTests("$ ");
        CaptureAndCommit(pane);
        // One dirty-row stamp, so the retained grid is valid.
        pane.FeedVtForTests("x");
        var frame = CaptureAndCommit(pane);
        Assert.NotNull(frame.Grid.Delta);

        if (reason == LiveStampFallbackReason.ScrollOffsetChange)
        {
            Assert.True(pane.TrySetScrollOrigin(2));
            CaptureAndCommit(pane);
        }

        if (reason == LiveStampFallbackReason.TableLimit)
        {
            pane.MaxRetainedStyles = 1;
            pane.FeedVtForTests("\u001b[31mr\u001b[0m");
            CaptureAndCommit(pane);
        }

        await Task.CompletedTask;
    }

    private static async Task TriggerAsync(PaneRuntime pane, GhosttyVtEngine vt, LiveStampFallbackReason reason)
    {
        switch (reason)
        {
            case LiveStampFallbackReason.NoRetainedGrid:
                pane.FeedVtForTests("first");
                break;
            case LiveStampFallbackReason.Resize:
                await pane.ResizeAsync(vt.Cols + 4, vt.Rows, CancellationToken.None);
                break;
            case LiveStampFallbackReason.ScreenSwitch:
                pane.FeedVtForTests("\u001b[?1049h");
                break;
            case LiveStampFallbackReason.ScrollOffsetChange:
                Assert.True(pane.TrySetScrollOrigin(0));
                break;
            case LiveStampFallbackReason.DeepScroll:
                Assert.True(pane.TrySetScrollOrigin(int.MaxValue / 2));
                break;
            case LiveStampFallbackReason.ScrollOffset:
                Assert.True(pane.TrySetScrollOrigin(2));
                break;
            case LiveStampFallbackReason.VerifyFrame:
                pane.LiveStampVerifyInterval = 1;
                pane.FeedVtForTests("v");
                break;
            case LiveStampFallbackReason.ColorChange:
                // A host theme palette changes how cells resolve. Ghostty
                // marks no row dirty for it.
                vt.SetDefaultPalette(default(HostPalette).WithColor(1, new HostRgb(0x10, 0xC0, 0x20)));
                pane.FeedVtForTests("k");
                break;
            case LiveStampFallbackReason.DirtyFlagsCleared:
                pane.FeedVtForTests("d");
                Assert.True(pane.TryCaptureAttachFrame(out _, out _));
                pane.CommitPostedPaint();
                pane.FeedVtForTests("e");
                break;
            case LiveStampFallbackReason.TableLimit:
                pane.FeedVtForTests("t");
                break;
            case LiveStampFallbackReason.GhosttyFull:
                // ED 2 erases in place. Ghostty reports a full redraw, and the
                // size, the screen and the scroll stay.
                pane.FeedVtForTests("\u001b[2J");
                break;
            case LiveStampFallbackReason.RenderStateUpdate:
                vt.ForceRenderStateUpdateFailure = true;
                pane.FeedVtForTests("u");
                break;
            default:
                vt.ForceLiveStampFault = reason;
                pane.FeedVtForTests("f");
                break;
        }
    }

    private static string RowText(VtFrame frame, int row)
    {
        var text = new System.Text.StringBuilder();
        for (var c = 0; c < frame.Cols; c++)
            text.Append(frame.CellAt(row, c).Text);
        return text.ToString();
    }

    private static string BlankCellScreen() =>
        "plain text then blanks\r\n"
        // Background-only erase: blank cells with a style.
        + "\u001b[41m\u001b[K\u001b[0m\r\n"
        + "a\u001b[1m \u001b[0mb\u001b[7m   \u001b[0mc\u001b[4m \u001b[0m\r\n"
        // A hyperlink on a space and on text.
        + "\u001b]8;;https://example.com\u001b\\ link \u001b]8;;\u001b\\ after\r\n"
        // DECSCA protected spaces.
        + "\u001b[1\"q   \u001b[0\"qp\r\n"
        // OSC 133 prompt marks on a row with blanks.
        + "\u001b]133;A\u001b\\$ \u001b]133;B\u001b\\cmd\u001b]133;C\u001b\\\r\n"
        // Wide glyphs, a wide glyph at the row end, combining and joined emoji.
        + "漢字 かな   x\u001b[39G漢\r\n"
        + "é 👍🏽 👨‍👩‍👧 \u001b[44m \u001b[0m\r\n"
        + "\u001b[38;5;196mred\u001b[48;5;21m on blue \u001b[0m \u001b[38;2;10;200;30mrgb\u001b[0m";

    private static PaneRuntime NewPane(GhosttyVtEngine vt, int cols, int rows, IPaneHistoryStore? store = null) =>
        new(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt,
            historyStore: store,
            nativeWindowBytes: store is null ? 0 : 1,
            scrollbackLimitBytes: store is null ? 0 : 50_000_000);

    private static VtFrame Unhinted(VtFrame frame) =>
        frame with { Grid = new VtFrameGrid(frame.Grid.Cells, frame.Grid.Tables, frame.Grid.Cols, frame.Grid.Rows) };

    private static string PackBytes(VtFrame frame, VtFrame? baseline)
    {
        var packed = VtCellPacker.Prepare(frame, baseline, dirtyRows: null);
        if (packed is null)
            return "<equal>";
        var payload = VtCellPacker.ToPayload(packed.Value, "p", 2, 1, 0);
        return Encoding.UTF8.GetString(VtCellPacker.SerializeUtf8(payload));
    }

    private abstract record Step;

    private sealed record FeedStep(byte[] Bytes) : Step;

    private sealed record ScrollStep(int Offset) : Step;

    private sealed record ResizeStep(int Cols, int Rows) : Step;

    private sealed record Script(int Cols, int Rows, int MaxScrollback, IReadOnlyList<Step> Steps);

    private sealed class ReplayResult
    {
        public int Captures { get; set; }

        public int MismatchedRows { get; set; }

        public int HintedPacks { get; set; }

        public int PackMismatches { get; set; }

        public List<string> Mismatches { get; } = [];

        public List<string> PackNotes { get; } = [];

        public LiveStampCounterSnapshot Counters { get; set; } = null!;
    }

    /// <summary>
    /// Feed each step to the pane and to an oracle engine. After most steps,
    /// capture the live frame and compare every row with a full stamp of the
    /// oracle. Commit most captures, so the dirty set sometimes covers more
    /// than one capture, as it does for a held paint.
    /// </summary>
    private static async Task<ReplayResult> ReplayAsync(Script script, int seed, int verifyInterval)
    {
        using var vt = new GhosttyVtEngine(script.Cols, script.Rows, script.MaxScrollback, LibPath);
        using var oracle = new GhosttyVtEngine(script.Cols, script.Rows, script.MaxScrollback, LibPath);
        await using var pane = NewPane(vt, script.Cols, script.Rows);
        pane.LiveStampVerifyInterval = verifyInterval;
        var random = new Random(seed + 1000);
        var result = new ReplayResult();
        VtFrame? committed = null;
        VtFrame? older = null;
        var index = 0;
        foreach (var step in script.Steps)
        {
            index++;
            switch (step)
            {
                case FeedStep feed:
                    pane.FeedVtForTests(feed.Bytes);
                    oracle.Feed(feed.Bytes);
                    break;
                case ScrollStep scroll:
                    Assert.True(pane.TrySetScrollOrigin(scroll.Offset));
                    oracle.SetScrollOffsetFromBottom(scroll.Offset);
                    break;
                case ResizeStep resize:
                    await pane.ResizeAsync(resize.Cols, resize.Rows, CancellationToken.None);
                    oracle.Resize(resize.Cols, resize.Rows);
                    break;
            }

            if (random.Next(100) >= 85)
                continue;
            Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
            Assert.NotNull(frame);
            result.Captures++;
            var full = new StampBuffer(oracle.Cols, oracle.Rows);
            Assert.True(oracle.TryStampPaintSnapshot(full.Cells, full.Tables, out _, out _, out _));
            CompareRows(frame!, full, index, result);

            if (frame!.Grid.Delta is not null)
            {
                foreach (var baseline in new[] { committed, older })
                {
                    if (baseline is null)
                        continue;
                    if (ReferenceEquals(baseline.Grid.Identity, frame.Grid.Delta.BaseIdentity))
                        result.HintedPacks++;
                    var hinted = PackBytes(frame, baseline);
                    var plain = PackBytes(Unhinted(frame), baseline);
                    if (!string.Equals(hinted, plain, StringComparison.Ordinal))
                    {
                        result.PackMismatches++;
                        result.PackNotes.Add($"step {index}: hinted pack differs from the full diff");
                    }
                }
            }

            if (random.Next(100) < 80)
            {
                pane.CommitPostedPaint();
                older = committed;
                committed = frame;
            }
        }

        result.Counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter).Snapshot();
        return result;
    }

    private static void CompareRows(VtFrame frame, StampBuffer full, int step, ReplayResult result)
    {
        if (frame.Cols != full.Cols || frame.Rows != full.Rows)
        {
            result.MismatchedRows++;
            result.Mismatches.Add($"step {step}: frame {frame.Cols}x{frame.Rows}, oracle {full.Cols}x{full.Rows}");
            return;
        }

        for (var r = 0; r < frame.Rows; r++)
        {
            for (var c = 0; c < frame.Cols; c++)
            {
                var got = frame.CellAt(r, c);
                var want = full.ViewAt(r, c);
                if (got.VisuallyEquals(want))
                    continue;
                result.MismatchedRows++;
                result.Mismatches.Add(
                    $"step {step} row {r} col {c}: got '{got.Text}' fg {got.Fg} bg {got.Bg}, "
                    + $"want '{want.Text}' fg {want.Fg} bg {want.Bg}");
                break;
            }
        }
    }

    private static Script LoadScript(string source, int seed)
    {
        if (source.StartsWith("fixture:", StringComparison.Ordinal))
            return LoadFixture(source["fixture:".Length..], seed);
        return BuildStream(source["stream:".Length..]);
    }

    private static Script LoadFixture(string name, int seed)
    {
        var path = Path.Combine(VtRecordingReplay.FindRepoRoot(), FixtureDirectory, name + ".jsonl");
        var cols = 120;
        var rows = 40;
        var steps = new List<Step>();
        var random = seed == 0 ? null : new Random(seed);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("meta", out var meta))
            {
                cols = meta.GetProperty("cols").GetInt32();
                rows = meta.GetProperty("rows").GetInt32();
                continue;
            }

            var bytes = Convert.FromBase64String(root.GetProperty("chunk").GetString()!);
            if (bytes.Length == 0)
                continue;
            if (random is null || bytes.Length < 2)
            {
                steps.Add(new FeedStep(bytes));
                continue;
            }

            var start = 0;
            while (start < bytes.Length)
            {
                var take = Math.Min(bytes.Length - start, 1 + random.Next(Math.Max(1, bytes.Length / 2)));
                steps.Add(new FeedStep(bytes[start..(start + take)]));
                start += take;
            }
        }

        return new Script(cols, rows, GhosttyVtEngine.DefaultMaxScrollback, steps);
    }

    private static Script BuildStream(string name)
    {
        var steps = new List<Step>();
        void Feed(string text) => steps.Add(new FeedStep(Encoding.UTF8.GetBytes(text)));
        void Type(string text)
        {
            foreach (var rune in text.EnumerateRunes())
                Feed(rune.ToString());
        }

        switch (name)
        {
            case "scroll":
                for (var i = 0; i < 40; i++)
                {
                    Feed($"$ \u001b[32mcmd\u001b[0m {i}\r\n");
                    Feed($"out {i} {new string('-', i % 15)}\r\n");
                }

                Type("echo done");
                return new Script(40, 10, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "erase":
                for (var i = 0; i < 8; i++)
                    Feed($"row {i} {new string((char)('a' + i), 20)}\r\n");
                Feed("\u001b[3;5H\u001b[K");
                Feed("\u001b[4;5H\u001b[1K");
                Feed("\u001b[5;1H\u001b[2K");
                Feed("\u001b[2;3H\u001b[3@ins");
                Feed("\u001b[6;2H\u001b[4P");
                Feed("\u001b[7;1H\u001b[J");
                Feed("\u001b[2;10H\u001b[1J");
                Feed("\u001b[3;1H\u001b[2L");
                Feed("\u001b[1;1H\u001b[1M");
                Feed("\u001b[8;1Hlast\u001b[X");
                Feed("\u001b[2J\u001b[Hafter clear");
                Type(" typed");
                return new Script(30, 10, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "alternate-screen":
                Feed("shell line 1\r\nshell line 2\r\n$ ");
                Type("vim");
                Feed("\u001b[?1049h\u001b[H\u001b[2J");
                for (var i = 1; i <= 8; i++)
                    Feed($"\u001b[{i};1H~");
                Feed("\u001b[1;1Hhello");
                Type(" world");
                Feed("\u001b[?1049l");
                Feed("\r\n$ ");
                Type("ls");
                return new Script(30, 9, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "resize":
                Feed("first row\r\nsecond row\r\n$ ");
                Type("abc");
                steps.Add(new ResizeStep(36, 8));
                Type("def");
                Feed("\r\nmore\r\n$ ");
                steps.Add(new ResizeStep(20, 6));
                Type("ghi");
                steps.Add(new ResizeStep(28, 9));
                Type("jkl");
                return new Script(30, 7, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "scroll-region":
                for (var i = 0; i < 9; i++)
                    Feed($"\u001b[{i + 1};1Hfixed {i}");
                Feed("\u001b[3;7r\u001b[7;1H");
                for (var i = 0; i < 6; i++)
                    Feed($"\nregion {i}");
                Feed("\u001b[3;1H\u001bMup");
                Feed("\u001b[2S");
                Feed("\u001b[1T");
                Feed("\u001b[r\u001b[9;1H");
                Type("tail");
                return new Script(24, 9, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "wide-glyphs":
                Feed("漢字かな交じり\r\n");
                Feed("é ẍ\r\n");
                Feed("👍🏽 👨‍👩‍👧 🇯🇵\r\n");
                Feed("\u001b[1;2Hz");
                Feed("\u001b[2;1H漢");
                Feed("\u001b[3;1H\u001b[2P");
                Feed("\u001b[4;19H漢字");
                Type("終わり");
                return new Script(20, 6, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "scroll-offset":
                for (var i = 0; i < 30; i++)
                    Feed($"history {i}\r\n");
                Feed("$ ");
                Type("ab");
                steps.Add(new ScrollStep(3));
                Type("c");
                steps.Add(new ScrollStep(0));
                Type("de");
                steps.Add(new ScrollStep(5));
                steps.Add(new ScrollStep(0));
                Type("f");
                return new Script(24, 6, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "colours":
                Feed("\u001b[31mred\u001b[0m plain \u001b[44mblue bg\u001b[0m\r\n");
                Feed("\u001b[7minverse\u001b[0m\r\n$ ");
                Type("a");
                Feed("\u001b]11;rgb:20/30/40\u001b\\");
                Type("b");
                Feed("\u001b]4;1;rgb:00/ff/00\u001b\\");
                Type("c");
                Feed("\u001b]104\u001b\\");
                Type("d");
                Feed("\u001b]111\u001b\\");
                Feed("\u001b[?5h");
                Type("e");
                Feed("\u001b[?5l");
                Type("f");
                return new Script(30, 6, GhosttyVtEngine.DefaultMaxScrollback, steps);
            case "prompt-shrink":
                const string fancy = "\u001b[48;2;0;95;135;38;2;255;255;255;1mPROMPT\u001b[0m";
                Feed(fancy + "\r\n" + fancy);
                Feed("\r\u001b[1A\u001b[0J" + fancy);
                Type(" ls");
                Feed("\r\n" + fancy + "\r\n" + fancy);
                for (var i = 0; i < 6; i++)
                    Feed("\r\n" + fancy);
                Feed("\r\u001b[1A\u001b[0J" + fancy);
                Feed("\r\n");
                Feed(fancy);
                Type(" git status");
                return new Script(40, 8, 80, steps);
            case "prune":
                for (var i = 0; i < 400; i++)
                    Feed($"line {i} {new string('x', i % 20)}\r\n");
                Type("end");
                return new Script(30, 6, 1, steps);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }
}
