using System.Globalization;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Packed cell colour.
/// <c>fg</c>/<c>bg</c> as <c>u32</c>. Tags copy
/// <c>src/protocol/wire.rs:768-791</c> <c>color_to_u32</c>: named
/// <c>0x00_00_00_XX</c>, indexed <c>0x01_00_00_XX</c>, RGB <c>0x02_RR_GG_BB</c>.
/// </summary>
public static class VtColorPack
{
    public const uint None = 0;
    public const uint PaletteMask = 0xFF00_0000;
    public const uint PaletteTag = 0x0100_0000;
    public const uint RgbTag = 0x0200_0000;

    private static readonly string[] PaletteWire = CreatePaletteWire();

    public static uint FromRgb(byte r, byte g, byte b) =>
        RgbTag | ((uint)r << 16) | ((uint)g << 8) | b;

    public static uint FromPalette(int index)
    {
        if ((uint)index > 255)
            return None;
        return PaletteTag | (uint)index;
    }

    /// <summary>
    /// (<c>src/protocol/wire.rs:770-786</c>).
    /// </summary>
    public static uint FromNamed(byte ansi)
    {
        if (ansi > 16)
            return None;
        return ansi;
    }

    public static bool IsRgb(uint packed) => (packed & PaletteMask) == RgbTag;

    public static bool IsPalette(uint packed) => (packed & PaletteMask) == PaletteTag;

    public static bool IsNamed(uint packed) =>
        packed != None && (packed & PaletteMask) == 0 && (packed & 0xFF) <= 16;

    public static int PaletteIndex(uint packed) => (int)(packed & 0xFF);

    public static int NamedIndex(uint packed) => (int)(packed & 0xFF);

    public static void GetRgb(uint packed, out byte r, out byte g, out byte b)
    {
        r = (byte)((packed >> 16) & 0xFF);
        g = (byte)((packed >> 8) & 0xFF);
        b = (byte)(packed & 0xFF);
    }

    public static uint Parse(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return None;
        if (wire.Length == 7 && wire[0] == '#'
            && byte.TryParse(wire.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(wire.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(wire.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return FromRgb(r, g, b);
        }

        const string palettePrefix = "palette:";
        if (wire.StartsWith(palettePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                wire.AsSpan(palettePrefix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var palette)
            && palette is >= 0 and <= 255)
        {
            return FromPalette(palette);
        }

        if (int.TryParse(wire, NumberStyles.Integer, CultureInfo.InvariantCulture, out var indexed)
            && indexed is >= 0 and <= 255)
        {
            return FromPalette(indexed);
        }

        return None;
    }

    public static string? ToWire(uint packed)
    {
        if (packed == None)
            return null;
        if (IsPalette(packed))
            return PaletteWire[PaletteIndex(packed)];
        if (!IsRgb(packed))
            return null;
        GetRgb(packed, out var r, out var g, out var b);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static string[] CreatePaletteWire()
    {
        var wires = new string[256];
        for (var i = 0; i < wires.Length; i++)
            wires[i] = "palette:" + i.ToString(CultureInfo.InvariantCulture);
        return wires;
    }
}

// / <summary>Packed SGR flags.
public static class VtStyleBits
{
    public const ushort Bold = 1 << 0;
    public const ushort Dim = 1 << 1;
    public const ushort Italic = 1 << 2;
    public const ushort Underline = 1 << 3;
    public const ushort Inverse = 1 << 4;
    public const ushort Invisible = 1 << 5;
    public const ushort Strikethrough = 1 << 6;
    public const ushort Blink = 1 << 7;
    public const ushort Overline = 1 << 8;

    public static ushort Pack(
        bool bold,
        bool dim,
        bool italic,
        bool underline,
        bool inverse,
        bool invisible,
        bool strikethrough,
        bool blink,
        bool overline)
    {
        ushort bits = 0;
        if (bold)
            bits |= Bold;
        if (dim)
            bits |= Dim;
        if (italic)
            bits |= Italic;
        if (underline)
            bits |= Underline;
        if (inverse)
            bits |= Inverse;
        if (invisible)
            bits |= Invisible;
        if (strikethrough)
            bits |= Strikethrough;
        if (blink)
            bits |= Blink;
        if (overline)
            bits |= Overline;
        return bits;
    }
}

/// <summary>Interned packed style for one stamp. Reused across paints.</summary>
public readonly record struct VtPackedStyle(
    uint Fg,
    uint Bg,
    uint UnderlineColor,
    ushort Modifier,
    byte UnderlineStyle);

/// <summary>
// / Per-pane style intern.
/// (<c>src/pane/terminal.rs:2131-2173</c>); Hypa intern is the style index
/// on each value cell.
/// </summary>
public sealed class VtStyleTable
{
    private VtPackedStyle[] _items = new VtPackedStyle[16];
    private int _count;

    public int Count => _count;

    public void BeginFrame() => _count = 0;

    public ushort Intern(in VtPackedStyle style)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_items[i].Equals(style))
                return (ushort)i;
        }

        if (_count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);
        _items[_count] = style;
        return (ushort)_count++;
    }

    public VtPackedStyle[] CopyEntries()
    {
        if (_count == 0)
            return [new VtPackedStyle(0, 0, 0, 0, 0)];
        var copy = new VtPackedStyle[_count];
        Array.Copy(_items, copy, _count);
        return copy;
    }
}

/// <summary>Intern single ASCII glyphs so a full stamp does not allocate them.</summary>
public static class VtGlyphIntern
{
    private static readonly string[] Ascii = CreateAscii();

    public static string Space => Ascii[' '];

    public static string FromIndex(int index) => Ascii[index];

    public static int AsciiIndex(string glyph)
    {
        if (glyph.Length == 1 && glyph[0] <= 127)
            return glyph[0];
        return -1;
    }

    public static string Intern(char ch) =>
        ch <= 127 ? Ascii[ch] : ch.ToString();

    public static string Intern(string text)
    {
        if (text.Length == 1 && text[0] <= 127)
            return Ascii[text[0]];
        return text;
    }

    private static string[] CreateAscii()
    {
        var glyphs = new string[128];
        for (var i = 0; i < glyphs.Length; i++)
            glyphs[i] = ((char)i).ToString();
        return glyphs;
    }
}
