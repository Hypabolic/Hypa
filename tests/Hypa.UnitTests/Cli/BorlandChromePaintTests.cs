using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class BorlandChromePaintTests
{
    [Fact]
    public void Single_pane_with_outer_borders_gets_full_frame()
    {
        var panes = new[]
        {
            new PaneChromeSpec("p1", "shell", new CellRect(0, 1, 80, 20), PaneChromeBorders.None, Focused: true),
        };

        var applied = PaneLineCells.Apply(
            panes,
            paneBorders: true,
            paneGaps: false,
            paneOuterBorders: true,
            singlePaneFrame: true);
        Assert.Single(applied);
        Assert.Equal(PaneChromeBorders.All, applied[0].Borders);
    }

    [Fact]
    public void Single_pane_without_window_frame_stays_flush()
    {
        var panes = new[]
        {
            new PaneChromeSpec("p1", "shell", new CellRect(0, 1, 80, 20), PaneChromeBorders.None, Focused: true),
        };

        var applied = PaneLineCells.Apply(panes, paneBorders: true, paneGaps: false, paneOuterBorders: true);
        Assert.Equal(PaneChromeBorders.None, Assert.Single(applied).Borders);
        Assert.Equal(
            PaneChromeBorders.None,
            PaneLineCells.ZoomedBorders(1, paneBorders: true, paneOuterBorders: true));
    }

    [Fact]
    public void Zoomed_borders_keep_frame_for_single_pane_outer()
    {
        Assert.Equal(
            PaneChromeBorders.All,
            PaneLineCells.ZoomedBorders(1, paneBorders: true, paneOuterBorders: true, singlePaneFrame: true));
    }

    [Fact]
    public void Borland_chrome_hints_enable_desktop_status_and_double_glyphs()
    {
        var theme = ThemePalette.Borland;
        Assert.True(theme.Chrome.DesktopStatusBar);
        Assert.True(theme.Chrome.SinglePaneFrame);
        Assert.False(ThemePalette.Catppuccin.Chrome.SinglePaneFrame);
        Assert.True(theme.Chrome.DropShadow);
        Assert.Equal(ChromeGlyphPreset.Double, theme.Chrome.BorderGlyphs);
        Assert.Equal(ChromeGlyphSet.Double, theme.ResolveGlyphs(ChromeGlyphSet.Unicode));
        AssertRgb(theme.SidebarBg, 0, 0, 170);
        AssertRgb(theme.PanelBg, 0, 0, 170);
        AssertRgb(theme.ResolveMenuBarBg(), 170, 170, 170);
    }

    private static void AssertRgb(ThemeColor color, byte r, byte g, byte b)
    {
        Assert.Equal(ThemeColorKind.Rgb, color.Kind);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }
}
