using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class GhosttyCellStyleTests
{
    [Fact]
    public void Unset_default_stays_packed_zero()
    {
        var colors = GhosttyCellStyle.ColorSet.FromPalette(GhosttyCellStyle.CreateXtermPalette());
        Assert.Null(colors.DefaultFg);
        Assert.Null(colors.DefaultBg);
        Assert.NotNull(colors.ResolvedFg);
        Assert.NotNull(colors.ResolvedBg);
        var packed = GhosttyCellStyle.ResolvePacked(0, 0, 0, 0, 0, colors);
        Assert.Equal(0u, packed.Fg);
        Assert.Equal(0u, packed.Bg);
        var resolved = GhosttyCellStyle.Resolve(VtCellStyleSnapshot.Default, colors);
        Assert.Null(resolved.Fg);
        Assert.Null(resolved.Bg);
    }

    [Fact]
    public void Palette_index_matching_default_stays_tagged()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var defaults = GhosttyCellStyle.CreateXtermPalette();
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            DefaultFg: null,
            DefaultBg: null,
            ResolvedFg: palette[7],
            ResolvedBg: palette[0],
            DefaultPalette: defaults);
        var packed = GhosttyCellStyle.ResolvePacked(
            VtColorPack.FromPalette(33), 0, 0, 0, 0, colors);
        Assert.Equal(VtColorPack.FromPalette(33), packed.Fg);
        Assert.False(VtColorPack.IsRgb(packed.Fg));
    }

    [Fact]
    public void Palette_index_differing_from_default_becomes_rgb()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var defaults = GhosttyCellStyle.CreateXtermPalette();
        palette[33] = new GhosttyCellStyle.Rgb(0x12, 0x34, 0x56);
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            DefaultFg: null,
            DefaultBg: null,
            ResolvedFg: palette[7],
            ResolvedBg: palette[0],
            DefaultPalette: defaults);
        var packed = GhosttyCellStyle.ResolvePacked(
            VtColorPack.FromPalette(33), 0, 0, 0, 0, colors);
        Assert.True(VtColorPack.IsRgb(packed.Fg));
        Assert.Equal(VtColorPack.FromRgb(0x12, 0x34, 0x56), packed.Fg);
        var other = GhosttyCellStyle.ResolvePacked(
            VtColorPack.FromPalette(1), 0, 0, 0, 0, colors);
        Assert.Equal(VtColorPack.FromPalette(1), other.Fg);
        Assert.False(VtColorPack.IsRgb(other.Fg));
    }

    [Fact]
    public void Palette_index_stays_tagged_not_rgb()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        palette[33] = new GhosttyCellStyle.Rgb(0x12, 0x34, 0x56);
        var colors = GhosttyCellStyle.ColorSet.FromPalette(palette);
        var packed = GhosttyCellStyle.ResolvePacked(
            VtColorPack.FromPalette(33), 0, 0, 0, 0, colors);
        Assert.Equal(0x01000021u, packed.Fg);
        Assert.False(VtColorPack.IsRgb(packed.Fg));
        Assert.Equal(0u, packed.Bg);
    }

    [Fact]
    public void Inverse_resolves_unset_before_swap()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            DefaultFg: null,
            DefaultBg: null,
            ResolvedFg: new GhosttyCellStyle.Rgb(0xEE, 0xEE, 0xEE),
            ResolvedBg: new GhosttyCellStyle.Rgb(0x11, 0x22, 0x33));
        var packed = GhosttyCellStyle.ResolvePacked(0, 0, 0, VtStyleBits.Inverse, 0, colors);
        Assert.Equal(GhosttyCellStyle.PackRgb(colors.ResolvedBg), packed.Fg);
        Assert.Equal(GhosttyCellStyle.PackRgb(colors.ResolvedFg), packed.Bg);
        Assert.Equal(0, packed.Modifier & VtStyleBits.Inverse);
    }

    [Fact]
    public void Child_changed_default_background_emits_rgb()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var changedBg = new GhosttyCellStyle.Rgb(0x20, 0x40, 0x60);
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            DefaultFg: null,
            DefaultBg: changedBg,
            ResolvedFg: palette[7],
            ResolvedBg: changedBg);
        var packed = GhosttyCellStyle.ResolvePacked(0, 0, 0, 0, 0, colors);
        Assert.Equal(0u, packed.Fg);
        Assert.Equal(GhosttyCellStyle.PackRgb(changedBg), packed.Bg);
        Assert.True(VtColorPack.IsRgb(packed.Bg));
    }

    [Fact]
    public void Host_matching_default_stays_packed_zero()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var hostBg = new GhosttyCellStyle.Rgb(0x12, 0x34, 0x56);
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            DefaultFg: null,
            DefaultBg: null,
            ResolvedFg: palette[7],
            ResolvedBg: hostBg);
        var packed = GhosttyCellStyle.ResolvePacked(0, 0, 0, 0, 0, colors);
        Assert.Equal(0u, packed.Fg);
        Assert.Equal(0u, packed.Bg);
    }

    [SkippableFact]
    public void Osc_11_host_match_keeps_blank_cell_packed_zero()
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        GhosttyTestRequire.RequireNativeLibrary(lib);
        using var vt = new GhosttyVtEngine(12, 4, libraryPathOverride: lib);
        var host = new HostRgb(0x12, 0x34, 0x56);
        vt.SetHostDefaultColors(null, host);
        vt.Feed(System.Text.Encoding.ASCII.GetBytes(
            HostThemeParser.OscSetDefaultColorSequence(HostDefaultColorKind.Background, host)));
        var paint = vt.CapturePaintSnapshot();
        Assert.True(string.IsNullOrEmpty(paint.Cells[0][0].Style.Bg));
        Assert.True(string.IsNullOrEmpty(paint.Cells[1][0].Style.Bg));
    }

    [Fact]
    public void Underline_palette_index_stays_tagged()
    {
        var colors = GhosttyCellStyle.ColorSet.FromPalette(GhosttyCellStyle.CreateXtermPalette());
        var packed = GhosttyCellStyle.ResolvePacked(
            0, 0, VtColorPack.FromPalette(4), 0, 1, colors);
        Assert.Equal(VtColorPack.FromPalette(4), packed.UnderlineColor);
        Assert.False(VtColorPack.IsRgb(packed.UnderlineColor));
    }

    [Fact]
    public void Palette_index_with_override_resolves_to_rgb()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        palette[33] = new GhosttyCellStyle.Rgb(0x12, 0x34, 0x56);
        var colors = GhosttyCellStyle.ColorSet.FromPalette(palette);
        var resolved = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = "palette:33",
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = false,
                Invisible = false,
                Strikethrough = false,
            },
            colors);
        Assert.Equal("palette:33", resolved.Fg);
        Assert.False(resolved.Inverse);
    }

    [Fact]
    public void Rgb_passthrough_keeps_hex()
    {
        var colors = GhosttyCellStyle.ColorSet.FromPalette(GhosttyCellStyle.CreateXtermPalette());
        var resolved = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = "#AABBCC",
                Bg = "#010203",
                Bold = true,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = false,
                Invisible = false,
                Strikethrough = false,
            },
            colors);
        Assert.Equal("#AABBCC", resolved.Fg);
        Assert.Equal("#010203", resolved.Bg);
        Assert.True(resolved.Bold);
    }

    [Fact]
    public void Inverse_swaps_using_pane_defaults_not_sgr_7()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            new GhosttyCellStyle.Rgb(0xEE, 0xEE, 0xEE),
            new GhosttyCellStyle.Rgb(0x11, 0x22, 0x33),
            new GhosttyCellStyle.Rgb(0xEE, 0xEE, 0xEE),
            new GhosttyCellStyle.Rgb(0x11, 0x22, 0x33));
        var resolved = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = null,
                Bg = null,
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = true,
                Invisible = false,
                Strikethrough = false,
            },
            colors);
        Assert.Equal("#112233", resolved.Fg);
        Assert.Equal("#EEEEEE", resolved.Bg);
        Assert.False(resolved.Inverse);
        Assert.False(resolved.Invisible);
    }

    [Fact]
    public void Invisible_sets_fg_to_bg()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var colors = GhosttyCellStyle.ColorSet.FromPalette(
            palette,
            defaultFg: new GhosttyCellStyle.Rgb(0xFF, 0xFF, 0xFF),
            defaultBg: new GhosttyCellStyle.Rgb(0x00, 0x00, 0x00));
        var resolved = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = "#FF0000",
                Bg = "#00FF00",
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = false,
                Invisible = true,
                Strikethrough = false,
            },
            colors);
        Assert.Equal("#00FF00", resolved.Fg);
        Assert.Equal("#00FF00", resolved.Bg);
        Assert.False(resolved.Invisible);
    }

    [Fact]
    public void Default_fg_bg_when_style_tag_is_none()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        var colors = GhosttyCellStyle.ColorSet.FromPalette(
            palette,
            defaultFg: new GhosttyCellStyle.Rgb(0xCA, 0xFE, 0xBA),
            defaultBg: new GhosttyCellStyle.Rgb(0xBE, 0xEF, 0x00));
        var resolved = GhosttyCellStyle.Resolve(VtCellStyleSnapshot.Default, colors);
        Assert.Equal("#CAFEBA", resolved.Fg);
        Assert.Equal("#BEEF00", resolved.Bg);
        Assert.False(resolved.Inverse);
    }

    [Fact]
    public void Fallback_palette_inverse_on_default_resolves_swapped_hex()
    {
        var colors = GhosttyCellStyle.ColorSet.FromPalette(GhosttyCellStyle.CreateXtermPalette());
        Assert.Null(colors.DefaultFg);
        Assert.Null(colors.DefaultBg);
        Assert.NotNull(colors.ResolvedFg);
        Assert.NotNull(colors.ResolvedBg);
        var resolved = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = null,
                Bg = null,
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = true,
                Invisible = false,
                Strikethrough = false,
            },
            colors);
        Assert.Equal(colors.ResolvedBg!.Value.ToHex(), resolved.Fg);
        Assert.Equal(colors.ResolvedFg!.Value.ToHex(), resolved.Bg);
        Assert.False(string.IsNullOrEmpty(resolved.Fg));
        Assert.False(string.IsNullOrEmpty(resolved.Bg));
        Assert.False(resolved.Inverse);
        Assert.NotEqual(resolved.Fg, resolved.Bg);
    }

    [Fact]
    public void Resolve_order_explicit_content_default_invisible_inverse()
    {
        var palette = GhosttyCellStyle.CreateXtermPalette();
        palette[33] = new GhosttyCellStyle.Rgb(0x12, 0x34, 0x56);
        var colors = new GhosttyCellStyle.ColorSet(
            palette,
            new GhosttyCellStyle.Rgb(0xAA, 0xAA, 0xAA),
            new GhosttyCellStyle.Rgb(0x11, 0x11, 0x11),
            new GhosttyCellStyle.Rgb(0xAA, 0xAA, 0xAA),
            new GhosttyCellStyle.Rgb(0x11, 0x11, 0x11));

        var explicitWins = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = "palette:33",
                Bg = "#010203",
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = false,
                Invisible = false,
                Strikethrough = false,
            },
            colors,
            contentFg: "#FFFFFF",
            contentBg: "#000000",
            contentBgTag: "#99AA88");
        Assert.Equal("palette:33", explicitWins.Fg);
        Assert.Equal("#99AA88", explicitWins.Bg);

        var contentWins = GhosttyCellStyle.Resolve(
            VtCellStyleSnapshot.Default,
            colors,
            contentFg: "#ABCDEF",
            contentBg: "#112233");
        Assert.Equal("#ABCDEF", contentWins.Fg);
        Assert.Equal("#112233", contentWins.Bg);

        var defaults = GhosttyCellStyle.Resolve(VtCellStyleSnapshot.Default, colors);
        Assert.Equal("#AAAAAA", defaults.Fg);
        Assert.Equal("#111111", defaults.Bg);

        var invisible = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = "#FF0000",
                Bg = "#00FF00",
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = false,
                Invisible = true,
                Strikethrough = false,
            },
            colors);
        Assert.Equal("#00FF00", invisible.Fg);
        Assert.Equal("#00FF00", invisible.Bg);
        Assert.False(invisible.Invisible);

        var inverse = GhosttyCellStyle.Resolve(
            new VtCellStyleSnapshot
            {
                Fg = null,
                Bg = null,
                Bold = false,
                Dim = false,
                Italic = false,
                Underline = false,
                Inverse = true,
                Invisible = false,
                Strikethrough = false,
                Blink = true,
                Overline = true,
                UnderlineColor = "#010203",
                UnderlineStyle = 3,
            },
            colors);
        Assert.Equal("#111111", inverse.Fg);
        Assert.Equal("#AAAAAA", inverse.Bg);
        Assert.False(inverse.Inverse);
        Assert.True(inverse.Blink);
        Assert.True(inverse.Overline);
        Assert.Equal("#010203", inverse.UnderlineColor);
        Assert.Equal(3, inverse.UnderlineStyle);
        Assert.True(inverse.Underline);
    }
}
