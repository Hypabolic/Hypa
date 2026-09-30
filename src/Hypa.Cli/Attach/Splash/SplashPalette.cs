using HypaCube;

namespace Hypa.Cli.Attach.Splash;

internal static class SplashPalette
{
    public static Rgb Foreground(HypaCube.Theme theme, byte ci)
    {
        if (ci == CubeEngine.PalHud)
            return theme.Hud;
        if (ci == CubeEngine.PalDim)
            return theme.HudDim;
        if (ci == CubeEngine.PalAccent)
            return theme.HudAccent;
        if (ci == CubeEngine.PalOk)
            return theme.HyperNear;

        var hue = ci / CubeEngine.Levels;
        var level = ci % CubeEngine.Levels;
        (Rgb far, Rgb near) = hue switch
        {
            1 => (theme.InnerFar, theme.InnerNear),
            2 => (theme.HyperFar, theme.HyperNear),
            _ => (theme.OuterFar, theme.OuterNear),
        };
        var t = CubeEngine.Levels <= 1 ? 1f : level / (float)(CubeEngine.Levels - 1);
        var g = MathF.Pow(t, 0.72f);
        return Mix(Mix(theme.Bg, far, 0.55f), near, g);
    }

    private static Rgb Mix(Rgb a, Rgb b, float t) =>
        new(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
}
