using Hypa.AgentRuntime.Application;

namespace Hypa.App;

internal readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

internal static class ColorResolve
{
    public static Rgba Resolve(uint packed, Rgba fallback)
    {
        if (packed == VtColorPack.None)
            return fallback;
        if (VtColorPack.IsRgb(packed))
        {
            VtColorPack.GetRgb(packed, out var r, out var g, out var b);
            return new Rgba(r, g, b);
        }

        if (VtColorPack.IsPalette(packed))
            return Palette256(VtColorPack.PaletteIndex(packed));
        if (VtColorPack.IsNamed(packed))
        {
            ReadOnlySpan<Rgba> ansi =
            [
                new(0, 0, 0), new(0, 0, 0), new(205, 0, 0), new(0, 205, 0),
                new(205, 205, 0), new(0, 0, 238), new(205, 0, 205), new(0, 205, 205),
                new(229, 229, 229), new(127, 127, 127), new(255, 0, 0), new(0, 255, 0),
                new(255, 255, 0), new(92, 92, 255), new(255, 0, 255), new(0, 255, 255),
                new(255, 255, 255),
            ];
            var i = VtColorPack.NamedIndex(packed);
            return (uint)i < (uint)ansi.Length ? ansi[i] : fallback;
        }

        return fallback;
    }

    private static Rgba Palette256(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16)
        {
            ReadOnlySpan<Rgba> base16 =
            [
                new(0, 0, 0), new(205, 0, 0), new(0, 205, 0), new(205, 205, 0),
                new(0, 0, 238), new(205, 0, 205), new(0, 205, 205), new(229, 229, 229),
                new(127, 127, 127), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0),
                new(92, 92, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255),
            ];
            return base16[index];
        }

        if (index < 232)
        {
            var v = index - 16;
            var r = v / 36;
            var g = (v / 6) % 6;
            var b = v % 6;
            return new Rgba(
                (byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40),
                (byte)(b == 0 ? 0 : 55 + b * 40));
        }

        var grey = (byte)(8 + (index - 232) * 10);
        return new Rgba(grey, grey, grey);
    }
}
