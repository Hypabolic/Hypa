using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Domain.Theme;

/// <summary>
/// Non-color chrome behavior and optional color overrides for one theme.
/// Null color fields fall back to the matching <see cref="ThemePalette"/> token.
/// </summary>
public sealed record ThemeChromeHints
{
    public ThemeColor? SelectionFg { get; init; }

    public ThemeColor? AccentFg { get; init; }

    public ThemeColor? MenuBarBg { get; init; }

    public ThemeColor? MenuBarFg { get; init; }

    /// <summary>Sidebar detail text. Null keeps the dimmed overlay color.</summary>
    public ThemeColor? MetaFg { get; init; }

    /// <summary>Detail text on a selected row. Null uses <see cref="MetaFg"/>.</summary>
    public ThemeColor? MetaFgOnSelection { get; init; }

    public ThemeColor? FocusBorder { get; init; }

    public ThemeColor? Shadow { get; init; }

    public bool DropShadow { get; init; }

    /// <summary>
    /// When set, attach uses this glyph preset unless the user forced
    /// <see cref="ChromeGlyphPreset.Ascii"/>.
    /// </summary>
    public ChromeGlyphPreset? BorderGlyphs { get; init; }

    public bool ScrollbarArrows { get; init; }

    /// <summary>Reserve and paint a gray Turbo Vision status strip on desktop attach.</summary>
    public bool DesktopStatusBar { get; init; }

    /// <summary>Draw a window frame around one pane. Other themes frame splits only.</summary>
    public bool SinglePaneFrame { get; init; }

    public static ThemeChromeHints Default { get; } = new();

    /// <summary>Turbo Vision chrome: gray menu bar, black-on-green selection, double borders.</summary>
    public static ThemeChromeHints Borland { get; } = new()
    {
        SelectionFg = ThemeColor.Rgb(0, 0, 0),
        AccentFg = ThemeColor.Rgb(0, 0, 0),
        MenuBarBg = ThemeColor.Rgb(170, 170, 170),
        MenuBarFg = ThemeColor.Rgb(0, 0, 0),
        MetaFg = ThemeColor.Rgb(170, 170, 170),
        MetaFgOnSelection = ThemeColor.Rgb(32, 32, 32),
        FocusBorder = ThemeColor.Rgb(255, 255, 255),
        Shadow = ThemeColor.Rgb(0, 0, 0),
        DropShadow = true,
        BorderGlyphs = ChromeGlyphPreset.Double,
        ScrollbarArrows = true,
        DesktopStatusBar = true,
        SinglePaneFrame = true,
    };
}
