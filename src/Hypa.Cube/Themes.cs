using System;

namespace HypaCube;

public readonly record struct Rgb(byte R, byte G, byte B);

public sealed class Theme
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Rgb Bg { get; init; }
    public required Rgb OuterNear { get; init; }
    public required Rgb OuterFar { get; init; }
    public required Rgb InnerNear { get; init; }
    public required Rgb InnerFar { get; init; }
    public required Rgb HyperNear { get; init; }
    public required Rgb HyperFar { get; init; }
    public required Rgb Hud { get; init; }
    public required Rgb HudDim { get; init; }
    public required Rgb HudAccent { get; init; }
}

public static class Themes
{
    public static readonly Theme[] All =
    [
        new Theme
        {
            Id = "phosphor",
            Label = "PHOSPHOR",
            Bg = new Rgb(5, 8, 5),
            OuterNear = new Rgb(108, 255, 154),
            OuterFar = new Rgb(12, 61, 34),
            InnerNear = new Rgb(210, 255, 220),
            InnerFar = new Rgb(28, 90, 56),
            HyperNear = new Rgb(61, 255, 200),
            HyperFar = new Rgb(10, 74, 64),
            Hud = new Rgb(180, 230, 196),
            HudDim = new Rgb(90, 130, 104),
            HudAccent = new Rgb(108, 255, 154),
        },
        new Theme
        {
            Id = "amber",
            Label = "AMBER",
            Bg = new Rgb(10, 8, 4),
            OuterNear = new Rgb(255, 176, 32),
            OuterFar = new Rgb(92, 48, 8),
            InnerNear = new Rgb(255, 220, 140),
            InnerFar = new Rgb(110, 64, 16),
            HyperNear = new Rgb(255, 122, 26),
            HyperFar = new Rgb(96, 36, 8),
            Hud = new Rgb(255, 206, 130),
            HudDim = new Rgb(150, 110, 56),
            HudAccent = new Rgb(255, 176, 32),
        },
        new Theme
        {
            Id = "ice",
            Label = "ICE",
            Bg = new Rgb(5, 7, 12),
            OuterNear = new Rgb(200, 220, 255),
            OuterFar = new Rgb(28, 48, 92),
            InnerNear = new Rgb(138, 180, 255),
            InnerFar = new Rgb(24, 52, 110),
            HyperNear = new Rgb(126, 231, 255),
            HyperFar = new Rgb(16, 72, 96),
            Hud = new Rgb(198, 214, 232),
            HudDim = new Rgb(96, 118, 148),
            HudAccent = new Rgb(126, 231, 255),
        },
        new Theme
        {
            Id = "helium",
            Label = "HELIUM",
            Bg = new Rgb(6, 8, 10),
            OuterNear = new Rgb(94, 234, 212),
            OuterFar = new Rgb(16, 72, 70),
            InnerNear = new Rgb(240, 194, 122),
            InnerFar = new Rgb(92, 58, 22),
            HyperNear = new Rgb(232, 255, 232),
            HyperFar = new Rgb(48, 80, 52),
            Hud = new Rgb(214, 228, 220),
            HudDim = new Rgb(110, 128, 124),
            HudAccent = new Rgb(94, 234, 212),
        },
    ];

    public static Theme ById(string id)
    {
        foreach (Theme t in All)
        {
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return All[0];
    }

    public static Theme Next(Theme current)
    {
        int i = Array.IndexOf(All, current);
        if (i < 0)
        {
            return All[0];
        }

        return All[(i + 1) % All.Length];
    }
}
