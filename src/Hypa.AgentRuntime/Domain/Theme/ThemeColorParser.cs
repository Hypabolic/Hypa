namespace Hypa.AgentRuntime.Domain.Theme;

/// <summary>Fail-closed color parse. Unknown text does not become cyan.</summary>
public static class ThemeColorParser
{
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = ThemeColor.Reset;
        if (text is null)
            return false;

        var s = text.Trim();
        if (s.Length == 0)
            return false;

        var lower = s.ToLowerInvariant();
        switch (lower)
        {
            case "reset":
            case "default":
            case "none":
            case "transparent":
                color = ThemeColor.Reset;
                return true;
        }

        if (lower.StartsWith('#') && TryParseHex(lower.AsSpan(1), out color))
            return true;

        if (TryParseRgbFunction(lower, out color))
            return true;

        if (TryParseNamed(lower, out color))
            return true;

        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out ThemeColor color)
    {
        color = ThemeColor.Reset;
        if (hex.Length == 6
            && TryHexByte(hex[..2], out var r)
            && TryHexByte(hex[2..4], out var g)
            && TryHexByte(hex[4..6], out var b))
        {
            color = ThemeColor.Rgb(r, g, b);
            return true;
        }

        if (hex.Length == 3
            && TryHexNibble(hex[0], out var rn)
            && TryHexNibble(hex[1], out var gn)
            && TryHexNibble(hex[2], out var bn))
        {
            color = ThemeColor.Rgb((byte)(rn * 17), (byte)(gn * 17), (byte)(bn * 17));
            return true;
        }

        return false;
    }

    private static bool TryParseRgbFunction(string lower, out ThemeColor color)
    {
        color = ThemeColor.Reset;
        if (!lower.StartsWith("rgb(", StringComparison.Ordinal) || !lower.EndsWith(')'))
            return false;

        var inner = lower.AsSpan("rgb(".Length, lower.Length - "rgb(".Length - 1);
        var parts = inner.ToString().Split(',');
        if (parts.Length != 3)
            return false;
        if (!byte.TryParse(parts[0].Trim(), out var r)
            || !byte.TryParse(parts[1].Trim(), out var g)
            || !byte.TryParse(parts[2].Trim(), out var b))
        {
            return false;
        }

        color = ThemeColor.Rgb(r, g, b);
        return true;
    }

    private static bool TryParseNamed(string lower, out ThemeColor color)
    {
        color = lower switch
        {
            "black" => ThemeColor.Black,
            "red" => ThemeColor.Red,
            "green" => ThemeColor.Green,
            "yellow" => ThemeColor.Yellow,
            "blue" => ThemeColor.Blue,
            "magenta" or "purple" => ThemeColor.Magenta,
            "cyan" => ThemeColor.Cyan,
            "white" => ThemeColor.White,
            "gray" or "grey" => ThemeColor.Gray,
            "darkgray" or "darkgrey" => ThemeColor.DarkGray,
            "lightred" => ThemeColor.LightRed,
            "lightgreen" => ThemeColor.LightGreen,
            "lightyellow" => ThemeColor.LightYellow,
            "lightblue" => ThemeColor.LightBlue,
            "lightmagenta" => ThemeColor.LightMagenta,
            "lightcyan" => ThemeColor.LightCyan,
            _ => default,
        };
        return color.Kind is ThemeColorKind.Ansi;
    }

    private static bool TryHexByte(ReadOnlySpan<char> text, out byte value)
    {
        value = 0;
        return text.Length == 2 && byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static bool TryHexNibble(char ch, out byte value)
    {
        value = 0;
        if (ch is >= '0' and <= '9')
        {
            value = (byte)(ch - '0');
            return true;
        }

        if (ch is >= 'a' and <= 'f')
        {
            value = (byte)(ch - 'a' + 10);
            return true;
        }

        return false;
    }
}
