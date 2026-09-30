using System.Globalization;
using System.Text;

namespace Hypa.AgentRuntime.Domain.Theme;

/// <summary>
// / Host light/dark and OSC 4 palette query/parse.
/// <c>src/terminal_theme.rs:82-92</c> and <c>:108-115</c>.
/// </summary>
public static class HostThemeParser
{
    public const string QueryColorScheme = "\u001b[?996n";
    public const string EnableReports = "\u001b[?2031h";
    public const string DisableReports = "\u001b[?2031l";
    public const string Osc10Query = "\u001b]10;?\u001b\\";
    public const string Osc11Query = "\u001b]11;?\u001b\\";

    public const string DarkReport = "\u001b[?997;1n";
    public const string LightReport = "\u001b[?997;2n";

    /// <summary>
    /// Write after the attach overlay: enable 2031, query 996, query OSC 10/11
    /// and OSC 4 for indices 0–255. Unix Ghostty attach always includes palette
    /// </summary>
    public static readonly string AttachStartSequence =
        EnableReports + QueryColorScheme + HostTerminalThemeQuerySequence(includePalette: true);

    /// <summary>
    /// <c>host_terminal_theme_query_sequence</c>. OSC 10/11, then OSC 4
    /// query <c>ESC]4;{index};? ST</c> for <c>0..=255</c> when
    /// <paramref name="includePalette"/> is true. Without <c>?</c> OSC 4
    /// is a colour set, not a query.
    /// </summary>
    public static string HostTerminalThemeQuerySequence(bool includePalette)
    {
        if (!includePalette)
            return Osc10Query + Osc11Query;

        var sequence = new StringBuilder(Osc10Query.Length + Osc11Query.Length + (256 * 12));
        sequence.Append(Osc10Query);
        sequence.Append(Osc11Query);
        for (var index = 0; index <= 255; index++)
        {
            sequence.Append("\u001b]4;");
            sequence.Append(index);
            sequence.Append(";?\u001b\\");
        }

        return sequence.ToString();
    }

    /// <summary>
    /// </summary>
    public static string OscSetDefaultColorSequence(HostDefaultColorKind kind, HostRgb color) =>
        $"\u001b]{(int)kind};rgb:{color.R:x2}/{color.G:x2}/{color.B:x2}\u001b\\";

    /// <summary>
    /// </summary>
    public static string OscResetDefaultColorSequence(HostDefaultColorKind kind) =>
        kind is HostDefaultColorKind.Foreground ? "\u001b]110\u001b\\" : "\u001b]111\u001b\\";

    /// <summary>
    /// Each component is <c>byte * 257</c> so 8-bit colour fills 16-bit OSC.
    /// </summary>
    public static byte[] OscRgbResponse(HostDefaultColorKind kind, HostRgb color)
    {
        var r = (ushort)(color.R * 257);
        var g = (ushort)(color.G * 257);
        var b = (ushort)(color.B * 257);
        return Encoding.ASCII.GetBytes(
            $"\u001b]{(int)kind};rgb:{r:x4}/{g:x4}/{b:x4}\u001b\\");
    }

    public static bool TryParseColorSchemeReport(ReadOnlySpan<byte> sequence, out HostAppearance appearance)
    {
        appearance = HostAppearance.Dark;
        if (sequence.SequenceEqual("\u001b[?997;1n"u8))
        {
            appearance = HostAppearance.Dark;
            return true;
        }

        if (sequence.SequenceEqual("\u001b[?997;2n"u8))
        {
            appearance = HostAppearance.Light;
            return true;
        }

        return false;
    }

    public static bool TryParseColorSchemeReport(string? sequence, out HostAppearance appearance)
    {
        appearance = HostAppearance.Dark;
        if (string.IsNullOrEmpty(sequence))
            return false;
        return TryParseColorSchemeReport(Encoding.ASCII.GetBytes(sequence), out appearance);
    }

    public static bool TryParseOscDefaultColor(
        ReadOnlySpan<byte> sequence,
        out HostDefaultColorKind kind,
        out HostRgb color)
    {
        kind = HostDefaultColorKind.Foreground;
        color = default;
        if (sequence.Length < 6 || sequence[0] != 0x1b || sequence[1] != (byte)']')
            return false;

        var ended = OscTerminatorLength(sequence);
        if (ended <= 0)
            return false;

        var body = sequence[2..(sequence.Length - ended)];
        return TryParseOscBody(body, out kind, out color);
    }

    public static bool TryParseOscDefaultColor(
        string? sequence,
        out HostDefaultColorKind kind,
        out HostRgb color)
    {
        kind = HostDefaultColorKind.Foreground;
        color = default;
        if (string.IsNullOrEmpty(sequence))
            return false;
        return TryParseOscDefaultColor(Encoding.UTF8.GetBytes(sequence), out kind, out color);
    }

    /// <summary>
    /// <c>parse_palette_color_response</c>. Prefix <c>ESC]4;</c>, ST or BEL,
    /// index as <c>u8</c> (0–255). Malformed RGB is false.
    /// </summary>
    public static bool TryParseOscPaletteColor(
        ReadOnlySpan<byte> sequence,
        out byte index,
        out HostRgb color)
    {
        index = 0;
        color = default;
        if (sequence.Length < 6
            || sequence[0] != 0x1b
            || sequence[1] != (byte)']'
            || sequence[2] != (byte)'4'
            || sequence[3] != (byte)';')
        {
            return false;
        }

        var ended = OscTerminatorLength(sequence);
        if (ended <= 0)
            return false;

        var body = sequence[4..(sequence.Length - ended)];
        var text = Encoding.ASCII.GetString(body);
        var split = text.IndexOf(';');
        if (split < 0)
            return false;
        if (!byte.TryParse(
                text.AsSpan(0, split),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index))
        {
            return false;
        }

        return TryParseRgbColor(text[(split + 1)..], out color);
    }

    public static bool TryParseOscPaletteColor(
        string? sequence,
        out byte index,
        out HostRgb color)
    {
        index = 0;
        color = default;
        if (string.IsNullOrEmpty(sequence))
            return false;
        return TryParseOscPaletteColor(Encoding.UTF8.GetBytes(sequence), out index, out color);
    }

    public static bool IsOscTerminated(ReadOnlySpan<byte> sequence) =>
        OscTerminatorLength(sequence) > 0;

    public static HostAppearance InferAppearance(byte r, byte g, byte b) =>
        new HostRgb(r, g, b).InferredAppearance();

    private static int OscTerminatorLength(ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length >= 1 && sequence[^1] == 0x07)
            return 1;
        if (sequence.Length >= 2 && sequence[^2] == 0x1b && sequence[^1] == (byte)'\\')
            return 2;
        return 0;
    }

    private static bool TryParseOscBody(
        ReadOnlySpan<byte> body,
        out HostDefaultColorKind kind,
        out HostRgb color)
    {
        kind = HostDefaultColorKind.Foreground;
        color = default;
        var text = Encoding.ASCII.GetString(body);
        var split = text.IndexOf(';');
        if (split <= 0)
            return false;

        var command = text[..split];
        var value = text[(split + 1)..];
        if (command == "10")
            kind = HostDefaultColorKind.Foreground;
        else if (command == "11")
            kind = HostDefaultColorKind.Background;
        else
            return false;

        return TryParseRgbColor(value, out color);
    }

    public static bool TryParseRgbColor(string value, out HostRgb color)
    {
        color = default;
        value = value.Trim();
        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value[4..].Split('/');
            if (parts.Length != 3)
                return false;
            if (!TryParseHexComponent(parts[0], out var r)
                || !TryParseHexComponent(parts[1], out var g)
                || !TryParseHexComponent(parts[2], out var b))
            {
                return false;
            }

            color = new HostRgb(r, g, b);
            return true;
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            var digits = hex.Length / 3;
            if (digits is < 1 or > 4 || hex.Length != digits * 3)
                return false;
            if (!TryParseHexComponent(hex[..digits], out var r)
                || !TryParseHexComponent(hex.Substring(digits, digits), out var g)
                || !TryParseHexComponent(hex[(digits * 2)..], out var b))
            {
                return false;
            }

            color = new HostRgb(r, g, b);
            return true;
        }

        return false;
    }

    internal static bool TryParseHexComponent(string component, out byte value)
    {
        value = 0;
        if (string.IsNullOrEmpty(component)
            || component.Length > 4
            || !IsHex(component))
        {
            return false;
        }

        if (!uint.TryParse(component, System.Globalization.NumberStyles.HexNumber, null, out var raw))
            return false;
        var max = (1u << (component.Length * 4)) - 1;
        value = (byte)((raw * 255 + (max / 2)) / max);
        return true;
    }

    private static bool IsHex(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
                return false;
        }

        return true;
    }
}
