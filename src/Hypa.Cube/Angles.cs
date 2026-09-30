using System;

namespace HypaCube;

public struct Angles
{
    public float Xy;
    public float Xz;
    public float Yz;
    public float Xw;
    public float Yw;
    public float Zw;

    public static Angles Default => new()
    {
        Xy = 0.35f,
        Xz = 0.18f,
        Yz = 0.55f,
        Xw = 0.9f,
        Yw = 0.4f,
        Zw = 0.2f,
    };
}

public enum RenderMode : byte
{
    Braille = 0,
    Quad = 1,
    Block = 2,
    Ascii = 3,
}

public static class RenderModes
{
    public static readonly RenderMode[] All =
    [
        RenderMode.Braille,
        RenderMode.Quad,
        RenderMode.Block,
        RenderMode.Ascii,
    ];

    public static RenderMode Next(RenderMode mode)
    {
        int i = (int)mode;
        return All[(i + 1) % All.Length];
    }

    public static string Label(RenderMode mode) => mode switch
    {
        RenderMode.Braille => "braille",
        RenderMode.Quad => "quad",
        RenderMode.Block => "block",
        RenderMode.Ascii => "ascii",
        _ => "braille",
    };

    public static RenderMode Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return RenderMode.Braille;
        }

        return s.Trim().ToLowerInvariant() switch
        {
            "braille" or "b" => RenderMode.Braille,
            "quad" or "q" => RenderMode.Quad,
            "block" or "k" => RenderMode.Block,
            "ascii" or "a" => RenderMode.Ascii,
            _ => RenderMode.Braille,
        };
    }
}
