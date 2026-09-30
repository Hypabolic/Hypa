using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// GhosttyVtEngine unit tests. Skip when native asset is absent.
/// </summary>
[Collection("GhosttyPtyTests")]
public class GhosttyVtEngineTests
{
    private static readonly string? LibPath = ResolveNativeLibraryPath();

    [SkippableFact]
    public void Feed_Resize_CaptureSnapshot_Reset_Dispose()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, maxScrollback: 100, libraryPathOverride: LibPath);
        Assert.Equal(20, vt.Cols);
        Assert.Equal(6, vt.Rows);

        vt.Feed("hello\r\nworld"u8);
        var snap = vt.CaptureSnapshot();
        Assert.Equal(GhosttyVtEngine.Provider, snap.Provider);
        Assert.Equal(20, snap.Cols);
        Assert.Equal(6, snap.Rows);
        Assert.Equal("h", snap.Cells[0][0].Text);
        Assert.Equal("w", snap.Cells[1][0].Text);
        Assert.Equal("main", snap.ActiveScreen);
        Assert.NotNull(snap.Cursor);
        Assert.True(snap.Cursor!.Visible);

        var visible = vt.GetVisibleText();
        Assert.Contains("hello", visible, StringComparison.Ordinal);
        Assert.Contains("world", visible, StringComparison.Ordinal);

        vt.Resize(24, 8);
        Assert.Equal(24, vt.Cols);
        Assert.Equal(8, vt.Rows);

        vt.Reset();
        var after = vt.CaptureSnapshot();
        Assert.Equal(" ", after.Cells[0][0].Text);
        Assert.Equal(0, after.ScrollRegion.Top);
        Assert.Equal(7, after.ScrollRegion.Bottom);
    }

    [SkippableFact]
    public void Scrollback_row_count_reads_data_id_15()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, maxScrollback: 100_000, libraryPathOverride: LibPath);
        vt.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\n");
        Assert.True(vt.TryGetScrollbackExtent(out var total, out var view));
        Assert.True(vt.TryGetScrollbackRowCount(out var scrollback));
        Assert.Equal(6, view);
        Assert.True(total >= view);
        Assert.True(scrollback >= 0);
        Assert.True(total >= scrollback);
    }

    [SkippableFact]
    public void Dirty_state_survives_a_stamp_that_no_post_committed()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("dirty-rule"u8);
        var dest = new StampBuffer(20, 6);

        // First stamp reads the dirty grid, as the live capture port does.
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out var first, out _, out _, out _));
        Assert.NotEqual(GhosttyNative.RenderStateDirtyClean, first);

        // The skip path runs no capture and commits no post, so the second
        // stamp still reports dirty instead of Clean.
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out var second, out _, out _, out _));
        Assert.NotEqual(GhosttyNative.RenderStateDirtyClean, second);

        vt.CommitPostedPaint();
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out var third, out _, out _, out _));
        Assert.Equal(GhosttyNative.RenderStateDirtyClean, third);
    }

    [SkippableFact]
    public void Ground_bel_vectors_match_basic_counter_rules()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(40, 10, libraryPathOverride: LibPath);

        vt.Feed("\a\a\a");
        Assert.Equal(3, vt.TakePendingBellCount());
        Assert.Equal(0, vt.TakePendingBellCount());

        vt.Feed("\a");
        Assert.Equal(1, vt.TakePendingBellCount());
        vt.Feed("\a");
        Assert.Equal(1, vt.TakePendingBellCount());

        vt.Feed("\u001b]0;title\a");
        Assert.Equal(0, vt.TakePendingBellCount());
        vt.Feed("\a");
        Assert.Equal(1, vt.TakePendingBellCount());

        vt.Feed("\u001bPdcs\a\u001b\\");
        Assert.Equal(0, vt.TakePendingBellCount());

        vt.Feed("\u001b[\a");
        Assert.Equal(0, vt.TakePendingBellCount());
        vt.Feed("\u001b[31m\a");
        Assert.Equal(1, vt.TakePendingBellCount());

        vt.Reset();
        Assert.Equal(0, vt.TakePendingBellCount());
    }

    [SkippableFact]
    public void Sgr_bold_and_red_are_captured()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        // ESC[1;31mR ESC[0m
        vt.Feed("\u001b[1;31mR\u001b[0m");
        var snap = vt.CaptureSnapshot();
        Assert.Equal("R", snap.Cells[0][0].Text);
        Assert.True(snap.Cells[0][0].Style.Bold);
        Assert.False(string.IsNullOrEmpty(snap.Cells[0][0].Style.Fg));
        // palette:1 (red) or #RRGGBB depending on Ghostty colour path
        var fg = snap.Cells[0][0].Style.Fg!;
        Assert.True(
            fg.StartsWith("palette:", StringComparison.Ordinal)
            || (fg.StartsWith('#') && fg.Length == 7),
            "expected palette:N or #RRGGBB, got " + fg);
    }

    [SkippableFact]
    public void Alt_screen_is_reported()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("main\u001b[?1049h\u001b[HALT");
        var snap = vt.CaptureSnapshot();
        Assert.Equal("alt", snap.ActiveScreen);
        var row0 = string.Concat(snap.Cells[0].Select(c => c.IsContinuation ? "" : c.Text));
        Assert.StartsWith("ALT", row0.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal("A", snap.Cells[0][0].Text);

        vt.Feed("\u001b[?1049l");
        var main = vt.CaptureSnapshot();
        Assert.Equal("main", main.ActiveScreen);
        Assert.Equal("m", main.Cells[0][0].Text);
    }

    [SkippableFact]
    public void Scroll_region_dual_track_decstbm()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        // CSI 2;5 r → 0-based top=1 bottom=4
        vt.Feed("\u001b[2;5rtop");
        var snap = vt.CaptureSnapshot();
        Assert.Equal(1, snap.ScrollRegion.Top);
        Assert.Equal(4, snap.ScrollRegion.Bottom);
        Assert.Equal("t", snap.Cells[0][0].Text);
    }

    [SkippableFact]
    public void Scroll_region_resets_on_RIS()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[2;5r");
        Assert.Equal(1, vt.CaptureSnapshot().ScrollRegion.Top);
        Assert.Equal(4, vt.CaptureSnapshot().ScrollRegion.Bottom);

        // RIS (ESC c) must restore dual-track full-screen scroll region.
        vt.Feed("\u001bc");
        var snap = vt.CaptureSnapshot();
        Assert.Equal(0, snap.ScrollRegion.Top);
        Assert.Equal(5, snap.ScrollRegion.Bottom);
    }

    [SkippableFact]
    public void Scroll_region_resets_on_soft_reset_decstr()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[2;5r");
        Assert.Equal(1, vt.CaptureSnapshot().ScrollRegion.Top);

        // DECSTR soft reset CSI ! p
        vt.Feed("\u001b[!p");
        var snap = vt.CaptureSnapshot();
        Assert.Equal(0, snap.ScrollRegion.Top);
        Assert.Equal(5, snap.ScrollRegion.Bottom);
    }

    [SkippableFact]
    public void Scroll_region_resets_to_full_screen_on_resize()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[2;5r");
        Assert.Equal(1, vt.CaptureSnapshot().ScrollRegion.Top);

        // Policy: dual-track cannot re-query Ghostty; resize → full screen.
        vt.Resize(24, 10);
        var snap = vt.CaptureSnapshot();
        Assert.Equal(0, snap.ScrollRegion.Top);
        Assert.Equal(9, snap.ScrollRegion.Bottom);
        Assert.Equal(24, snap.Cols);
        Assert.Equal(10, snap.Rows);
    }

    [SkippableFact]
    public void Wide_char_has_width_and_continuation()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("中文");
        var snap = vt.CaptureSnapshot();
        Assert.Equal("中", snap.Cells[0][0].Text);
        Assert.Equal(2, snap.Cells[0][0].Width);
        Assert.False(snap.Cells[0][0].IsContinuation);
        // Spacer tail should follow wide cell
        Assert.True(snap.Cells[0][1].IsContinuation);
        Assert.Equal("文", snap.Cells[0][2].Text);
        Assert.Equal(2, snap.Cells[0][2].Width);
    }

    [SkippableFact]
    public void Cursor_cup_and_erase()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[3;5HX");
        var snap = vt.CaptureSnapshot();
        Assert.NotNull(snap.Cursor);
        Assert.Equal(2, snap.Cursor!.Row);
        Assert.Equal(5, snap.Cursor.Col);
        Assert.Equal("X", snap.Cells[2][4].Text);

        using var erase = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        erase.Feed("HELLO\u001b[1;3H\u001b[K");
        var eSnap = erase.CaptureSnapshot();
        Assert.Equal("H", eSnap.Cells[0][0].Text);
        Assert.Equal("E", eSnap.Cells[0][1].Text);
        // Erase to end of line from col 3 (0-based col 2)
        Assert.Equal(" ", eSnap.Cells[0][2].Text);
    }

    [SkippableFact]
    public void Text_projection_is_lossy_no_sgr()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[31mRED\u001b[0m");
        var text = vt.GetVisibleText();
        Assert.StartsWith("RED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("31", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Double_dispose_is_safe()
    {
        RequireLibrary();
        var vt = new GhosttyVtEngine(10, 4, libraryPathOverride: LibPath);
        vt.Dispose();
        vt.Dispose();
        Assert.ThrowsAny<ObjectDisposedException>(() => vt.Feed("x"u8));
    }


    [SkippableFact]
    public void Factory_default_creates_ghostty_engine()
    {
        RequireLibrary();
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            LibraryPathOverride = LibPath,
        });
        using var engine = factory.Create(40, 12);
        Assert.IsType<GhosttyVtEngine>(engine);
        Assert.Equal("ghostty", factory.ProviderWireName);
        Assert.Equal("ghostty", engine.CaptureSnapshot().Provider);
    }

    [SkippableFact]
    public void Factory_ghostty_creates_ghostty_engine()
    {
        RequireLibrary();
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
            LibraryPathOverride = LibPath,
        });
        using var engine = (GhosttyVtEngine)factory.Create(20, 6);
        Assert.Equal("ghostty", factory.ProviderWireName);
        Assert.Equal("ghostty", engine.CaptureSnapshot().Provider);
    }

    [SkippableFact]
    public void Factory_ghostty_missing_asset_fails_closed()
    {
        Skip.If(OperatingSystem.IsWindows(), "Ghostty native asset is Unix-only");

        var missing = Path.Combine(
            Path.GetTempPath(),
            "hypa-missing-ghostty-engine-" + Guid.NewGuid().ToString("N"),
            "nope.dylib");
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
            LibraryPathOverride = missing,
        });
        Assert.ThrowsAny<Exception>(() => factory.Create(20, 6));
    }

    [Fact]
    public void GhosttySelection_abi_is_64_bytes_on_64_bit()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(24, Marshal.SizeOf<GhosttyNative.GhosttyGridRef>());
        Assert.Equal(64, Marshal.SizeOf<GhosttyNative.GhosttySelection>());
        Assert.Equal(32, Marshal.SizeOf<GhosttyNative.GhosttyFormatterTerminalExtra>());
        Assert.Equal(56, Marshal.SizeOf<GhosttyNative.GhosttyFormatterTerminalOptions>());
    }

    [SkippableFact]
    public void Recent_unwrapped_ignores_soft_wrap_on_five_col_fixture()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(5, 3, maxScrollback: 100, libraryPathOverride: LibPath);
        vt.Feed("ABCDEFGHIJ");

        var recent = vt.GetRecentText(3);
        Assert.Contains("ABCDE", recent, StringComparison.Ordinal);
        Assert.Contains('\n', recent);
        var newline = recent.IndexOf('\n');
        Assert.Contains("FGHIJ", recent[(newline + 1)..], StringComparison.Ordinal);

        Assert.Equal("ABCDEFGHIJ", vt.GetRecentUnwrappedText(3));
    }

    [SkippableFact]
    public void Recent_unwrapped_keeps_spaces_at_wrap_column()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(8, 3, maxScrollback: 100, libraryPathOverride: LibPath);
        vt.Feed("hello   world");

        var recent = vt.GetRecentText(3);
        Assert.Contains('\n', recent);
        Assert.Contains("hello", recent, StringComparison.Ordinal);
        Assert.Contains("world", recent, StringComparison.Ordinal);

        Assert.Equal("hello   world", vt.GetRecentUnwrappedText(3));
    }

    [SkippableFact]
    public void Recent_unwrapped_keeps_hard_line_feed_after_resize()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(5, 3, maxScrollback: 100, libraryPathOverride: LibPath);
        vt.Feed("ABCDE\nFGHIJ");
        var before = vt.GetRecentUnwrappedText(3);
        Assert.Contains('\n', before);
        Assert.Contains("ABCDE", before, StringComparison.Ordinal);
        Assert.Contains("FGHIJ", before, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJ", before.Replace(" ", ""), StringComparison.Ordinal);

        vt.Resize(6, 3);
        var widened = vt.GetRecentUnwrappedText(3);
        Assert.Contains('\n', widened);
        Assert.Contains("ABCDE", widened, StringComparison.Ordinal);
        Assert.Contains("FGHIJ", widened, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJ", widened.Replace(" ", ""), StringComparison.Ordinal);

        vt.Resize(4, 3);
        var shrunk = vt.GetRecentUnwrappedText(3);
        Assert.Contains('\n', shrunk);
        Assert.DoesNotContain("ABCDEFGHIJ", shrunk.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void Style_get_failure_honours_raw_contract()
    {
        var raw = VtCellStyleSnapshot.Default with { Fg = "palette:33", Inverse = true };
        var resolved = VtCellStyleSnapshot.Default with { Fg = "#123456", Bg = "#000000" };
        var missRaw = GhosttyVtEngine.StyleAfterNativeGet(
            success: false, resolveColors: false, raw, _ => resolved);
        Assert.Null(missRaw.Fg);
        Assert.False(missRaw.Inverse);
        var missPaint = GhosttyVtEngine.StyleAfterNativeGet(
            success: false, resolveColors: true, raw, _ => resolved);
        Assert.Equal("#123456", missPaint.Fg);
        var hitRaw = GhosttyVtEngine.StyleAfterNativeGet(
            success: true, resolveColors: false, raw, _ => resolved);
        Assert.Equal("palette:33", hitRaw.Fg);
        Assert.True(hitRaw.Inverse);
    }

    [SkippableFact]
    public void Capture_style_get_failure_keeps_raw_default_and_paint_resolves()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[7mINV");
        vt.ForceStyleGetFailure = true;
        var raw = vt.CaptureSnapshot();
        Assert.Null(raw.Cells[0][0].Style.Fg);
        Assert.False(raw.Cells[0][0].Style.Inverse);
        Assert.Null(raw.Cells[0][4].Style.Fg);
        Assert.False(raw.Cells[0][4].Style.Inverse);

        vt.ForceStyleGetFailure = true;
        var paint = vt.CapturePaintSnapshot();
        Assert.Null(paint.Cells[0][0].Style.Fg);
        Assert.Null(paint.Cells[0][0].Style.Bg);
        Assert.False(paint.Cells[0][0].Style.Inverse);
    }

    [SkippableFact]
    public void Capture_paint_resolves_content_fg_bg_and_native_attrs()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(40, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[5mBLINK\u001b[0m \u001b[53mOVER\u001b[0m \u001b[58;2;1;2;3m\u001b[4mUND");
        var paint = vt.CapturePaintSnapshot();
        Assert.True(vt.ReadRowCellsRgbCalls > 0);
        Assert.True(vt.ReadContentBgTokenCalls > 0);
        Assert.True(TryFindToken(paint, "DIR", out var dir));
        Assert.StartsWith("palette:", dir.Fg, StringComparison.Ordinal);
        Assert.True(TryFindToken(paint, "BLINK", out var blink));
        Assert.True(blink.Blink);
        Assert.True(TryFindToken(paint, "OVER", out var over));
        Assert.True(over.Overline);
        Assert.True(TryFindToken(paint, "UND", out var und));
        Assert.True(und.Underline);
        Assert.False(string.IsNullOrEmpty(und.UnderlineColor));
        Assert.StartsWith("#", und.UnderlineColor, StringComparison.Ordinal);
        Assert.True(und.UnderlineStyle >= 1);
    }

    [SkippableFact]
    public void Capture_paint_unstyled_cells_use_pane_defaults_hex()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("plain");
        var paint = vt.CapturePaintSnapshot();
        var style = paint.Cells[0][0].Style;
        Assert.True(string.IsNullOrEmpty(style.Fg));
        Assert.True(string.IsNullOrEmpty(style.Bg));
        Assert.False(style.Inverse);
        var blank = paint.Cells[1][0].Style;
        Assert.True(string.IsNullOrEmpty(blank.Fg));
        Assert.True(string.IsNullOrEmpty(blank.Bg));
    }

    [SkippableFact]
    public void Capture_paint_osc4_override_and_inverse_at_capture()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b]4;33;rgb:12/34/56\u001b\\\u001b[38;5;33mDIR\u001b[0m \u001b[7mINV");
        var paint = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(paint, "DIR", out var dir));
        Assert.Equal("#123456", dir.Fg);
        Assert.True(TryFindToken(paint, "INV", out var inv));
        Assert.False(inv.Inverse);
        Assert.StartsWith("#", inv.Fg, StringComparison.Ordinal);
        Assert.StartsWith("#", inv.Bg, StringComparison.Ordinal);
        Assert.NotEqual(inv.Fg, inv.Bg);
    }


    [SkippableFact]
    public void Capture_paint_bce_content_bg_is_not_none()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("plain");
        var plain = vt.CapturePaintSnapshot();
        var defaultBg = plain.Cells[0][0].Style.Bg;
        Assert.True(string.IsNullOrEmpty(defaultBg));

        using var bce = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        bce.Feed("\u001b[48;2;20;40;60m\u001b[2J\u001b[H          ");
        var paint = bce.CapturePaintSnapshot();
        Assert.True(bce.ReadRowCellsRgbCalls > 0);
        Assert.True(bce.ReadContentBgTokenCalls > 0);
        var bg = paint.Cells[0][3].Style.Bg;
        Assert.False(string.IsNullOrEmpty(bg));
        Assert.DoesNotContain("palette:", bg, StringComparison.Ordinal);
        Assert.StartsWith("#", bg, StringComparison.Ordinal);
        Assert.Equal("#14283C", bg);
        Assert.NotEqual(defaultBg, bg);
    }

    [SkippableFact]
    public void Capture_paint_truecolor_segments_keep_adjacent_fg_bg()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;2;10;20;30;48;2;40;50;60mAB\u001b[38;2;70;80;90;48;2;100;110;120mCD");
        var paint = vt.CapturePaintSnapshot();
        Assert.True(vt.ReadRowCellsRgbCalls > 0);
        Assert.True(vt.ReadContentBgTokenCalls > 0);
        var a = paint.Cells[0][0].Style;
        var c = paint.Cells[0][2].Style;
        Assert.Equal("#0A141E", a.Fg);
        Assert.Equal("#28323C", a.Bg);
        Assert.Equal("#46505A", c.Fg);
        Assert.Equal("#646E78", c.Bg);
        Assert.NotEqual(a.Fg, c.Fg);
        Assert.NotEqual(a.Bg, c.Bg);
    }

    [SkippableFact]
    public void Capture_paint_p10k_segments_are_not_a_flat_bar()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(40, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[48;2;0;0;128;38;2;255;255;255mp10k\u001b[48;2;0;128;0;38;2;0;0;0mnext");
        var paint = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(paint, "p10k", out var bar));
        Assert.True(TryFindToken(paint, "next", out var next));
        Assert.False(string.IsNullOrEmpty(bar.Fg));
        Assert.False(string.IsNullOrEmpty(bar.Bg));
        Assert.False(string.IsNullOrEmpty(next.Fg));
        Assert.False(string.IsNullOrEmpty(next.Bg));
        Assert.NotEqual(bar.Bg, next.Bg);
        Assert.NotEqual(bar.Fg, next.Fg);
        Assert.Equal("#000080", bar.Bg);
        Assert.Equal("#FFFFFF", bar.Fg);
        Assert.Equal("#008000", next.Bg);
        Assert.Equal("#000000", next.Fg);
    }

    [SkippableFact]
    public void Capture_raw_snapshot_keeps_palette_tags()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[7mINV");
        var raw = vt.CaptureSnapshot();
        Assert.True(TryFindToken(raw, "DIR", out var dir));
        Assert.StartsWith("palette:", dir.Fg, StringComparison.Ordinal);
        Assert.True(TryFindToken(raw, "INV", out var inv));
        Assert.True(inv.Inverse);
        Assert.Null(inv.Fg);
    }

    [SkippableFact]
    public void Directory_palette_and_inverse_resolve_to_rgb_on_paint()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[7mINV");
        var raw = vt.CaptureSnapshot();
        Assert.StartsWith("palette:", raw.Cells[0][0].Style.Fg, StringComparison.Ordinal);
        Assert.True(raw.Cells[0][4].Style.Inverse);

        var paint = vt.CapturePaintSnapshot();
        Assert.StartsWith("palette:", paint.Cells[0][0].Style.Fg, StringComparison.Ordinal);
        Assert.False(paint.Cells[0][4].Style.Inverse);
        Assert.False(string.IsNullOrEmpty(paint.Cells[0][4].Style.Fg));
        Assert.False(string.IsNullOrEmpty(paint.Cells[0][4].Style.Bg));
        Assert.StartsWith("#", paint.Cells[0][4].Style.Fg, StringComparison.Ordinal);
        Assert.StartsWith("#", paint.Cells[0][4].Style.Bg, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Attach_paint_snapshot_updates_render_state_before_colour_resolve()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[7mINV");
        var paint = vt.CapturePaintSnapshot();
        Assert.StartsWith("palette:", paint.Cells[0][0].Style.Fg, StringComparison.Ordinal);
        Assert.False(paint.Cells[0][4].Style.Inverse);
        Assert.StartsWith("#", paint.Cells[0][4].Style.Fg, StringComparison.Ordinal);
        Assert.StartsWith("#", paint.Cells[0][4].Style.Bg, StringComparison.Ordinal);
        Assert.NotEqual(paint.Cells[0][4].Style.Fg, paint.Cells[0][4].Style.Bg);
    }

    [SkippableFact]
    public void Stamp_unstyled_cells_keep_packed_zero()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("plain");
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out _, out _));
        Assert.Equal("p", dest[0][0].Text);
        Assert.Equal(0u, dest[0][0].FgPacked);
        Assert.Equal(0u, dest[0][0].BgPacked);
        Assert.Equal(0u, dest[1][0].FgPacked);
        Assert.Equal(0u, dest[1][0].BgPacked);
    }

    [SkippableFact]
    public void Stamp_indexed_foreground_stays_palette_tag()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR");
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out _, out _));
        Assert.Equal("D", dest[0][0].Text);
        Assert.Equal(VtColorPack.FromPalette(33), dest[0][0].FgPacked);
        Assert.False(VtColorPack.IsRgb(dest[0][0].FgPacked));
        var paint = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(paint, "DIR", out var dir));
        Assert.StartsWith("palette:", dir.Fg, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Capture_paint_inverse_resolves_unset_before_swap()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[7mINV");
        var paint = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(paint, "INV", out var inv));
        Assert.False(inv.Inverse);
        Assert.StartsWith("#", inv.Fg, StringComparison.Ordinal);
        Assert.StartsWith("#", inv.Bg, StringComparison.Ordinal);
        Assert.NotEqual(inv.Fg, inv.Bg);
    }

    [SkippableFact]
    public void Child_osc11_default_background_emits_rgb()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b]11;rgb:44/55/66\u001b\\\u001b]10;rgb:11/22/33\u001b\\plain");
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out _, out _));
        Assert.Equal("p", dest[0][0].Text);
        Assert.NotEqual(0u, dest[0][0].BgPacked);
        Assert.True(VtColorPack.IsRgb(dest[0][0].BgPacked));
        Assert.NotEqual(0u, dest[0][0].FgPacked);
        Assert.True(VtColorPack.IsRgb(dest[0][0].FgPacked));
        var paint = vt.CapturePaintSnapshot();
        Assert.StartsWith("#", paint.Cells[0][0].Style.Bg, StringComparison.Ordinal);
        Assert.StartsWith("#", paint.Cells[0][0].Style.Fg, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Set_default_palette_merges_host_entries()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        var untouched = vt.AppliedDefaultPalette[6];
        var host = default(HostPalette)
            .WithColor(5, new HostRgb(1, 2, 3))
            .WithColor(255, new HostRgb(9, 8, 7));
        vt.SetDefaultPalette(host);
        Assert.Equal(new GhosttyCellStyle.Rgb(1, 2, 3), vt.AppliedDefaultPalette[5]);
        Assert.Equal(new GhosttyCellStyle.Rgb(9, 8, 7), vt.AppliedDefaultPalette[255]);
        Assert.Equal(untouched, vt.AppliedDefaultPalette[6]);
        Assert.NotEqual(new GhosttyCellStyle.Rgb(1, 2, 3), vt.AppliedDefaultPalette[6]);
    }

    [SkippableFact]
    public void Child_osc4_redefinition_emits_rgb_other_indices_stay_tagged()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b]4;33;rgb:12/34/56\u001b\\\u001b[38;5;33mA\u001b[38;5;1mB");
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out _, out _));
        Assert.Equal("A", dest[0][0].Text);
        Assert.True(VtColorPack.IsRgb(dest[0][0].FgPacked));
        Assert.Equal(VtColorPack.FromRgb(0x12, 0x34, 0x56), dest[0][0].FgPacked);
        Assert.Equal("B", dest[0][1].Text);
        Assert.Equal(VtColorPack.FromPalette(1), dest[0][1].FgPacked);
        Assert.False(VtColorPack.IsRgb(dest[0][1].FgPacked));
    }

    [SkippableFact]
    public void Capture_paint_bce_truecolor_background_stays_rgb()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("\u001b[48;2;20;40;60m\u001b[2J\u001b[H          ");
        var paint = vt.CapturePaintSnapshot();
        Assert.Equal("#14283C", paint.Cells[0][3].Style.Bg);
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out _, out _));
        Assert.Equal(VtColorPack.FromRgb(0x14, 0x28, 0x3C), dest[0][3].BgPacked);
    }

    [SkippableFact]
    public void Hidden_cursor_survives_compact_pack_assemble_paint()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25l"u8);
        var snap = vt.CapturePaintSnapshot();
        Assert.NotNull(snap.Cursor);
        Assert.False(snap.Cursor!.Visible);
        Assert.Equal(0, snap.Cursor.Col);
        Assert.Equal(0, snap.Cursor.Row);

        var json = VtSnapshotNormalizer.ToCompactJson(snap);
        Assert.Contains("\"visible\":false", json, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(json))
        {
            var cursor = doc.RootElement.GetProperty("cursor");
            Assert.True(cursor.TryGetProperty("visible", out var visible));
            Assert.False(visible.GetBoolean());
        }

        var parts = AttachSnapshotPacker.Pack(
            "p1", AttachSnapshotPacker.ParseSnapshotJson("p1", json), new DefaultEventPayloadRedactor());
        var assembler = new SnapshotAssembler();
        AssembledSnapshot? frame = null;
        foreach (var part in parts)
        {
            var slice = JsonSerializer.Deserialize(
                part, EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);
            Assert.NotNull(slice);
            assembler.TryAdd(slice, out frame);
        }

        Assert.NotNull(frame);
        Assert.True(frame!.Cursor.HasCursor);
        Assert.False(frame.Cursor.Visible);
        Assert.Equal(0, frame.Cursor.Col);
        Assert.Equal(0, frame.Cursor.Row);
        var painted = SnapshotPainter.Paint(frame);
        Assert.EndsWith(SnapshotPainter.HideCursor, painted);
        Assert.DoesNotContain(SnapshotPainter.ShowCursor, painted, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Paint_snapshot_hidden_cursor_uses_render_state_visibility()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25l"u8);
        var snap = vt.CapturePaintSnapshot();
        Assert.NotNull(snap.Cursor);
        Assert.False(snap.Cursor!.Visible);
        Assert.Equal(0, snap.Cursor.Col);
        Assert.Equal(0, snap.Cursor.Row);
    }

    [SkippableFact]
    public void Paint_snapshot_visible_cursor_uses_render_state_viewport()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25h\u001b[3;5H"u8);
        var snap = vt.CapturePaintSnapshot();
        Assert.NotNull(snap.Cursor);
        Assert.True(snap.Cursor!.Visible);
        Assert.Equal(4, snap.Cursor.Col);
        Assert.Equal(2, snap.Cursor.Row);
    }

    [SkippableFact]
    public void Paint_snapshot_scroll_off_viewport_has_no_host_cursor()
    {
        RequireLibrary();
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25h"u8);
        for (var i = 0; i < 20; i++)
            vt.Feed($"hist-{i}\r\n");
        vt.Feed("live-edge\r\n");
        Assert.True(vt.TryGetScrollMetrics(out _, out var maxOffset));
        Assert.True(maxOffset > 0);
        Assert.Equal(maxOffset, vt.SetScrollOffsetFromBottom(maxOffset));
        var scrolled = vt.CapturePaintSnapshot();
        Assert.Null(scrolled.Cursor);
    }

    [Fact]
    public void Failed_render_state_cursor_get_is_not_visible()
    {
        Assert.False(GhosttyVtEngine.TryRenderStateGetBool(GhosttyNative.InvalidValue, 1, out var visible));
        Assert.False(visible);
        Assert.False(GhosttyVtEngine.TryRenderStateGetBool(GhosttyNative.OutOfMemory, 1, out visible));
        Assert.False(visible);
        Assert.True(GhosttyVtEngine.TryRenderStateGetBool(GhosttyNative.Success, 1, out visible));
        Assert.True(visible);
        Assert.True(GhosttyVtEngine.TryRenderStateGetBool(GhosttyNative.Success, 0, out visible));
        Assert.False(visible);
        Assert.False(GhosttyVtEngine.TryRenderStateGetU16(GhosttyNative.InvalidValue, 7, out var coord));
        Assert.Equal(0, coord);
    }

    [Fact]
    public void Failed_render_state_update_does_not_read_paint_cursor()
    {
        Assert.False(GhosttyVtEngine.ShouldReadPaintCursor(includeCursorShape: true, renderStateUpdateOk: false));
        Assert.False(GhosttyVtEngine.ShouldReadPaintCursor(includeCursorShape: false, renderStateUpdateOk: true));
        Assert.True(GhosttyVtEngine.ShouldReadPaintCursor(includeCursorShape: true, renderStateUpdateOk: true));
    }

    [SkippableFact]
    public void Failed_render_state_update_emits_no_host_cursor()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25h\u001b[3;5H"u8);
        var ok = vt.CapturePaintSnapshot();
        Assert.NotNull(ok.Cursor);
        Assert.True(ok.Cursor!.Visible);
        Assert.Equal(4, ok.Cursor.Col);
        Assert.Equal(2, ok.Cursor.Row);

        vt.ForceRenderStateUpdateFailure = true;
        var failed = vt.CapturePaintSnapshot();
        Assert.Null(failed.Cursor);

        Assert.True(vt.TryCaptureLivePaint(out var live, out _, out _));
        Assert.Null(live.Cursor);

        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out var stampCursor, out _, out _));
        Assert.False(stampCursor.HasCursor);
        Assert.True(vt.TryStampPaintSnapshot(dest.Cells, dest.Tables, out stampCursor, out _, out _));
        Assert.False(stampCursor.HasCursor);
    }



    [SkippableFact]
    public void Live_stamp_hidden_dectcem_keeps_some_cursor()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25h\u001b[3;5H"u8);
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out var shown, out _, out _));
        Assert.True(shown.HasCursor);
        Assert.True(shown.Visible);
        Assert.Equal(4, shown.Col);
        Assert.Equal(2, shown.Row);

        vt.Feed("\u001b[?25l"u8);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out var hidden, out _, out _));
        Assert.True(hidden.HasCursor);
        Assert.False(hidden.Visible);
        Assert.Equal(4, hidden.Col);
        Assert.Equal(2, hidden.Row);
    }

    [SkippableFact]
    public void Live_stamp_scroll_off_has_no_cursor()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, maxScrollback: 200, libraryPathOverride: LibPath);
        for (var i = 0; i < 30; i++)
            vt.Feed($"hist-{i}\r\n");
        Assert.True(vt.TryGetScrollMetrics(out _, out var maxOffset));
        Assert.True(maxOffset > 0);
        Assert.Equal(maxOffset, vt.SetScrollOffsetFromBottom(maxOffset));
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        Assert.True(vt.TryStampPaintSnapshot(dest.Cells, dest.Tables, out var cursor, out _, out _));
        Assert.False(cursor.HasCursor);
    }

    [SkippableFact]
    public void Live_stamp_copies_sync_and_mouse_encoding()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        var dest = new StampBuffer(vt.Cols, vt.Rows);
        vt.Feed("\u001b[?25h"u8);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out var off, out _));
        Assert.False(off.SynchronizedOutput);
        Assert.False(vt.IsSynchronizedOutputActive);

        vt.Feed("\u001b[?2026h"u8);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out var onCursor, out var on, out _));
        Assert.True(on.SynchronizedOutput);
        Assert.True(vt.IsSynchronizedOutputActive);
        Assert.True(onCursor.HasCursor);
        Assert.True(onCursor.Visible);

        vt.Feed("\u001b[?2026l\u001b[?1003h\u001b[?1006h"u8);
        Assert.True(vt.TryStampLivePaint(dest.Cells, dest.Tables, out _, out _, out var mouse, out _));
        Assert.False(mouse.SynchronizedOutput);
        Assert.False(vt.IsSynchronizedOutputActive);
        Assert.Equal("any", mouse.Mouse);
        Assert.Equal("sgr", mouse.MouseEncoding);
    }

    [SkippableFact]
    public void Synchronized_output_sets_modes_sync_without_hiding_dectcem()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 6, libraryPathOverride: LibPath);
        vt.Feed("\u001b[?25h\u001b[?2026h"u8);
        var on = vt.CapturePaintSnapshot();
        Assert.True(on.Modes.Sync);
        Assert.NotNull(on.Cursor);
        Assert.True(on.Cursor!.Visible);
        vt.Feed("\u001b[?2026l"u8);
        var off = vt.CapturePaintSnapshot();
        Assert.False(off.Modes.Sync);
        Assert.NotNull(off.Cursor);
        Assert.True(off.Cursor!.Visible);
        vt.Feed("\u001b[?25l\u001b[?2026h"u8);
        var hidden = vt.CapturePaintSnapshot();
        Assert.True(hidden.Modes.Sync);
        Assert.NotNull(hidden.Cursor);
        Assert.False(hidden.Cursor!.Visible);
    }

    [SkippableFact]
    public void Live_paint_reports_clean_partial_or_full()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        Assert.True(vt.TryCaptureLivePaint(out _, out var firstDirty, out _));
        vt.Feed("abc");
        Assert.True(vt.TryCaptureLivePaint(out var snap, out var dirty, out var rows));
        Assert.True(
            dirty is GhosttyNative.RenderStateDirtyClean
                or GhosttyNative.RenderStateDirtyPartial
                or GhosttyNative.RenderStateDirtyFull,
            "dirty kind must be clean, partial, or full");
        if (dirty == GhosttyNative.RenderStateDirtyPartial)
            Assert.NotEmpty(rows!);
        Assert.Equal("a", snap.Cells[0][0].Text);
        _ = firstDirty;
    }

    [SkippableFact]
    public void Live_paint_keeps_dirty_until_commit()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("abc");
        Assert.True(vt.TryCaptureLivePaint(out var first, out var dirty, out _));
        Assert.NotEqual(GhosttyNative.RenderStateDirtyClean, dirty);
        Assert.Equal("a", first.Cells[0][0].Text);

        Assert.True(vt.TryCaptureLivePaint(out var second, out var stillDirty, out _));
        Assert.NotEqual(GhosttyNative.RenderStateDirtyClean, stillDirty);
        Assert.Equal("a", second.Cells[0][0].Text);

        vt.CommitPostedPaint();
        Assert.True(vt.TryCaptureLivePaint(out _, out var afterCommit, out _));
        Assert.Equal(GhosttyNative.RenderStateDirtyClean, afterCommit);
    }

    [SkippableFact]
    public async Task Live_mouse_only_decset_packs_cells_token_and_stops_forward()
    {
        RequireLibrary();
        const int cols = 20;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = new PaneId("p1"),
                Cwd = Path.GetTempPath(),
                Command = "echo",
                Cols = cols,
                Rows = rows,
            },
            vt: vt,
            ptyFactory: TestPaneFactories.ProcessIo());

        vt.Feed("ready");
        Assert.True(pane.TryCaptureLivePaintFrame(out var first, out _, out var firstKind));
        Assert.Equal(PaneVtPaintKind.Full, firstKind);
        Assert.NotNull(first);
        Assert.Equal("none", first!.Modes.Mouse);
        pane.CommitPostedPaint();

        vt.Feed("\u001b[?1003h\u001b[?1006h");
        Assert.True(vt.TryCaptureLivePaint(out var liveAny, out var dirtyAny, out _));
        Assert.Equal(GhosttyNative.RenderStateDirtyClean, dirtyAny);
        Assert.Equal("any", liveAny.Modes.Mouse);
        Assert.Equal("sgr", liveAny.Modes.MouseEncoding);
        Assert.True(pane.TryCaptureLivePaintFrame(out var anyFrame, out _, out var anyKind));
        Assert.Equal(PaneVtPaintKind.Clean, anyKind);
        Assert.NotNull(anyFrame);
        Assert.Equal("any", anyFrame!.Modes.Mouse);
        Assert.Equal("sgr", anyFrame.Modes.MouseEncoding);

        var packedCapturedAny = VtCellPacker.Prepare(
            first with { Modes = anyFrame.Modes, Generation = 2 },
            first,
            dirtyRows: []);
        Assert.NotNull(packedCapturedAny);
        Assert.False(packedCapturedAny.Value.Full);
        Assert.Empty(packedCapturedAny.Value.Rows);
        Assert.Equal("any", packedCapturedAny.Value.Mouse);
        Assert.Equal("sgr", packedCapturedAny.Value.MouseEncoding);

        var geo = MouseLiveGeometry("p1");
        var live = MouseLiveState(geo);
        var box = geo.Panes[0].Content;
        var admitted = MouseBoxFrame(box, first.Modes, generation: 1);
        using var capture = new MemoryStream();
        using var tty = new UnixRawTerminal(capture, 80, 24);
        tty.EnterClientOverlay();
        var firstPacked = VtCellPacker.Prepare(admitted, baseline: null, dirtyRows: null);
        Assert.NotNull(firstPacked);
        var firstPayload = VtCellPacker.ToPayload(firstPacked.Value, "p1", 1, 0, 1) with
        {
            Full = true,
            Reanchor = true,
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, firstPayload, reanchorBeforePaint: true));
        Assert.Equal(PaneMouseMode.None, AttachSession.ReadMouseMode(live, "p1"));
        var anyWrite = admitted with { Modes = anyFrame.Modes, Generation = 2 };
        var packedAny = VtCellPacker.Prepare(anyWrite, admitted, dirtyRows: []);
        Assert.NotNull(packedAny);
        var anyPayload = VtCellPacker.ToPayload(packedAny.Value, "p1", 2, 1, 1);
        Assert.True(AttachSession.TryWriteCells(tty, live, anyPayload, reanchorBeforePaint: true));
        Assert.Equal(PaneMouseMode.Any, AttachSession.ReadMouseMode(live, "p1"));
        var pressEv = new MouseEvent(
            MouseButton.Left, MouseAction.Press, box.Col, box.Row);
        var press = live.Mouse.Feed(pressEv, AttachSession.MouseFeedContextFor(live, "p1"));
        Assert.DoesNotContain(press, r => r.Kind is MouseCommandKind.ForwardSgr);
        var releaseEv = new MouseEvent(
            MouseButton.Left, MouseAction.Release, box.Col, box.Row);
        var forward = live.Mouse.Feed(releaseEv, AttachSession.MouseFeedContextFor(live, "p1"));
        Assert.Contains(forward, r => r.Kind is MouseCommandKind.ForwardSgr);
        live.Mouse.Reset();
        pane.CommitPostedPaint();

        vt.Feed("\u001b[?1003l\u001b[?1006l");
        Assert.True(vt.TryCaptureLivePaint(out var liveNone, out var dirtyNone, out _));
        Assert.Equal(GhosttyNative.RenderStateDirtyClean, dirtyNone);
        Assert.Equal("none", liveNone.Modes.Mouse);
        Assert.True(pane.TryCaptureLivePaintFrame(out var noneFrame, out _, out var noneKind));
        Assert.Equal(PaneVtPaintKind.Clean, noneKind);
        Assert.NotNull(noneFrame);
        Assert.Equal("none", noneFrame!.Modes.Mouse);

        var packedCapturedNone = VtCellPacker.Prepare(
            first with { Modes = noneFrame.Modes, Generation = 3 },
            first with { Modes = anyFrame.Modes, Generation = 2 },
            dirtyRows: []);
        Assert.NotNull(packedCapturedNone);
        Assert.Equal("none", packedCapturedNone.Value.Mouse);
        var noneWrite = anyWrite with { Modes = noneFrame.Modes, Generation = 3 };
        var packedNone = VtCellPacker.Prepare(noneWrite, anyWrite, dirtyRows: []);
        Assert.NotNull(packedNone);
        Assert.False(packedNone.Value.Full);
        Assert.Empty(packedNone.Value.Rows);
        Assert.Equal("none", packedNone.Value.Mouse);
        var nonePayload = VtCellPacker.ToPayload(packedNone.Value, "p1", 3, 2, 1);
        Assert.True(AttachSession.TryWriteCells(tty, live, nonePayload, reanchorBeforePaint: true));
        Assert.Equal(PaneMouseMode.None, AttachSession.ReadMouseMode(live, "p1"));
        var blocked = live.Mouse.Feed(pressEv, AttachSession.MouseFeedContextFor(live, "p1"));
        Assert.DoesNotContain(blocked, r => r.Kind is MouseCommandKind.ForwardSgr);
    }

    [SkippableFact]
    public async Task Live_paint_pack_applies_into_retained_frame()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(8, 4, libraryPathOverride: LibPath);
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "echo",
                Cols = 8,
                Rows = 4,
            },
            vt: vt,
            ptyFactory: TestPaneFactories.ProcessIo());

        vt.Feed("AAA");
        Assert.True(pane.TryCaptureLivePaintJson(out var fullJson, out _, out var kind, out _));
        Assert.NotEqual(PaneVtPaintKind.Clean, kind);
        Assert.False(string.IsNullOrEmpty(fullJson));
        pane.CommitPostedPaint();

        var assembler = new SnapshotAssembler();
        AssembledSnapshot? frame = null;
        foreach (var part in AttachSnapshotPacker.Pack(
            "p1", AttachSnapshotPacker.ParseSnapshotJson("p1", fullJson),
            new DefaultEventPayloadRedactor(), generation: 1))
        {
            var slice = JsonSerializer.Deserialize(
                part, EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);
            Assert.NotNull(slice);
            assembler.TryAdd(slice, out frame);
        }

        Assert.NotNull(frame);
        Assert.Equal("A", frame!.Cells[0][0].Text);

        vt.Feed("\rZZZ");
        Assert.True(pane.TryCaptureLivePaintJson(
            out var liveJson, out _, out var liveKind, out var dirtyRows));
        Assert.False(string.IsNullOrEmpty(liveJson));
        var packDirty = liveKind == PaneVtPaintKind.Patch ? dirtyRows : new[] { 0 };
        var parts = AttachSnapshotPacker.Pack(
            "p1",
            AttachSnapshotPacker.ParseSnapshotJson("p1", liveJson),
            new DefaultEventPayloadRedactor(),
            generation: 2,
            dirtyRows: packDirty);
        Assert.NotEmpty(parts);
        if (liveKind == PaneVtPaintKind.Patch)
        {
            Assert.All(parts, part =>
            {
                using var doc = JsonDocument.Parse(part);
                Assert.True(doc.RootElement.GetProperty("patch").GetBoolean());
            });
            using var last = JsonDocument.Parse(parts[^1]);
            Assert.True(last.RootElement.GetProperty("complete").GetBoolean());
        }

        foreach (var part in parts)
        {
            var slice = JsonSerializer.Deserialize(
                part, EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);
            Assert.NotNull(slice);
            assembler.TryAdd(slice, out frame);
        }

        Assert.NotNull(frame);
        Assert.Equal("Z", frame!.Cells[0][0].Text);
    }

    [SkippableFact]
    public async Task Scrolled_paint_keeps_history_styles_off_live_prompt()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(24, 6, maxScrollback: 200, libraryPathOverride: LibPath);
        vt.Feed("\u001b[31mZXQ\u001b[0m\r\n");
        for (var i = 0; i < 20; i++)
            vt.Feed($"hist-{i}\r\n");
        vt.Feed("\u001b[1;32mDIR\u001b[0m\r\n");
        vt.Feed("\u001b[44;37mp10k\u001b[0m");

        var live = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(live, "DIR", out var liveDir));
        Assert.True(TryFindToken(live, "p10k", out var liveBar));
        Assert.False(string.IsNullOrEmpty(liveDir.Fg));
        Assert.False(string.IsNullOrEmpty(liveBar.Bg));

        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "echo",
                Cols = 24,
                Rows = 6,
            },
            vt: vt,
            ptyFactory: TestPaneFactories.ProcessIo());

        Assert.True(pane.TryGetScrollMetrics(out var origin, out var maxOffset));
        Assert.Equal(0, origin);
        Assert.True(maxOffset > 0);
        Assert.True(pane.TrySetScrollOrigin(maxOffset));
        Assert.True(pane.TryCaptureSnapshotJson(out var json, out _));

        using var doc = JsonDocument.Parse(json);
        var cells = doc.RootElement.GetProperty("cells");
        Assert.True(TryFindToken(cells, "ZXQ", out var histRow, out var histCol));
        var histStyle = cells[histRow][histCol].TryGetProperty("style", out var st) ? st : default;
        var histFg = histStyle.ValueKind == JsonValueKind.Object
            && histStyle.TryGetProperty("fg", out var fg)
            && fg.ValueKind == JsonValueKind.String
            ? fg.GetString()
            : null;
        var histBg = histStyle.ValueKind == JsonValueKind.Object
            && histStyle.TryGetProperty("bg", out var bg)
            && bg.ValueKind == JsonValueKind.String
            ? bg.GetString()
            : null;
        Assert.NotEqual(liveDir.Fg, histFg);
        Assert.NotEqual(liveBar.Bg, histBg);
        Assert.False(
            histStyle.ValueKind == JsonValueKind.Object
            && histStyle.TryGetProperty("bold", out var bold)
            && bold.ValueKind == JsonValueKind.True,
            "history text must not carry the live prompt bold");
    }

    [SkippableFact]
    public async Task Prompt_rewrite_through_pane_then_scrolled_recapture_keeps_transient_styles()
    {
        RequireLibrary();
        const int cols = 40;
        const int rows = 8;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        var pty = new InjectedPtyProcess();
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            vt: vt,
            spawnProcess: (_, _) => pty);

        await pane.StartAsync(CancellationToken.None);

        // src/ghostty/mod.rs:867-872 ghostty_terminal_vt_write. A p10k
        // transient prompt is VT state. Wheel only moves the origin
        // (src/pane/terminal.rs:224-237, 2741-2755).
        const string fancy = "\u001b[48;2;0;95;135;38;2;255;255;255;1mPROMPT \u001b[0m";
        pty.Emit(Encoding.UTF8.GetBytes(fancy + "ls"));
        await WaitUntilAsync(() =>
        {
            var snap = vt.CapturePaintSnapshot();
            return TryFindToken(snap, "PROMPT", out var style)
                && !string.IsNullOrEmpty(style.Bg)
                && style.Bold;
        });
        var before = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(before, "PROMPT", out var powerline));
        Assert.False(string.IsNullOrEmpty(powerline.Bg));
        Assert.True(powerline.Bold);
        Assert.True(pane.FeedGeneration > 0, "prompt bytes must enter Ghostty through the pane PTY read loop");

        var rest = new StringBuilder();
        rest.Append("\r\u001b[0m\u001b[2Kold $ ls\r\n");
        rest.Append("file.txt\r\n");
        for (var i = 0; i < 20; i++)
            rest.Append($"hist-{i}\r\n");
        rest.Append(fancy);
        pty.Emit(Encoding.UTF8.GetBytes(rest.ToString()));
        await WaitUntilAsync(() =>
        {
            var snap = vt.CapturePaintSnapshot();
            return TryFindToken(snap, "PROMPT", out _)
                && TryFindToken(snap, "hist-19", out _);
        });

        var live = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(live, "PROMPT", out var liveBar));
        Assert.Equal(powerline.Bg, liveBar.Bg, StringComparer.OrdinalIgnoreCase);
        Assert.True(liveBar.Bold);
        Assert.Equal(1, CountRowsContaining(live, "PROMPT"));
        Assert.False(TryFindToken(live, "old $ ls", out _));

        Assert.True(pane.TryGetScrollMetrics(out var origin, out var maxOffset));
        Assert.Equal(0, origin);
        Assert.True(maxOffset > 0);
        Assert.True(pane.TrySetScrollOrigin(maxOffset));
        Assert.True(pane.TryCaptureSnapshotJson(out var json, out _));

        using var doc = JsonDocument.Parse(json);
        var cells = doc.RootElement.GetProperty("cells");
        Assert.True(TryFindToken(cells, "old $ ls", out var histRow, out var histCol));
        var histStyle = cells[histRow][histCol].TryGetProperty("style", out var st) ? st : default;
        var histBg = histStyle.ValueKind == JsonValueKind.Object
            && histStyle.TryGetProperty("bg", out var bg)
            && bg.ValueKind == JsonValueKind.String
            ? bg.GetString()
            : null;
        Assert.False(
            string.Equals(histBg, powerline.Bg, StringComparison.OrdinalIgnoreCase),
            "rewritten prompt must not keep the live powerline background");
        Assert.False(
            histStyle.ValueKind == JsonValueKind.Object
            && histStyle.TryGetProperty("bold", out var histBold)
            && histBold.ValueKind == JsonValueKind.True,
            "rewritten prompt must not keep the live powerline bold");
        Assert.False(TryFindToken(cells, "PROMPT", out _, out _));

        var parts = AttachSnapshotPacker.Pack(
            "p1", AttachSnapshotPacker.ParseSnapshotJson("p1", json),
            new DefaultEventPayloadRedactor(), generation: 1);
        var assembler = new SnapshotAssembler();
        AssembledSnapshot? frame = null;
        foreach (var part in parts)
        {
            var slice = JsonSerializer.Deserialize(
                part, EventPayloadJsonContext.Default.TerminalRenderSnapshotPayload);
            Assert.NotNull(slice);
            assembler.TryAdd(slice, out frame);
        }

        Assert.NotNull(frame);
        var host = new HostFrame();
        host.Resize(80, 24);
        var box = new CellRect(10, 2, cols, rows);
        host.StampSnapshot(frame!, box);
        var stamped = new StringBuilder();
        for (var i = 0; i < 8; i++)
            stamped.Append(host.CellAt(box.Col + i, box.Row + histRow).Text);
        Assert.Equal("old $ ls", stamped.ToString());
        Assert.NotEqual(
            VtColorPack.Parse(powerline.Bg),
            host.CellAt(box.Col, box.Row + histRow).Style.Bg);
        Assert.False(host.CellAt(box.Col, box.Row + histRow).Style.Bold);
    }

    [SkippableFact]
    public async Task Prompt_shrink_live_partial_unpacks_to_one_powerline_row()
    {
        RequireLibrary();
        const int cols = 40;
        const int rows = 8;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 80, libraryPathOverride: LibPath);
        var pty = new InjectedPtyProcess();
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            vt: vt,
            spawnProcess: (_, _) => pty);

        await pane.StartAsync(CancellationToken.None);
        const string fancy = "\u001b[48;2;0;95;135;38;2;255;255;255;1mPROMPT\u001b[0m";
        pty.Emit(Encoding.UTF8.GetBytes(fancy + "\r\n" + fancy));
        await WaitUntilAsync(() => CountRowsContaining(vt.CapturePaintSnapshot(), "PROMPT") >= 2);

        Assert.True(pane.TryCaptureLivePaintFrame(out var previousFrame, out _, out var prevKind, out _));
        Assert.Equal(PaneVtPaintKind.Full, prevKind);
        Assert.NotNull(previousFrame);
        pane.CommitPostedPaint();
        var previousPacked = VtCellPacker.Prepare(previousFrame!, baseline: null, dirtyRows: null);
        Assert.NotNull(previousPacked);
        var previousPayload = VtCellPacker.ToPayload(previousPacked.Value, "p1", 1, 0, 1);
        var previous = VtCellUnpacker.Apply(previousPayload, previous: null);
        Assert.Equal(2, CountAssembledRowsContaining(previous, "PROMPT"));

        pty.Emit(Encoding.UTF8.GetBytes("\r\u001b[1A\u001b[0J" + fancy));
        await WaitUntilAsync(() => CountRowsContaining(vt.CapturePaintSnapshot(), "PROMPT") == 1);

        Assert.True(vt.TryCaptureLivePaint(out _, out var dirtyKind, out var dirtyRows));
        _ = dirtyKind;
        _ = dirtyRows;
        Assert.True(pane.TryCaptureLivePaintFrame(out var nextFrame, out _, out var nextKind, out var patch));
        Assert.Equal(PaneVtPaintKind.Full, nextKind);
        Assert.Null(patch);
        Assert.NotNull(nextFrame);
        Assert.Equal(1, CountFrameRowsContaining(nextFrame!, "PROMPT"));

        var packed = VtCellPacker.Prepare(nextFrame!, baseline: null, dirtyRows: null);
        Assert.NotNull(packed);
        Assert.True(packed.Value.Full);
        var payload = VtCellPacker.ToPayload(packed.Value, "p1", 2, 1, 1);
        var assembled = VtCellUnpacker.Apply(payload, previous);
        Assert.Equal(1, CountAssembledRowsContaining(assembled, "PROMPT"));

        var host = new HostFrame();
        host.Resize(cols, rows);
        host.StampSnapshot(assembled, new CellRect(0, 0, cols, rows));
        var encoded = Encoding.UTF8.GetString(new HostBlitEncoder().Encode(host).Bytes);
        Assert.Equal(1, CountOccurrences(encoded, "PROMPT"));
        Assert.Equal(1, CountOccurrences(encoded, "48;2;0;95;135"));
    }

    [SkippableFact]
    public void Viewport_scroll_capture_top_row_is_history_and_keeps_indexed_fg()
    {
        RequireLibrary();
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m\r\n");
        for (var i = 0; i < 20; i++)
            vt.Feed($"hist-{i}\r\n");
        vt.Feed("live-edge\r\n");
        var live = vt.CapturePaintSnapshot();
        Assert.False(TryFindToken(live, "DIR", out _));
        Assert.True(vt.TryGetScrollMetrics(out var origin, out var maxOffset));
        Assert.Equal(0, origin);
        Assert.True(maxOffset > 0);
        Assert.Equal(maxOffset, vt.SetScrollOffsetFromBottom(maxOffset));
        var scrolled = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(scrolled, "DIR", out var dir));
        Assert.StartsWith("palette:", dir.Fg, StringComparison.Ordinal);
        Assert.True(vt.ReadRowCellsRgbCalls > 0);
        var top = RowText(scrolled, 0);
        Assert.StartsWith("DIR", top, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Dir_and_executable_sgr_survive_viewport_capture_pack_unpack_and_host_encode()
    {
        RequireLibrary();
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        vt.Feed("\u001b[38;5;33mDIR\u001b[0m \u001b[1;32mEXE\u001b[0m\r\n");
        for (var i = 0; i < 20; i++)
            vt.Feed($"hist-{i}\r\n");
        vt.Feed("live-edge\r\n");
        Assert.True(vt.TryGetScrollMetrics(out _, out var maxOffset));
        Assert.True(maxOffset > 0);
        Assert.Equal(maxOffset, vt.SetScrollOffsetFromBottom(maxOffset));
        var snap = vt.CapturePaintSnapshot();
        Assert.True(TryFindToken(snap, "DIR", out var dir));
        Assert.True(TryFindToken(snap, "EXE", out var exe));
        Assert.False(string.IsNullOrEmpty(dir.Fg));
        Assert.False(string.IsNullOrEmpty(exe.Fg));
        Assert.NotEqual(dir.Fg, exe.Fg);
        Assert.True(exe.Bold);
        Assert.False(dir.Bold);

        var frame = PaneRuntime.ToVtFrame("p1", snap, 0, 0, maxOffset);
        var packed = VtCellPacker.Prepare(frame, baseline: null, dirtyRows: null);
        Assert.NotNull(packed);
        var payload = VtCellPacker.ToPayload(packed.Value, "p1", 1, 0, 1);
        var assembled = VtCellUnpacker.Apply(payload, previous: null);
        Assert.Contains("DIR", AssembledText(assembled), StringComparison.Ordinal);
        Assert.Contains("EXE", AssembledText(assembled), StringComparison.Ordinal);
        var host = new HostFrame();
        host.Resize(cols, rows);
        host.StampSnapshot(assembled, new CellRect(0, 0, cols, rows));
        var encoded = Encoding.UTF8.GetString(new HostBlitEncoder().Encode(host).Bytes);
        Assert.Contains("DIR", encoded, StringComparison.Ordinal);
        Assert.Contains("EXE", encoded, StringComparison.Ordinal);
        var dirSgr = new StringBuilder();
        CellSgrEncoder.AppendSgr(dirSgr, new CellSgr(VtColorPack.Parse(dir.Fg), VtColorPack.Parse(dir.Bg), false, false, false, false, false, false, false));
        var exeSgr = new StringBuilder();
        CellSgrEncoder.AppendSgr(exeSgr, new CellSgr(VtColorPack.Parse(exe.Fg), VtColorPack.Parse(exe.Bg), true, false, false, false, false, false, false));
        Assert.False(string.IsNullOrEmpty(dirSgr.ToString()));
        Assert.False(string.IsNullOrEmpty(exeSgr.ToString()));
        Assert.NotEqual(dirSgr.ToString(), exeSgr.ToString());
        Assert.Contains(dirSgr.ToString(), encoded, StringComparison.Ordinal);
        Assert.Contains(exeSgr.ToString(), encoded, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Ghostty_live_paint_while_scrolled_is_full_viewport()
    {
        RequireLibrary();
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        for (var i = 0; i < 20; i++)
            vt.Feed($"scroll-line-{i}\r\n");
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "echo",
                Cols = cols,
                Rows = rows,
            },
            vt: vt,
            ptyFactory: TestPaneFactories.ProcessIo());

        Assert.True(pane.TryGetScrollMetrics(out _, out var maxOffset));
        Assert.True(maxOffset > 0);
        Assert.True(pane.TrySetScrollOrigin(maxOffset));
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out var kind));
        Assert.Equal(PaneVtPaintKind.Full, kind);
        Assert.NotNull(frame);
        Assert.True(frame!.ViewportOrigin > 0);
        var visible = pane.ReadVisibleText();
        Assert.Contains("scroll-line-", visible, StringComparison.Ordinal);
        Assert.True(pane.TryCaptureLivePaintJson(out var json, out _, out var jsonKind, out var dirty));
        Assert.Equal(PaneVtPaintKind.Full, jsonKind);
        Assert.False(string.IsNullOrEmpty(json));
        Assert.Null(dirty);
    }

    [SkippableFact]
    public async Task Ghostty_scroll_metrics_poll_live_scrollbar_after_write_while_scrolled()
    {
        RequireLibrary();
        const int cols = 24;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 200, libraryPathOverride: LibPath);
        for (var i = 0; i < 20; i++)
            vt.Feed($"scroll-line-{i}\r\n");
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "echo",
                Cols = cols,
                Rows = rows,
            },
            vt: vt,
            ptyFactory: TestPaneFactories.ProcessIo());

        Assert.True(pane.TryGetScrollMetrics(out var origin, out var maxOffset));
        Assert.Equal(0, origin);
        Assert.True(maxOffset >= 4);
        var parked = Math.Min(3, maxOffset);
        Assert.True(pane.TrySetScrollOrigin(parked));
        Assert.True(pane.TryGetScrollMetrics(out var parkedOrigin, out _));
        Assert.Equal(parked, parkedOrigin);
        Assert.True(vt.TryGetScrollMetrics(out var engineParked, out _));
        Assert.Equal(parked, engineParked);
        var parkedVisible = pane.ReadVisibleText();
        Assert.DoesNotContain("fresh-line-", parkedVisible, StringComparison.Ordinal);

        for (var i = 0; i < 5; i++)
            vt.Feed($"fresh-line-{i}\r\n");

        Assert.True(pane.TryGetScrollOrigin(out var originCached));
        Assert.Equal(parked, originCached);

        Assert.True(vt.TryGetScrollMetrics(out var engineLive, out _));
        Assert.True(
            engineLive > parked,
            $"Ghostty offset_from_bottom must grow after vt_write while scrolled (parked={parked}, live={engineLive})");
        Assert.True(pane.TryGetScrollMetrics(out var paneLive, out _));
        Assert.Equal(engineLive, paneLive);
        Assert.True(pane.TryGetScrollOrigin(out var originLive));
        Assert.Equal(engineLive, originLive);
        Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out var kind));
        Assert.Equal(PaneVtPaintKind.Full, kind);
        Assert.NotNull(frame);
        Assert.Equal(engineLive, frame!.ViewportOrigin);
        var stayed = pane.ReadVisibleText();
        Assert.DoesNotContain("fresh-line-", stayed, StringComparison.Ordinal);
        Assert.Equal(parkedVisible, stayed);

        Assert.True(pane.TrySetScrollOrigin(paneLive + 1));
        Assert.True(vt.TryGetScrollMetrics(out var afterStep, out var afterMax));
        Assert.Equal(Math.Min(paneLive + 1, afterMax), afterStep);
        Assert.True(pane.TryGetScrollMetrics(out var paneAfter, out _));
        Assert.Equal(afterStep, paneAfter);
        var afterVisible = pane.ReadVisibleText();
        Assert.DoesNotContain("fresh-line-", afterVisible, StringComparison.Ordinal);
        Assert.NotEqual(stayed, afterVisible);
    }

    [SkippableFact]
    public void Live_paint_reads_dirty_kind_before_grid_and_skips_clean_rows()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(20, 4, libraryPathOverride: LibPath);
        vt.Feed("abc");
        Assert.True(vt.TryCaptureLivePaint(out var snap, out var dirty, out var rows));
        if (dirty == GhosttyNative.RenderStateDirtyPartial)
        {
            Assert.NotEmpty(rows!);
            Assert.Equal("a", snap.Cells[0][0].Text);
            var skipped = false;
            for (var r = 0; r < snap.Rows; r++)
            {
                if (rows!.Contains(r))
                {
                    Assert.Equal(snap.Cols, snap.Cells[r].Length);
                    continue;
                }

                Assert.Empty(snap.Cells[r]);
                skipped = true;
            }

            Assert.True(skipped, "partial capture must skip at least one clean row");
        }
        else
            Assert.Equal("a", snap.Cells[0][0].Text);
    }



    [Fact]
    public void Dirty_rows_cover_complete_suffix_only()
    {
        Assert.True(PaneRuntime.DirtyRowsCoverCompleteSuffix([5, 6, 7], 8));
        Assert.True(PaneRuntime.DirtyRowsCoverCompleteSuffix([0, 1, 2], 3));
        Assert.False(PaneRuntime.DirtyRowsCoverCompleteSuffix([5, 7], 8));
        Assert.False(PaneRuntime.DirtyRowsCoverCompleteSuffix([3, 4], 8));
        Assert.False(PaneRuntime.DirtyRowsCoverCompleteSuffix([], 8));
        Assert.False(PaneRuntime.DirtyRowsCoverCompleteSuffix([8], 8));
    }

    [Fact]
    public void Factory_is_process_level_no_mid_pane_swap_api()
    {
        // Documented contract: selection is fixed at factory construction.
        // There is no SetProvider / SwapEngine API on the factory.
        var t = typeof(VtEngineFactory);
        Assert.Null(t.GetMethod("SetProvider"));
        Assert.Null(t.GetMethod("SwapEngine"));
        Assert.Null(t.GetMethod("SetSelection"));
        Assert.NotNull(t.GetProperty(nameof(VtEngineFactory.Selection)));
        Assert.True(t.GetProperty(nameof(VtEngineFactory.Selection))!.SetMethod is null
            || !t.GetProperty(nameof(VtEngineFactory.Selection))!.CanWrite
            || t.GetProperty(nameof(VtEngineFactory.Selection))!.SetMethod!.IsPrivate);
    }

    private static StampBuffer AllocStampDest(int cols, int rows) => new(cols, rows);

    private static string RowText(VtStructuredSnapshot snap, int row)
    {
        var line = new StringBuilder(snap.Cols);
        for (var c = 0; c < snap.Cols; c++)
        {
            var cell = snap.Cells[row][c];
            if (cell.IsContinuation)
                continue;
            line.Append(cell.Text);
        }

        return line.ToString().TrimEnd();
    }

    private static string AssembledText(AssembledSnapshot snap)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < snap.Rows; r++)
        {
            var row = snap.Cells[r];
            for (var c = 0; c < snap.Cols && c < row.Count; c++)
            {
                if (row[c].IsContinuation)
                    continue;
                sb.Append(row[c].Text);
            }
        }

        return sb.ToString();
    }

    private static int CountAssembledRowsContaining(AssembledSnapshot snap, string token)
    {
        var n = 0;
        for (var r = 0; r < snap.Rows; r++)
        {
            var line = new StringBuilder();
            var row = snap.Cells[r];
            for (var c = 0; c < snap.Cols && c < row.Count; c++)
            {
                if (row[c].IsContinuation)
                    continue;
                line.Append(row[c].Text);
            }

            if (line.ToString().Contains(token, StringComparison.Ordinal))
                n++;
        }

        return n;
    }

    private static int CountFrameRowsContaining(VtFrame frame, string token)
    {
        var n = 0;
        for (var r = 0; r < frame.Rows; r++)
        {
            var line = new StringBuilder();
            var row = frame.Cells[r];
            for (var c = 0; c < frame.Cols && c < row.Count; c++)
            {
                if (row[c].IsContinuation)
                    continue;
                line.Append(row[c].Text);
            }

            if (line.ToString().Contains(token, StringComparison.Ordinal))
                n++;
        }

        return n;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var start = 0;
        while (start < haystack.Length)
        {
            var i = haystack.IndexOf(needle, start, StringComparison.Ordinal);
            if (i < 0)
                break;
            n++;
            start = i + needle.Length;
        }

        return n;
    }

    private static int CountRowsContaining(VtStructuredSnapshot snap, string token)
    {
        var n = 0;
        for (var r = 0; r < snap.Rows; r++)
        {
            var line = new StringBuilder(snap.Cols);
            for (var c = 0; c < snap.Cols; c++)
            {
                var cell = snap.Cells[r][c];
                if (cell.IsContinuation)
                    continue;
                line.Append(cell.Text);
            }

            if (line.ToString().Contains(token, StringComparison.Ordinal))
                n++;
        }

        return n;
    }

    private static async Task WaitUntilAsync(Func<bool> pred, int timeoutMs = 10_000)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (pred())
                return;
            await Task.Delay(20);
        }

        Assert.Fail("condition not met within " + timeoutMs + "ms");
    }

    private static bool TryFindToken(VtStructuredSnapshot snap, string token, out VtCellStyleSnapshot style)
    {
        style = VtCellStyleSnapshot.Default;
        for (var r = 0; r < snap.Rows; r++)
        {
            var line = new StringBuilder(snap.Cols);
            for (var c = 0; c < snap.Cols; c++)
            {
                var cell = snap.Cells[r][c];
                if (cell.IsContinuation)
                    continue;
                line.Append(cell.Text);
            }

            var idx = line.ToString().IndexOf(token, StringComparison.Ordinal);
            if (idx < 0)
                continue;
            style = snap.Cells[r][idx].Style;
            return true;
        }

        return false;
    }

    private static bool TryFindToken(JsonElement cells, string token, out int row, out int col)
    {
        row = 0;
        col = 0;
        var r = 0;
        foreach (var rowEl in cells.EnumerateArray())
        {
            var line = new StringBuilder();
            var cols = new List<int>();
            var c = 0;
            foreach (var cell in rowEl.EnumerateArray())
            {
                if (!(cell.TryGetProperty("is_continuation", out var cont)
                    && cont.ValueKind == JsonValueKind.True))
                {
                    line.Append(cell.TryGetProperty("text", out var t) ? t.GetString() : " ");
                    cols.Add(c);
                }

                c++;
            }

            var idx = line.ToString().IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
            {
                row = r;
                col = cols[idx];
                return true;
            }

            r++;
        }

        return false;
    }

    private static LayoutChromeGeometry MouseLiveGeometry(string paneId) =>
        LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = paneId },
            false,
            null,
            paneId,
            LayoutChromeGeometry.BareInsets,
            1,
            AttachClientMode.Terminal,
            [("t1", "one", true)]);

    private static VtFrame MouseBoxFrame(CellRect box, VtFrameModes modes, long generation)
    {
        var cells = new VtCellView[box.Rows][];
        for (var r = 0; r < box.Rows; r++)
        {
            cells[r] = new VtCellView[box.Cols];
            for (var c = 0; c < box.Cols; c++)
                cells[r][c] = new VtCellView("x", 1, false, null, null, false, false, false, false, false, false, false);
        }

        return new VtFrame(
            "p1",
            box.Cols,
            box.Rows,
            cells,
            new VtFrameCursor(0, 0, true, 0),
            modes,
            0,
            1,
            generation,
            "ghostty",
            "main");
    }

    private static AttachLiveState MouseLiveState(LayoutChromeGeometry geo)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new NullAttachPort(), "w1", "t1", "p1", "lease-r"),
            PaneId = "p1",
            Chrome = geo,
        };
    }

    private static void RequireLibrary()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
    }

    private static string? ResolveNativeLibraryPath() =>
        GhosttyTestRequire.TryResolveNativeLibraryPath();


    private sealed class NullAttachPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(
            string method,
            JsonObject? parameters,
            CancellationToken ct) =>
            Task.FromResult(default(JsonElement));
    }

    private sealed class InjectedPtyProcess : IPtyProcess
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private readonly ChannelStream _output;

        public InjectedPtyProcess() => _output = new ChannelStream(_channel.Reader);

        public int Pid => 4242;
        public bool IsRunning => true;
        public int? ExitCode => null;
        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput => _output;

        public void Emit(byte[] chunk) => _channel.Writer.TryWrite(chunk);

        public void Resize(int cols, int rows)
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private byte[]? _current;
        private int _offset;

        public ChannelStream(ChannelReader<byte[]> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var n = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                    _offset += n;
                    if (_offset >= _current.Length)
                    {
                        _current = null;
                        _offset = 0;
                    }

                    return n;
                }

                if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (!_reader.TryRead(out _current))
                    continue;
                _offset = 0;
            }
        }
    }
}
