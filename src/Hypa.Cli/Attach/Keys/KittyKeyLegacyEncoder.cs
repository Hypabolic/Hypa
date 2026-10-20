using System.Globalization;
using System.Text;

namespace Hypa.Cli.Attach.Keys;

/// <summary>
/// Converts a kitty keyboard report from the host into the legacy bytes that
/// the same key gives with no keyboard protocol. The attach pushes kitty flag
/// 1 (disambiguate) on the host. A pane that did not ask for kitty key codes
/// must still get legacy bytes.
/// </summary>
/// <remarks>
/// The attach cannot see the kitty flags of a pane. Thus it always converts.
/// The pane then gets the same bytes that a legacy host sends today.
/// </remarks>
public static class KittyKeyLegacyEncoder
{
    private const int ShiftBit = 1;
    private const int AltBit = 2;
    private const int CtrlBit = 4;

    /// <summary>
    /// <paramref name="raw"/> is a key report that only a kitty host sends:
    /// <c>CSI ... u</c>, <c>CSI P</c>, <c>CSI Q</c>, <c>CSI S</c> (F1, F2, F4
    /// with no modifier) or <c>CSI 13 ~</c> (F3). Then
    /// <paramref name="legacy"/> is the legacy encoding. It is empty for a key
    /// that has no legacy encoding (for example a media key or F13).
    /// </summary>
    /// <returns>False when <paramref name="raw"/> is not a kitty-only form.
    /// The caller then keeps <paramref name="raw"/>.</returns>
    public static bool TryEncodeLegacy(ReadOnlySpan<byte> raw, out byte[] legacy)
    {
        legacy = [];
        if (raw.Length < 3 || raw[0] != 0x1B || raw[1] != (byte)'[')
            return false;
        var final = raw[^1];
        var body = raw[2..^1];
        if (body.Length > 0 && body[0] is (byte)'?' or (byte)'>' or (byte)'<' or (byte)'=')
            return false;

        if (final is (byte)'P' or (byte)'Q' or (byte)'S' && body.IsEmpty)
        {
            legacy = [0x1B, (byte)'O', final];
            return true;
        }

        if (final == (byte)'~')
        {
            if (!TryParse(body, out var tilde) || tilde.Code != 13)
                return false;
            var mods = tilde.Modifiers & (ShiftBit | AltBit | CtrlBit);
            legacy = mods == 0
                ? [0x1B, (byte)'O', (byte)'R']
                : Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\u001b[1;{mods + 1}R"));
            return true;
        }

        if (final != (byte)'u')
            return false;
        if (!TryParse(body, out var report))
        {
            legacy = [];
            return true;
        }

        legacy = EncodeReport(report);
        return true;
    }

    private static byte[] EncodeReport(KittyReport report)
    {
        var mods = report.Modifiers;
        var shift = (mods & ShiftBit) != 0;
        var alt = (mods & AltBit) != 0;
        var ctrl = (mods & CtrlBit) != 0;
        var code = report.Code;

        switch (code)
        {
            case 27:
                return WithAlt(alt, [0x1B]);
            case 13 or 57414:
                return WithAlt(alt, [0x0D]);
            case 9:
                return WithAlt(alt, shift ? [0x1B, (byte)'[', (byte)'Z'] : [0x09]);
            case 127 or 8:
                return WithAlt(alt, ctrl ? [0x08] : [0x7F]);
            case 32:
                return WithAlt(alt, ctrl ? [0x00] : [(byte)' ']);
        }

        if (KeypadCursor(code) is { } cursor)
            return cursor.Encode(mods & (ShiftBit | AltBit | CtrlBit));

        if (KeypadText(code) is { } padText)
            code = padText;

        // The private use area holds functional keys that legacy input
        // cannot send: F13 and up, media keys, lock and modifier keys.
        if (code is >= 0xE000 and <= 0xF8FF || code < 32 || code == 127 || code > 0x10FFFF)
            return [];

        if (ctrl && CtrlByte(code) is { } control)
            return WithAlt(alt, [control]);

        var text = code;
        if (shift)
            text = report.Shifted > 0 ? report.Shifted : ShiftedUs(code);
        string value;
        try
        {
            value = char.ConvertFromUtf32(text);
        }
        catch (ArgumentOutOfRangeException)
        {
            return [];
        }

        return WithAlt(alt, Encoding.UTF8.GetBytes(value));
    }

    private static byte[] WithAlt(bool alt, byte[] body)
    {
        if (!alt || body.Length == 0)
            return body;
        var prefixed = new byte[body.Length + 1];
        prefixed[0] = 0x1B;
        Buffer.BlockCopy(body, 0, prefixed, 1, body.Length);
        return prefixed;
    }

    /// <summary>
    /// The legacy control byte for ctrl plus <paramref name="code"/>. Kitty
    /// keyboard protocol, "Legacy ctrl mapping of ASCII keys".
    /// </summary>
    private static byte? CtrlByte(int code) => code switch
    {
        >= 'a' and <= 'z' => (byte)(code - 'a' + 1),
        >= 'A' and <= 'Z' => (byte)(code - 'A' + 1),
        '@' or '2' => 0x00,
        '[' or '3' => 0x1B,
        '\\' or '4' => 0x1C,
        ']' or '5' => 0x1D,
        '^' or '6' or '~' => 0x1E,
        '_' or '7' or '/' or '-' => 0x1F,
        '8' or '?' => 0x7F,
        _ => null,
    };

    /// <summary>The shifted character on a US layout, for a report with no alternate key.</summary>
    private static int ShiftedUs(int code) => code switch
    {
        >= 'a' and <= 'z' => code - 32,
        '1' => '!',
        '2' => '@',
        '3' => '#',
        '4' => '$',
        '5' => '%',
        '6' => '^',
        '7' => '&',
        '8' => '*',
        '9' => '(',
        '0' => ')',
        '-' => '_',
        '=' => '+',
        '[' => '{',
        ']' => '}',
        '\\' => '|',
        ';' => ':',
        '\'' => '"',
        ',' => '<',
        '.' => '>',
        '/' => '?',
        '`' => '~',
        _ => code,
    };

    private static int? KeypadText(int code) => code switch
    {
        >= 57399 and <= 57408 => '0' + (code - 57399),
        57409 => '.',
        57410 => '/',
        57411 => '*',
        57412 => '-',
        57413 => '+',
        57415 => '=',
        57416 => ',',
        _ => null,
    };

    private static LegacyCursor? KeypadCursor(int code) => code switch
    {
        57417 => new LegacyCursor('D', 0),
        57418 => new LegacyCursor('C', 0),
        57419 => new LegacyCursor('A', 0),
        57420 => new LegacyCursor('B', 0),
        57421 => new LegacyCursor('~', 5),
        57422 => new LegacyCursor('~', 6),
        57423 => new LegacyCursor('H', 0),
        57424 => new LegacyCursor('F', 0),
        57425 => new LegacyCursor('~', 2),
        57426 => new LegacyCursor('~', 3),
        57427 => new LegacyCursor('E', 0),
        _ => null,
    };

    /// <summary>
    /// Parse <c>code[:shifted[:base]][;mods[:event][;text]]</c>. Lock bits
    /// (caps lock, num lock) and super, hyper and meta are not in
    /// <see cref="KittyReport.Modifiers"/>, because legacy input has no
    /// encoding for them.
    /// </summary>
    private static bool TryParse(ReadOnlySpan<byte> body, out KittyReport report)
    {
        report = default;
        Span<int> values = stackalloc int[6];
        values.Clear();
        var field = 0;
        var sub = 0;
        var any = false;
        foreach (var b in body)
        {
            if (b == (byte)';')
            {
                field++;
                sub = 0;
                if (field > 2)
                    break;
                continue;
            }

            if (b == (byte)':')
            {
                sub++;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'9')
                return false;
            var slot = field switch
            {
                0 when sub <= 1 => sub,
                1 when sub == 0 => 2,
                _ => -1,
            };
            if (slot < 0)
                continue;
            any = true;
            var next = (values[slot] * 10) + (b - '0');
            if (next > 0x10FFFF)
                return false;
            values[slot] = next;
        }

        if (!any)
            return false;
        var mods = values[2] > 1 ? values[2] - 1 : 0;
        report = new KittyReport(values[0], values[1], mods & (ShiftBit | AltBit | CtrlBit));
        return true;
    }

    private readonly record struct KittyReport(int Code, int Shifted, int Modifiers);

    private readonly record struct LegacyCursor(char Final, int Number)
    {
        public byte[] Encode(int mods)
        {
            var param = mods + 1;
            if (Final == '~')
            {
                return Encoding.ASCII.GetBytes(param > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"\u001b[{Number};{param}~")
                    : string.Create(CultureInfo.InvariantCulture, $"\u001b[{Number}~"));
            }

            return Encoding.ASCII.GetBytes(param > 1
                ? string.Create(CultureInfo.InvariantCulture, $"\u001b[1;{param}{Final}")
                : string.Create(CultureInfo.InvariantCulture, $"\u001b[{Final}"));
        }
    }
}
