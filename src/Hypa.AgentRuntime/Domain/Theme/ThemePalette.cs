using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Domain.Theme;

/// <summary>One built-in chrome palette. Pane VT cells stay snapshot-owned.</summary>
public sealed record ThemePalette
{
    public required ThemeColor Accent { get; init; }
    public required ThemeColor PanelBg { get; init; }
    public required ThemeColor SidebarBg { get; init; }
    public required ThemeColor ActiveRowBg { get; init; }
    public required ThemeColor SelectionBg { get; init; }
    public required ThemeColor Surface0 { get; init; }
    public required ThemeColor Surface1 { get; init; }
    public required ThemeColor SurfaceDim { get; init; }
    public required ThemeColor Overlay0 { get; init; }
    public required ThemeColor Overlay1 { get; init; }
    public required ThemeColor Text { get; init; }
    public required ThemeColor Subtext0 { get; init; }
    public required ThemeColor Mauve { get; init; }
    public required ThemeColor Green { get; init; }
    public required ThemeColor Yellow { get; init; }
    public required ThemeColor Red { get; init; }
    public required ThemeColor Blue { get; init; }
    public required ThemeColor Teal { get; init; }
    public required ThemeColor Peach { get; init; }

    /// <summary>Optional chrome overrides. Defaults keep existing theme paint paths.</summary>
    public ThemeChromeHints Chrome { get; init; } = ThemeChromeHints.Default;

    public ThemeColor ResolveSelectionFg() => Chrome.SelectionFg ?? Text;

    public ThemeColor ResolveAccentFg() =>
        Chrome.AccentFg ?? (PanelBg.IsReset ? SurfaceDim : PanelBg);

    public ThemeColor ResolveMenuBarBg() => Chrome.MenuBarBg ?? PanelBg;

    public ThemeColor ResolveMenuBarFg() => Chrome.MenuBarFg ?? Text;

    public ThemeColor ResolveFocusBorder() => Chrome.FocusBorder ?? Accent;

    public ThemeColor ResolveShadow() => Chrome.Shadow ?? ThemeColor.Rgb(0, 0, 0);

    /// <summary>
    /// Theme-preferred glyphs win over the default Unicode set. Explicit Ascii stays.
    /// </summary>
    public ChromeGlyphSet ResolveGlyphs(ChromeGlyphSet configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (configured.Preset is ChromeGlyphPreset.Ascii)
            return configured;
        if (Chrome.BorderGlyphs is { } preset)
            return ChromeGlyphSet.For(preset);
        return configured;
    }

    public static ThemePalette Catppuccin { get; } = new()
    {
        Accent = R(137, 180, 250),
        PanelBg = R(24, 24, 37),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(30, 30, 46),
        SelectionBg = R(49, 50, 68),
        Surface0 = R(49, 50, 68),
        Surface1 = R(69, 71, 90),
        SurfaceDim = R(30, 30, 46),
        Overlay0 = R(108, 112, 134),
        Overlay1 = R(127, 132, 156),
        Text = R(205, 214, 244),
        Subtext0 = R(166, 173, 200),
        Mauve = R(203, 166, 247),
        Green = R(166, 227, 161),
        Yellow = R(249, 226, 175),
        Red = R(243, 139, 168),
        Blue = R(137, 180, 250),
        Teal = R(148, 226, 213),
        Peach = R(250, 179, 135),
    };

    public static ThemePalette CatppuccinLatte { get; } = new()
    {
        Accent = R(30, 102, 245),
        PanelBg = R(239, 241, 245),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(230, 233, 239),
        SelectionBg = R(189, 208, 245),
        Surface0 = R(204, 208, 218),
        Surface1 = R(188, 192, 204),
        SurfaceDim = R(230, 233, 239),
        Overlay0 = R(156, 160, 176),
        Overlay1 = R(140, 143, 161),
        Text = R(76, 79, 105),
        Subtext0 = R(108, 111, 133),
        Mauve = R(136, 57, 239),
        Green = R(64, 160, 43),
        Yellow = R(223, 142, 29),
        Red = R(210, 15, 57),
        Blue = R(30, 102, 245),
        Teal = R(23, 146, 153),
        Peach = R(254, 100, 11),
    };

    public static ThemePalette Terminal { get; } = new()
    {
        Accent = ThemeColor.Blue,
        PanelBg = ThemeColor.Reset,
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = ThemeColor.DarkGray,
        SelectionBg = ThemeColor.Reset,
        Surface0 = ThemeColor.Reset,
        Surface1 = ThemeColor.DarkGray,
        SurfaceDim = ThemeColor.DarkGray,
        Overlay0 = ThemeColor.Gray,
        Overlay1 = ThemeColor.White,
        Text = ThemeColor.Reset,
        Subtext0 = ThemeColor.Gray,
        Mauve = ThemeColor.Gray,
        Green = ThemeColor.Green,
        Yellow = ThemeColor.Yellow,
        Red = ThemeColor.LightRed,
        Blue = ThemeColor.Blue,
        Teal = ThemeColor.Cyan,
        Peach = ThemeColor.Yellow,
    };

    public static ThemePalette TokyoNight { get; } = new()
    {
        Accent = R(122, 162, 247),
        PanelBg = R(26, 27, 38),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(35, 38, 54),
        SelectionBg = R(45, 54, 80),
        Surface0 = R(36, 40, 59),
        Surface1 = R(65, 72, 104),
        SurfaceDim = R(26, 27, 38),
        Overlay0 = R(86, 95, 137),
        Overlay1 = R(105, 113, 150),
        Text = R(192, 202, 245),
        Subtext0 = R(169, 177, 214),
        Mauve = R(187, 154, 247),
        Green = R(158, 206, 106),
        Yellow = R(224, 175, 104),
        Red = R(247, 118, 142),
        Blue = R(122, 162, 247),
        Teal = R(125, 207, 255),
        Peach = R(255, 158, 100),
    };

    public static ThemePalette TokyoNightDay { get; } = new()
    {
        Accent = R(46, 125, 233),
        PanelBg = R(225, 226, 231),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(210, 211, 218),
        SelectionBg = R(182, 202, 231),
        Surface0 = R(196, 200, 218),
        Surface1 = R(168, 174, 203),
        SurfaceDim = R(210, 211, 218),
        Overlay0 = R(137, 144, 179),
        Overlay1 = R(104, 112, 154),
        Text = R(55, 96, 191),
        Subtext0 = R(97, 114, 176),
        Mauve = R(120, 71, 189),
        Green = R(88, 117, 57),
        Yellow = R(140, 108, 62),
        Red = R(245, 42, 101),
        Blue = R(46, 125, 233),
        Teal = R(17, 140, 116),
        Peach = R(177, 92, 0),
    };

    public static ThemePalette Dracula { get; } = new()
    {
        Accent = R(189, 147, 249),
        PanelBg = R(40, 42, 54),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(55, 60, 82),
        SelectionBg = R(70, 63, 93),
        Surface0 = R(68, 71, 90),
        Surface1 = R(98, 114, 164),
        SurfaceDim = R(40, 42, 54),
        Overlay0 = R(98, 114, 164),
        Overlay1 = R(130, 140, 180),
        Text = R(248, 248, 242),
        Subtext0 = R(210, 210, 220),
        Mauve = R(255, 121, 198),
        Green = R(80, 250, 123),
        Yellow = R(241, 250, 140),
        Red = R(255, 85, 85),
        Blue = R(139, 233, 253),
        Teal = R(139, 233, 253),
        Peach = R(255, 184, 108),
    };

    public static ThemePalette Nord { get; } = new()
    {
        Accent = R(136, 192, 208),
        PanelBg = R(46, 52, 64),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(67, 76, 94),
        SelectionBg = R(64, 80, 93),
        Surface0 = R(59, 66, 82),
        Surface1 = R(67, 76, 94),
        SurfaceDim = R(46, 52, 64),
        Overlay0 = R(76, 86, 106),
        Overlay1 = R(100, 110, 130),
        Text = R(236, 239, 244),
        Subtext0 = R(216, 222, 233),
        Mauve = R(180, 142, 173),
        Green = R(163, 190, 140),
        Yellow = R(235, 203, 139),
        Red = R(191, 97, 106),
        Blue = R(129, 161, 193),
        Teal = R(143, 188, 187),
        Peach = R(208, 135, 112),
    };

    public static ThemePalette Gruvbox { get; } = new()
    {
        Accent = R(215, 153, 33),
        PanelBg = R(40, 40, 40),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(50, 49, 48),
        SelectionBg = R(75, 63, 39),
        Surface0 = R(60, 56, 54),
        Surface1 = R(80, 73, 69),
        SurfaceDim = R(40, 40, 40),
        Overlay0 = R(146, 131, 116),
        Overlay1 = R(168, 153, 132),
        Text = R(235, 219, 178),
        Subtext0 = R(213, 196, 161),
        Mauve = R(211, 134, 155),
        Green = R(184, 187, 38),
        Yellow = R(250, 189, 47),
        Red = R(251, 73, 52),
        Blue = R(131, 165, 152),
        Teal = R(142, 192, 124),
        Peach = R(254, 128, 25),
    };

    public static ThemePalette GruvboxLight { get; } = new()
    {
        Accent = R(7, 102, 120),
        PanelBg = R(251, 241, 199),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(242, 229, 188),
        SelectionBg = R(235, 219, 178),
        Surface0 = R(235, 219, 178),
        Surface1 = R(213, 196, 161),
        SurfaceDim = R(242, 229, 188),
        Overlay0 = R(146, 131, 116),
        Overlay1 = R(124, 111, 100),
        Text = R(60, 56, 54),
        Subtext0 = R(80, 73, 69),
        Mauve = R(143, 63, 113),
        Green = R(121, 116, 14),
        Yellow = R(181, 118, 20),
        Red = R(157, 0, 6),
        Blue = R(7, 102, 120),
        Teal = R(66, 123, 88),
        Peach = R(175, 58, 3),
    };

    public static ThemePalette OneDark { get; } = new()
    {
        Accent = R(97, 175, 239),
        PanelBg = R(40, 44, 52),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(49, 54, 64),
        SelectionBg = R(51, 70, 89),
        Surface0 = R(44, 49, 58),
        Surface1 = R(62, 68, 81),
        SurfaceDim = R(40, 44, 52),
        Overlay0 = R(92, 99, 112),
        Overlay1 = R(115, 122, 135),
        Text = R(171, 178, 191),
        Subtext0 = R(150, 156, 168),
        Mauve = R(198, 120, 221),
        Green = R(152, 195, 121),
        Yellow = R(229, 192, 123),
        Red = R(224, 108, 117),
        Blue = R(97, 175, 239),
        Teal = R(86, 182, 194),
        Peach = R(209, 154, 102),
    };

    public static ThemePalette OneLight { get; } = new()
    {
        Accent = R(64, 120, 242),
        PanelBg = R(250, 250, 250),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(216, 219, 226),
        SelectionBg = R(205, 219, 248),
        Surface0 = R(240, 240, 241),
        Surface1 = R(229, 229, 230),
        SurfaceDim = R(245, 245, 246),
        Overlay0 = R(160, 161, 167),
        Overlay1 = R(104, 107, 119),
        Text = R(56, 58, 66),
        Subtext0 = R(104, 107, 119),
        Mauve = R(166, 38, 164),
        Green = R(80, 161, 79),
        Yellow = R(193, 132, 1),
        Red = R(228, 86, 73),
        Blue = R(64, 120, 242),
        Teal = R(1, 132, 188),
        Peach = R(152, 104, 1),
    };

    public static ThemePalette Solarized { get; } = new()
    {
        Accent = R(38, 139, 210),
        PanelBg = R(0, 43, 54),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(22, 75, 87),
        SelectionBg = R(8, 62, 85),
        Surface0 = R(7, 54, 66),
        Surface1 = R(88, 110, 117),
        SurfaceDim = R(0, 43, 54),
        Overlay0 = R(88, 110, 117),
        Overlay1 = R(101, 123, 131),
        Text = R(147, 161, 161),
        Subtext0 = R(131, 148, 150),
        Mauve = R(211, 54, 130),
        Green = R(133, 153, 0),
        Yellow = R(181, 137, 0),
        Red = R(220, 50, 47),
        Blue = R(38, 139, 210),
        Teal = R(42, 161, 152),
        Peach = R(203, 75, 22),
    };

    public static ThemePalette SolarizedLight { get; } = new()
    {
        Accent = R(38, 139, 210),
        PanelBg = R(253, 246, 227),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(238, 232, 213),
        SelectionBg = R(201, 220, 223),
        Surface0 = R(238, 232, 213),
        Surface1 = R(147, 161, 161),
        SurfaceDim = R(238, 232, 213),
        Overlay0 = R(147, 161, 161),
        Overlay1 = R(88, 110, 117),
        Text = R(101, 123, 131),
        Subtext0 = R(131, 148, 150),
        Mauve = R(211, 54, 130),
        Green = R(133, 153, 0),
        Yellow = R(181, 137, 0),
        Red = R(220, 50, 47),
        Blue = R(38, 139, 210),
        Teal = R(42, 161, 152),
        Peach = R(203, 75, 22),
    };

    public static ThemePalette Kanagawa { get; } = new()
    {
        Accent = R(126, 156, 216),
        PanelBg = R(31, 31, 40),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(54, 54, 70),
        SelectionBg = R(50, 56, 75),
        Surface0 = R(42, 42, 55),
        Surface1 = R(54, 54, 70),
        SurfaceDim = R(31, 31, 40),
        Overlay0 = R(114, 113, 105),
        Overlay1 = R(135, 134, 125),
        Text = R(220, 215, 186),
        Subtext0 = R(200, 195, 170),
        Mauve = R(149, 127, 184),
        Green = R(118, 148, 106),
        Yellow = R(192, 163, 110),
        Red = R(195, 64, 67),
        Blue = R(126, 156, 216),
        Teal = R(127, 180, 202),
        Peach = R(255, 160, 102),
    };

    public static ThemePalette KanagawaLotus { get; } = new()
    {
        Accent = R(77, 105, 155),
        PanelBg = R(242, 236, 188),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(213, 206, 163),
        SelectionBg = R(220, 213, 172),
        Surface0 = R(220, 213, 172),
        Surface1 = R(201, 203, 209),
        SurfaceDim = R(213, 206, 163),
        Overlay0 = R(160, 156, 172),
        Overlay1 = R(138, 137, 128),
        Text = R(84, 84, 100),
        Subtext0 = R(67, 67, 108),
        Mauve = R(98, 76, 131),
        Green = R(111, 137, 78),
        Yellow = R(119, 113, 63),
        Red = R(200, 64, 83),
        Blue = R(77, 105, 155),
        Teal = R(78, 140, 162),
        Peach = R(204, 109, 0),
    };

    public static ThemePalette RosePine { get; } = new()
    {
        Accent = R(196, 167, 231),
        PanelBg = R(25, 23, 36),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(38, 35, 58),
        SelectionBg = R(59, 52, 75),
        Surface0 = R(31, 29, 46),
        Surface1 = R(38, 35, 58),
        SurfaceDim = R(38, 35, 58),
        Overlay0 = R(110, 106, 134),
        Overlay1 = R(144, 140, 170),
        Text = R(224, 222, 244),
        Subtext0 = R(200, 197, 220),
        Mauve = R(196, 167, 231),
        Green = R(49, 116, 143),
        Yellow = R(246, 193, 119),
        Red = R(235, 111, 146),
        Blue = R(49, 116, 143),
        Teal = R(156, 207, 216),
        Peach = R(234, 154, 151),
    };

    public static ThemePalette RosePineDawn { get; } = new()
    {
        Accent = R(144, 122, 169),
        PanelBg = R(250, 244, 237),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(227, 217, 207),
        SelectionBg = R(242, 233, 225),
        Surface0 = R(242, 233, 225),
        Surface1 = R(255, 250, 243),
        SurfaceDim = R(242, 233, 225),
        Overlay0 = R(152, 147, 165),
        Overlay1 = R(121, 117, 147),
        Text = R(70, 66, 97),
        Subtext0 = R(121, 117, 147),
        Mauve = R(144, 122, 169),
        Green = R(40, 105, 131),
        Yellow = R(234, 157, 52),
        Red = R(180, 99, 122),
        Blue = R(40, 105, 131),
        Teal = R(86, 148, 159),
        Peach = R(215, 130, 126),
    };

    public static ThemePalette Vesper { get; } = new()
    {
        Accent = R(255, 199, 153),
        PanelBg = R(26, 26, 26),
        SidebarBg = ThemeColor.Reset,
        ActiveRowBg = R(16, 16, 16),
        SelectionBg = R(35, 35, 35),
        Surface0 = R(35, 35, 35),
        Surface1 = R(40, 40, 40),
        SurfaceDim = R(16, 16, 16),
        Overlay0 = R(92, 92, 92),
        Overlay1 = R(126, 126, 126),
        Text = R(255, 255, 255),
        Subtext0 = R(160, 160, 160),
        Mauve = R(255, 209, 168),
        Green = R(153, 255, 228),
        Yellow = R(255, 199, 153),
        Red = R(255, 128, 128),
        Blue = R(176, 176, 176),
        Teal = R(102, 221, 204),
        Peach = R(255, 199, 153),
    };

    /// <summary>
    /// Borland Turbo C / Turbo Vision DOS VGA palette.
    /// Blue desktop, green selection, white focus borders, gray menu bar.
    /// </summary>
    public static ThemePalette Borland { get; } = new()
    {
        Accent = R(0, 170, 0),
        PanelBg = R(0, 0, 170),
        SidebarBg = R(0, 0, 170),
        ActiveRowBg = R(0, 170, 0),
        SelectionBg = R(0, 170, 0),
        Surface0 = R(0, 0, 170),
        Surface1 = R(85, 85, 255),
        SurfaceDim = R(0, 0, 85),
        Overlay0 = R(170, 170, 170),
        Overlay1 = R(255, 255, 255),
        Text = R(255, 255, 255),
        Subtext0 = R(170, 170, 170),
        Mauve = R(170, 0, 170),
        Green = R(0, 170, 0),
        Yellow = R(255, 255, 85),
        Red = R(255, 85, 85),
        Blue = R(85, 85, 255),
        Teal = R(0, 170, 170),
        Peach = R(170, 85, 0),
        Chrome = ThemeChromeHints.Borland,
    };

    public static AttachConfigResult<ThemePalette> FromName(string? name)
    {
        if (string.IsNullOrEmpty(name) || string.Equals(name, "catppuccin", StringComparison.Ordinal))
            return AttachConfigResult<ThemePalette>.Ok(Catppuccin);

        var palette = name switch
        {
            "catppuccin-latte" => CatppuccinLatte,
            "terminal" => Terminal,
            "tokyo-night" => TokyoNight,
            "tokyo-night-day" => TokyoNightDay,
            "dracula" => Dracula,
            "nord" => Nord,
            "gruvbox" => Gruvbox,
            "gruvbox-light" => GruvboxLight,
            "one-dark" => OneDark,
            "one-light" => OneLight,
            "solarized" => Solarized,
            "solarized-light" => SolarizedLight,
            "kanagawa" => Kanagawa,
            "kanagawa-lotus" => KanagawaLotus,
            "rose-pine" => RosePine,
            "rose-pine-dawn" => RosePineDawn,
            "vesper" => Vesper,
            "borland" => Borland,
            _ => null,
        };
        if (palette is null)
        {
            return AttachConfigResult<ThemePalette>.Fail(AttachConfigError.Value(
                "theme.name",
                $"theme.name '{name}' is not a built-in theme.",
                line: null));
        }

        return AttachConfigResult<ThemePalette>.Ok(palette);
    }

    public AttachConfigResult<ThemePalette> WithOverrides(IReadOnlyDictionary<string, string>? custom)
    {
        if (custom is null || custom.Count == 0)
            return AttachConfigResult<ThemePalette>.Ok(this);

        var next = this;
        foreach (var (token, value) in custom)
        {
            if (!IsCustomToken(token))
            {
                return AttachConfigResult<ThemePalette>.Fail(AttachConfigError.Unknown(
                    "theme.custom." + token,
                    line: null));
            }

            if (!ThemeColorParser.TryParse(value, out var color))
            {
                return AttachConfigResult<ThemePalette>.Fail(AttachConfigError.Value(
                    "theme.custom." + token,
                    $"theme.custom.{token} is not a valid color.",
                    line: null));
            }

            var applied = next.WithToken(token, color);
            if (!applied.IsOk)
                return applied;
            next = applied.Value;
        }

        return AttachConfigResult<ThemePalette>.Ok(next);
    }

    public static bool IsCustomToken(string token) =>
        token is "accent" or "panel_bg" or "sidebar_bg" or "active_row_bg" or "selection_bg"
            or "surface0" or "surface1" or "surface_dim" or "overlay0" or "overlay1"
            or "text" or "subtext0" or "mauve" or "green" or "yellow" or "red"
            or "blue" or "teal" or "peach";

    public AttachConfigResult<ThemePalette> WithToken(string token, ThemeColor color) =>
        token switch
        {
            "accent" => AttachConfigResult<ThemePalette>.Ok(this with { Accent = color }),
            "panel_bg" => AttachConfigResult<ThemePalette>.Ok(this with { PanelBg = color }),
            "sidebar_bg" => AttachConfigResult<ThemePalette>.Ok(this with { SidebarBg = color }),
            "active_row_bg" => AttachConfigResult<ThemePalette>.Ok(this with { ActiveRowBg = color }),
            "selection_bg" => AttachConfigResult<ThemePalette>.Ok(this with { SelectionBg = color }),
            "surface0" => AttachConfigResult<ThemePalette>.Ok(this with { Surface0 = color }),
            "surface1" => AttachConfigResult<ThemePalette>.Ok(this with { Surface1 = color }),
            "surface_dim" => AttachConfigResult<ThemePalette>.Ok(this with { SurfaceDim = color }),
            "overlay0" => AttachConfigResult<ThemePalette>.Ok(this with { Overlay0 = color }),
            "overlay1" => AttachConfigResult<ThemePalette>.Ok(this with { Overlay1 = color }),
            "text" => AttachConfigResult<ThemePalette>.Ok(this with { Text = color }),
            "subtext0" => AttachConfigResult<ThemePalette>.Ok(this with { Subtext0 = color }),
            "mauve" => AttachConfigResult<ThemePalette>.Ok(this with { Mauve = color }),
            "green" => AttachConfigResult<ThemePalette>.Ok(this with { Green = color }),
            "yellow" => AttachConfigResult<ThemePalette>.Ok(this with { Yellow = color }),
            "red" => AttachConfigResult<ThemePalette>.Ok(this with { Red = color }),
            "blue" => AttachConfigResult<ThemePalette>.Ok(this with { Blue = color }),
            "teal" => AttachConfigResult<ThemePalette>.Ok(this with { Teal = color }),
            "peach" => AttachConfigResult<ThemePalette>.Ok(this with { Peach = color }),
            _ => AttachConfigResult<ThemePalette>.Fail(AttachConfigError.Unknown(
                "theme.custom." + token,
                line: null)),
        };

    public bool UsesHostAnsiOnly()
    {
        foreach (var color in AllTokens())
        {
            if (color.Kind is ThemeColorKind.Rgb)
                return false;
        }

        return true;
    }

    public IEnumerable<ThemeColor> AllTokens()
    {
        yield return Accent;
        yield return PanelBg;
        yield return SidebarBg;
        yield return ActiveRowBg;
        yield return SelectionBg;
        yield return Surface0;
        yield return Surface1;
        yield return SurfaceDim;
        yield return Overlay0;
        yield return Overlay1;
        yield return Text;
        yield return Subtext0;
        yield return Mauve;
        yield return Green;
        yield return Yellow;
        yield return Red;
        yield return Blue;
        yield return Teal;
        yield return Peach;
    }

    private static ThemeColor R(byte r, byte g, byte b) => ThemeColor.Rgb(r, g, b);
}
