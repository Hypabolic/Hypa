using System.Globalization;
using Hypa.AgentRuntime.Application;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Resolve a Ghostty cell colour the way <c>ghostty_cell_style</c> does:
/// explicit style, palette, pane defaults, invisible, then inverse.
/// Inverse and invisible are applied here so the host does not receive SGR 7/8.
/// </summary>
public static class GhosttyCellStyle
{
    public const int PaletteSize = 256;

    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public string ToHex() =>
            string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
    }

    public sealed record ColorSet(
        Rgb[] Palette,
        Rgb? DefaultFg,
        Rgb? DefaultBg,
        Rgb? ResolvedFg,
        Rgb? ResolvedBg,
        Rgb[]? DefaultPalette = null)
    {
        public static ColorSet FromPalette(Rgb[] palette, Rgb? defaultFg = null, Rgb? defaultBg = null)
        {
            ArgumentNullException.ThrowIfNull(palette);
            // Option (None = host reset). resolved_* is inverse RGB.
            // Omitted defaults stay null. Palette 7/0 fill Resolved* only.
            var resolvedFg = defaultFg ?? (palette.Length > 7 ? palette[7] : new Rgb(0xFF, 0xFF, 0xFF));
            var resolvedBg = defaultBg ?? (palette.Length > 0 ? palette[0] : new Rgb(0x00, 0x00, 0x00));
            return new(palette, defaultFg, defaultBg, resolvedFg, resolvedBg, palette);
        }
    }

    public static VtCellStyleSnapshot Resolve(
        VtCellStyleSnapshot style,
        ColorSet colors,
        string? contentFg = null,
        string? contentBg = null,
        string? contentBgTag = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(colors.Palette);

        var underlineStyle = style.UnderlineStyle is >= 0 and <= 255
            ? (byte)style.UnderlineStyle
            : (byte)0;
        var packed = ResolvePacked(
            VtColorPack.Parse(style.Fg),
            VtColorPack.Parse(style.Bg),
            VtColorPack.Parse(style.UnderlineColor),
            VtStyleBits.Pack(
                style.Bold,
                style.Dim,
                style.Italic,
                style.Underline,
                style.Inverse,
                style.Invisible,
                style.Strikethrough,
                style.Blink,
                style.Overline),
            underlineStyle,
            colors,
            VtColorPack.Parse(contentFg),
            VtColorPack.Parse(contentBg),
            VtColorPack.Parse(contentBgTag));
        return new VtCellStyleSnapshot
        {
            Fg = VtColorPack.ToWire(packed.Fg),
            Bg = VtColorPack.ToWire(packed.Bg),
            Bold = style.Bold,
            Dim = style.Dim,
            Italic = style.Italic,
            Underline = (packed.Modifier & VtStyleBits.Underline) != 0,
            Inverse = (packed.Modifier & VtStyleBits.Inverse) != 0,
            Invisible = false,
            Strikethrough = style.Strikethrough,
            Blink = style.Blink,
            Overline = style.Overline,
            UnderlineColor = VtColorPack.ToWire(packed.UnderlineColor),
            UnderlineStyle = packed.UnderlineStyle,
        };
    }

    public static VtPackedStyle ResolvePacked(
        uint fgToken,
        uint bgToken,
        uint underlineToken,
        ushort modifier,
        byte underlineStyle,
        ColorSet colors,
        uint contentFg = 0,
        uint contentBg = 0,
        uint contentBgTag = 0)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(colors.Palette);

        // colour, then default_* (None stays packed 0 / host reset).
        var fg = ResolvePackedToken(fgToken, colors.Palette, fallback: 0, colors.DefaultPalette);
        if (fg == 0)
            fg = ResolvePackedToken(contentFg, colors.Palette, fallback: 0, colors.DefaultPalette);
        if (fg == 0)
            fg = PackRgb(colors.DefaultFg);

        var bg = ResolvePackedToken(contentBgTag, colors.Palette, fallback: 0, colors.DefaultPalette);
        if (bg == 0)
            bg = ResolvePackedToken(bgToken, colors.Palette, fallback: 0, colors.DefaultPalette);
        if (bg == 0)
            bg = ResolvePackedToken(contentBg, colors.Palette, fallback: 0, colors.DefaultPalette);
        if (bg == 0)
            bg = PackRgb(colors.DefaultBg);

        if ((modifier & VtStyleBits.Invisible) != 0)
            fg = bg != 0 ? bg : PackRgb(colors.DefaultBg);

        var inverse = false;
        if ((modifier & VtStyleBits.Inverse) != 0)
        {
            // emit colour is None, then swap. Do not send SGR 7.
            if (fg == 0)
                fg = PackRgb(colors.ResolvedFg);
            if (bg == 0)
                bg = PackRgb(colors.ResolvedBg);
            if (fg != 0 && bg != 0)
                (fg, bg) = (bg, fg);
            else
                inverse = true;
        }

        var underlineColor = ResolvePackedToken(
            underlineToken, colors.Palette, fallback: 0, colors.DefaultPalette);
        var underline = (modifier & VtStyleBits.Underline) != 0 || underlineStyle != 0;
        var bits = (ushort)(modifier & ~(VtStyleBits.Invisible | VtStyleBits.Inverse | VtStyleBits.Underline));
        if (underline)
            bits |= VtStyleBits.Underline;
        if (inverse)
            bits |= VtStyleBits.Inverse;
        return new VtPackedStyle(fg, bg, underlineColor, bits, underlineStyle);
    }

    public static uint PackRgb(Rgb? rgb)
    {
        if (rgb is not { } value)
            return 0;
        return VtColorPack.FromRgb(value.R, value.G, value.B);
    }

    public static uint ResolvePackedToken(
        uint token,
        Rgb[] palette,
        uint fallback,
        Rgb[]? defaultPalette = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (token == 0)
            return fallback;
        // (active != default) becomes RGB; other indices stay Color::Indexed.
        if (VtColorPack.IsPalette(token))
        {
            var idx = VtColorPack.PaletteIndex(token);
            if (idx is >= 0 and < PaletteSize && palette.Length > idx)
            {
                if (defaultPalette is { Length: > 0 }
                    && defaultPalette.Length > idx
                    && palette[idx] != defaultPalette[idx])
                {
                    return VtColorPack.FromRgb(palette[idx].R, palette[idx].G, palette[idx].B);
                }

                return token;
            }

            return fallback;
        }

        if (VtColorPack.IsRgb(token))
            return token;
        return fallback;
    }

    public static Rgb? ResolveToken(string? token, Rgb[] palette, Rgb? fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
            return fallback;

        if (token.StartsWith('#') && token.Length == 7
            && TryParseHex(token, out var rgb))
        {
            return rgb;
        }

        if (token.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = token.AsSpan("palette:".Length);
            if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                && idx is >= 0 and < PaletteSize
                && palette.Length > idx)
            {
                return palette[idx];
            }

            return fallback;
        }

        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare)
            && bare is >= 0 and < PaletteSize
            && palette.Length > bare)
        {
            return palette[bare];
        }

        return fallback;
    }

    public static bool TryParseHex(string token, out Rgb rgb)
    {
        rgb = default;
        if (token.Length != 7 || token[0] != '#')
            return false;
        if (!byte.TryParse(token.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(token.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(token.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        rgb = new Rgb(r, g, b);
        return true;
    }

    /// <summary>Xterm-style 256-colour cube used when native default palette is unavailable.</summary>
    public static Rgb[] CreateXtermPalette()
    {
        var palette = new Rgb[PaletteSize];
        ReadOnlySpan<Rgb> base16 =
        [
            new(0x00, 0x00, 0x00),
            new(0x80, 0x00, 0x00),
            new(0x00, 0x80, 0x00),
            new(0x80, 0x80, 0x00),
            new(0x00, 0x00, 0x80),
            new(0x80, 0x00, 0x80),
            new(0x00, 0x80, 0x80),
            new(0xC0, 0xC0, 0xC0),
            new(0x80, 0x80, 0x80),
            new(0xFF, 0x00, 0x00),
            new(0x00, 0xFF, 0x00),
            new(0xFF, 0xFF, 0x00),
            new(0x00, 0x00, 0xFF),
            new(0xFF, 0x00, 0xFF),
            new(0x00, 0xFF, 0xFF),
            new(0xFF, 0xFF, 0xFF),
        ];
        for (var i = 0; i < 16; i++)
            palette[i] = base16[i];

        var levels = new byte[] { 0, 95, 135, 175, 215, 255 };
        var idx = 16;
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                    palette[idx++] = new Rgb(levels[r], levels[g], levels[b]);
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + (i * 10));
            palette[232 + i] = new Rgb(v, v, v);
        }

        return palette;
    }
}
