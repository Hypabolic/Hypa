using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BorlandThemeTests
{
    [Fact]
    public void Borland_is_a_built_in_theme_name()
    {
        Assert.Contains("borland", AttachThemeNames.BuiltIn);
        Assert.True(AttachThemeNames.IsBuiltIn("borland"));
        Assert.True(AttachThemeNames.TrySiblingPair("borland", out var dark, out var light));
        Assert.Equal("borland", dark);
        Assert.Equal("borland", light);
    }

    [Fact]
    public void FromName_borland_locks_vga_palette_and_chrome_hints()
    {
        var result = ThemePalette.FromName("borland");
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        var theme = result.Value;

        AssertRgb(theme.Accent, 0, 170, 0);
        AssertRgb(theme.PanelBg, 0, 0, 170);
        AssertRgb(theme.SidebarBg, 0, 0, 170);
        AssertRgb(theme.ActiveRowBg, 0, 170, 0);
        AssertRgb(theme.SelectionBg, 0, 170, 0);
        AssertRgb(theme.Surface0, 0, 0, 170);
        AssertRgb(theme.Overlay0, 170, 170, 170);
        AssertRgb(theme.Overlay1, 255, 255, 255);
        AssertRgb(theme.Text, 255, 255, 255);
        AssertRgb(theme.Red, 255, 85, 85);
        AssertRgb(theme.Teal, 0, 170, 170);

        Assert.Same(ThemeChromeHints.Borland, theme.Chrome);
        AssertRgb(theme.ResolveSelectionFg(), 0, 0, 0);
        AssertRgb(theme.Chrome.MetaFg!.Value, 170, 170, 170);
        AssertRgb(theme.Chrome.MetaFgOnSelection!.Value, 32, 32, 32);
        AssertRgb(theme.ResolveAccentFg(), 0, 0, 0);
        AssertRgb(theme.ResolveMenuBarBg(), 170, 170, 170);
        AssertRgb(theme.ResolveMenuBarFg(), 0, 0, 0);
        AssertRgb(theme.ResolveFocusBorder(), 255, 255, 255);
        Assert.True(theme.Chrome.DropShadow);
        Assert.True(theme.Chrome.ScrollbarArrows);
        Assert.True(theme.Chrome.DesktopStatusBar);
        Assert.Equal(ChromeGlyphPreset.Double, theme.Chrome.BorderGlyphs);
        Assert.Equal(ChromeGlyphSet.Double, theme.ResolveGlyphs(ChromeGlyphSet.Unicode));
        Assert.Equal(ChromeGlyphSet.Ascii, theme.ResolveGlyphs(ChromeGlyphSet.Ascii));
    }

    [Fact]
    public void Double_glyph_preset_uses_turbo_vision_box_drawing()
    {
        var glyphs = ChromeGlyphSet.Double;
        Assert.Equal('═', glyphs.H);
        Assert.Equal('║', glyphs.V);
        Assert.Equal('╔', glyphs.Tl);
        Assert.Equal('╗', glyphs.Tr);
        Assert.Equal('╚', glyphs.Bl);
        Assert.Equal('╝', glyphs.Br);
        Assert.Equal('╬', glyphs.Resolve(up: true, down: true, left: true, right: true));
        Assert.Equal('╔', glyphs.Resolve(up: false, down: true, left: false, right: true));
    }

    [Fact]
    public void Config_binds_theme_name_borland_and_double_glyphs()
    {
        var bound = TomlAttachConfigBinder.Bind("""
            [theme]
            name = "borland"

            [ui.chrome]
            glyphs = "double"
            """);
        Assert.True(bound.IsOk, bound.IsOk ? "" : bound.Error.Message);
        Assert.Equal("borland", bound.Value.Theme.Name);
        Assert.Equal(ChromeGlyphPreset.Double, bound.Value.Ui.Glyphs.Preset);

        var runtime = ThemeRuntime.FromConfig(bound.Value.Theme);
        Assert.True(runtime.IsOk, runtime.IsOk ? "" : runtime.Error.Message);
        Assert.Equal("borland", runtime.Value.Name);
        Assert.Equal(ThemePalette.Borland, runtime.Value.Palette);
    }

    private static void AssertRgb(ThemeColor color, byte r, byte g, byte b)
    {
        Assert.Equal(ThemeColorKind.Rgb, color.Kind);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }
}
