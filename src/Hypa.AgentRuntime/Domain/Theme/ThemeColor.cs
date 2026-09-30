namespace Hypa.AgentRuntime.Domain.Theme;

public enum ThemeColorKind
{
    Reset = 0,
    Ansi = 1,
    Rgb = 2,
}

public enum ThemeAnsiColor
{
    Black,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,
    White,
    Gray,
    DarkGray,
    LightRed,
    LightGreen,
    LightYellow,
    LightBlue,
    LightMagenta,
    LightCyan,
}

/// <summary>One palette token. Reset and named ANSI follow the host 16-color set.</summary>
public readonly record struct ThemeColor
{
    public ThemeColorKind Kind { get; }
    public ThemeAnsiColor Ansi { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    private ThemeColor(ThemeColorKind kind, ThemeAnsiColor ansi, byte r, byte g, byte b)
    {
        Kind = kind;
        Ansi = ansi;
        R = r;
        G = g;
        B = b;
    }

    public static ThemeColor Reset { get; } = new(ThemeColorKind.Reset, default, 0, 0, 0);

    public static ThemeColor Named(ThemeAnsiColor ansi) =>
        new(ThemeColorKind.Ansi, ansi, 0, 0, 0);

    public static ThemeColor Rgb(byte r, byte g, byte b) =>
        new(ThemeColorKind.Rgb, default, r, g, b);

    public static ThemeColor Black { get; } = Named(ThemeAnsiColor.Black);
    public static ThemeColor Red { get; } = Named(ThemeAnsiColor.Red);
    public static ThemeColor Green { get; } = Named(ThemeAnsiColor.Green);
    public static ThemeColor Yellow { get; } = Named(ThemeAnsiColor.Yellow);
    public static ThemeColor Blue { get; } = Named(ThemeAnsiColor.Blue);
    public static ThemeColor Magenta { get; } = Named(ThemeAnsiColor.Magenta);
    public static ThemeColor Cyan { get; } = Named(ThemeAnsiColor.Cyan);
    public static ThemeColor White { get; } = Named(ThemeAnsiColor.White);
    public static ThemeColor Gray { get; } = Named(ThemeAnsiColor.Gray);
    public static ThemeColor DarkGray { get; } = Named(ThemeAnsiColor.DarkGray);
    public static ThemeColor LightRed { get; } = Named(ThemeAnsiColor.LightRed);
    public static ThemeColor LightGreen { get; } = Named(ThemeAnsiColor.LightGreen);
    public static ThemeColor LightYellow { get; } = Named(ThemeAnsiColor.LightYellow);
    public static ThemeColor LightBlue { get; } = Named(ThemeAnsiColor.LightBlue);
    public static ThemeColor LightMagenta { get; } = Named(ThemeAnsiColor.LightMagenta);
    public static ThemeColor LightCyan { get; } = Named(ThemeAnsiColor.LightCyan);

    public bool IsRgb => Kind is ThemeColorKind.Rgb;
    public bool IsAnsi => Kind is ThemeColorKind.Ansi;
    public bool IsReset => Kind is ThemeColorKind.Reset;
}
