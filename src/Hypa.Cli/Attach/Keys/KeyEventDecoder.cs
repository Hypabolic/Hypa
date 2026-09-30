using System.Text;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Maps raw TTY bytes / assembled CSI to <see cref="KeyChord"/>.</summary>
public static class KeyEventDecoder
{
    public sealed record DecodedInput(KeyChord Chord, byte[] Raw);

    public static IReadOnlyList<DecodedInput> Decode(ReadOnlySpan<byte> bytes)
    {
        var list = new List<DecodedInput>();
        var i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == 0x1B)
            {
                var start = i;
                i++;
                if (i < bytes.Length && bytes[i] == (byte)'[')
                {
                    i++;
                    while (i < bytes.Length && !IsCsiFinal(bytes[i]))
                        i++;
                    if (i < bytes.Length)
                        i++;
                    var raw = bytes[start..i].ToArray();
                    list.Add(new DecodedInput(DecodeCsi(raw.AsSpan(1)), raw));
                    continue;
                }

                if (i < bytes.Length && bytes[i] == (byte)'O' && i + 1 < bytes.Length)
                {
                    var fn = bytes[i + 1] switch
                    {
                        (byte)'A' => "up",
                        (byte)'B' => "down",
                        (byte)'C' => "right",
                        (byte)'D' => "left",
                        (byte)'F' => "end",
                        (byte)'H' => "home",
                        (byte)'P' => "f1",
                        (byte)'Q' => "f2",
                        (byte)'R' => "f3",
                        (byte)'S' => "f4",
                        _ => null,
                    };
                    if (fn is not null)
                    {
                        var raw = bytes[start..(i + 2)].ToArray();
                        list.Add(new DecodedInput(new KeyChord(false, false, false, false, fn), raw));
                        i += 2;
                        continue;
                    }
                }

                if (i < bytes.Length)
                {
                    var inner = DecodeByte(bytes[i], alt: true);
                    var raw = bytes[start..(i + 1)].ToArray();
                    list.Add(new DecodedInput(inner.Chord, raw));
                    i++;
                    continue;
                }

                list.Add(new DecodedInput(
                    new KeyChord(false, false, false, false, "esc"),
                    [0x1B]));
                continue;
            }

            var one = DecodeByte(bytes[i], alt: false);
            list.Add(one);
            i++;
        }

        return list;
    }

    public static bool TryEncode(KeyChord chord, out byte[] bytes)
    {
        bytes = [];
        if (chord is null || !CanEncode(chord.Key))
            return false;
        bytes = Encode(chord);
        return bytes.Length > 0;
    }

    public static byte[] Encode(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (NeedsCsiU(chord))
            return EncodeCsiU(chord);

        var body = EncodeBody(chord);
        if (chord.Alt && body.Length > 0 && !IsCsiOrSs3(body))
        {
            var prefixed = new byte[body.Length + 1];
            prefixed[0] = 0x1B;
            Buffer.BlockCopy(body, 0, prefixed, 1, body.Length);
            return prefixed;
        }

        return body;
    }

    private static bool IsCsiOrSs3(byte[] body) =>
        body.Length >= 2 && body[0] == 0x1B && body[1] is (byte)'[' or (byte)'O';

    private static bool CanEncode(string key) =>
        key.Length == 1
        || key is "esc" or "enter" or "tab" or "backspace" or "space"
            or "up" or "down" or "left" or "right"
            or "minus" or "plus" or "comma" or "period" or "slash" or "?" or "[" or "]"
            or "\\" or "^" or "_"
            or "delete" or "home" or "end" or "pageup" or "pagedown" or "insert"
            or "f1" or "f2" or "f3" or "f4";

    private static byte[] EncodeBody(KeyChord chord)
    {
        var key = chord.Key;
        if (key is "esc")
            return [0x1B];
        if (key is "enter")
            return [0x0D];
        if (key is "tab" && chord.Shift)
            return [0x1B, (byte)'[', (byte)'Z'];
        if (key is "tab")
            return [0x09];
        if (key is "backspace")
            return [0x7F];
        if (key is "space")
            return chord.Ctrl ? [0x00] : [(byte)' '];
        if (key is "up" or "down" or "left" or "right")
            return EncodeArrow(key, chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "delete")
            return EncodeTilde(3, chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "insert")
            return EncodeTilde(2, chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "pageup")
            return EncodeTilde(5, chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "pagedown")
            return EncodeTilde(6, chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "home")
            return EncodeCsiFinal('H', chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "end")
            return EncodeCsiFinal('F', chord.Shift, chord.Alt, chord.Ctrl);
        if (key is "f1")
            return [0x1B, (byte)'O', (byte)'P'];
        if (key is "f2")
            return [0x1B, (byte)'O', (byte)'Q'];
        if (key is "f3")
            return [0x1B, (byte)'O', (byte)'R'];
        if (key is "f4")
            return [0x1B, (byte)'O', (byte)'S'];
        if (key is "minus")
            return chord.Ctrl ? [0x1F] : [(byte)'-'];
        if (key is "plus")
            return [(byte)'+'];
        if (key is "comma")
            return [(byte)','];
        if (key is "period")
            return [(byte)'.'];
        if (key is "slash")
            return [(byte)'/'];
        if (key is "?")
            return [(byte)'?'];
        if (key is "[")
            return chord.Ctrl ? [0x1B] : [(byte)'['];
        if (key is "]")
            return chord.Ctrl ? [0x1D] : [(byte)']'];
        if (key is "\\")
            return chord.Ctrl ? [0x1C] : [(byte)'\\'];
        if (key is "^")
            return chord.Ctrl ? [0x1E] : [(byte)'^'];
        if (key is "_")
            return chord.Ctrl ? [0x1F] : [(byte)'_'];

        if (key.Length == 1)
        {
            var ch = key[0];
            if (chord.Ctrl && char.IsAsciiLetter(ch))
                return [(byte)(char.ToUpperInvariant(ch) - 64)];
            if (chord.Shift && char.IsAsciiLetter(ch))
                return [(byte)char.ToUpperInvariant(ch)];
            return Encoding.UTF8.GetBytes(char.ToString(ch));
        }

        return [];
    }

    /// <summary>
    /// Encode an unmodified cursor key for the pane. Application cursor
    /// uses SS3. Normal mode uses CSI. A modifier keeps the xterm form.
    /// </summary>
    public static byte[] EncodePlainCursor(string key, bool applicationCursor)
    {
        var letter = CursorLetter(key);
        if (letter == 0)
            return [];
        if (applicationCursor)
            return [0x1B, (byte)'O', (byte)letter];
        return [0x1B, (byte)'[', (byte)letter];
    }

    private static char CursorLetter(string key) => key switch
    {
        "up" => 'A',
        "down" => 'B',
        "right" => 'C',
        "left" => 'D',
        "home" => 'H',
        "end" => 'F',
        _ => '\0',
    };

    private static byte[] EncodeArrow(string key, bool shift, bool alt, bool ctrl)
    {
        var letter = key switch
        {
            "up" => 'A',
            "down" => 'B',
            "right" => 'C',
            _ => 'D',
        };
        return EncodeCsiFinal(letter, shift, alt, ctrl);
    }

    private static byte[] EncodeCsiFinal(char letter, bool shift, bool alt, bool ctrl)
    {
        var modifier = CsiModifier(shift, alt, ctrl);
        if (modifier > 1)
            return Encoding.ASCII.GetBytes($"\u001b[1;{modifier}{letter}");
        return [0x1B, (byte)'[', (byte)letter];
    }

    private static byte[] EncodeTilde(int code, bool shift, bool alt, bool ctrl)
    {
        var modifier = CsiModifier(shift, alt, ctrl);
        if (modifier > 1)
            return Encoding.ASCII.GetBytes($"\u001b[{code};{modifier}~");
        return Encoding.ASCII.GetBytes($"\u001b[{code}~");
    }

    private static int CsiModifier(bool shift, bool alt, bool ctrl)
    {
        var modifier = 1;
        if (shift)
            modifier += 1;
        if (alt)
            modifier += 2;
        if (ctrl)
            modifier += 4;
        return modifier;
    }

    private static bool NeedsCsiU(KeyChord chord)
    {
        if (chord.Key.Length == 1 && char.IsAsciiDigit(chord.Key[0]) && (chord.Ctrl || chord.Shift))
            return true;
        return chord.Ctrl
            && chord.Shift
            && chord.Key.Length == 1
            && char.IsAsciiLetter(chord.Key[0]);
    }

    private static byte[] EncodeCsiU(KeyChord chord)
    {
        var code = UnicodeCode(chord.Key);
        if (code == 0)
            return [];
        var modifier = CsiModifier(chord.Shift, chord.Alt, chord.Ctrl);
        return Encoding.ASCII.GetBytes($"\u001b[{code};{modifier}u");
    }

    private static int UnicodeCode(string key) => key switch
    {
        "minus" => 45,
        "plus" => 43,
        "comma" => 44,
        "period" => 46,
        "slash" => 47,
        "?" => 63,
        "[" => 91,
        "]" => 93,
        "\\" => 92,
        "^" => 94,
        "_" => 95,
        "space" => 32,
        "esc" => 27,
        "enter" => 13,
        "tab" => 9,
        "backspace" => 127,
        _ when key.Length == 1 => key[0],
        _ => 0,
    };

    private static string UnicodeKey(int code)
    {
        if (code is >= 65 and <= 90)
            return ((char)(code + 32)).ToString();
        if (code is >= 97 and <= 122)
            return ((char)code).ToString();
        if (code is >= 32 and <= 126)
            return KeyChord.NormalizeKey(((char)code).ToString());
        return "csi";
    }

    private static DecodedInput DecodeByte(byte b, bool alt)
    {
        var raw = alt ? new byte[] { 0x1B, b } : [b];
        switch (b)
        {
            case 0x00:
                return new DecodedInput(new KeyChord(false, true, alt, false, "space"), raw);
            case 0x09:
                return new DecodedInput(new KeyChord(false, false, alt, false, "tab"), raw);
            case 0x0D:
                return new DecodedInput(new KeyChord(false, false, alt, false, "enter"), raw);
            case 0x1B:
                return alt
                    ? new DecodedInput(new KeyChord(false, true, true, false, "["), raw)
                    : new DecodedInput(new KeyChord(false, false, false, false, "esc"), raw);
            case 0x08:
                return alt
                    ? new DecodedInput(new KeyChord(false, true, true, false, "h"), raw)
                    : new DecodedInput(new KeyChord(false, false, false, false, "backspace"), raw);
            case 0x7F:
                return new DecodedInput(new KeyChord(false, false, alt, false, "backspace"), raw);
            case 0x20:
                return new DecodedInput(new KeyChord(false, false, alt, false, "space"), raw);
            case (byte)'-':
                return new DecodedInput(new KeyChord(false, false, alt, false, "minus"), raw);
            case (byte)',':
                return new DecodedInput(new KeyChord(false, false, alt, false, "comma"), raw);
            case (byte)'.':
                return new DecodedInput(new KeyChord(false, false, alt, false, "period"), raw);
            case (byte)'/':
                return new DecodedInput(new KeyChord(false, false, alt, false, "slash"), raw);
        }

        if (b is >= 0x01 and <= 0x1A)
        {
            var letter = (char)('a' + b - 1);
            return new DecodedInput(new KeyChord(false, true, alt, false, letter.ToString()), raw);
        }

        if (b is >= 0x1C and <= 0x1F)
        {
            var key = b switch
            {
                0x1C => "\\",
                0x1D => "]",
                0x1E => "^",
                _ => "_",
            };
            return new DecodedInput(new KeyChord(false, true, alt, false, key), raw);
        }

        if (b is >= (byte)'A' and <= (byte)'Z')
        {
            var letter = char.ToLowerInvariant((char)b).ToString();
            return new DecodedInput(new KeyChord(false, false, alt, true, letter), raw);
        }

        if (b is >= 0x20 and <= 0x7E)
        {
            return new DecodedInput(
                new KeyChord(false, false, alt, false, KeyChord.NormalizeKey(((char)b).ToString())),
                raw);
        }

        return new DecodedInput(new KeyChord(false, false, alt, false, ((char)b).ToString()), raw);
    }

    private static KeyChord DecodeCsi(ReadOnlySpan<byte> csi)
    {
        // csi starts with '['
        if (csi.Length >= 2 && csi[^1] == (byte)'Z')
            return new KeyChord(false, false, false, true, "tab");

        var final = csi.Length == 0 ? (byte)0 : csi[^1];
        ParseCsiParams(csi, out var code, out var extra, out var shift, out var alt, out var ctrl);
        if (final == (byte)'u')
            return new KeyChord(false, ctrl, alt, shift, UnicodeKey(code));
        if (final == (byte)'~' && code == 27)
            return new KeyChord(false, ctrl, alt, shift, UnicodeKey(extra));

        var key = final switch
        {
            (byte)'A' => "up",
            (byte)'B' => "down",
            (byte)'C' => "right",
            (byte)'D' => "left",
            (byte)'H' => "home",
            (byte)'F' => "end",
            (byte)'~' => TildeKey(code),
            _ => "csi",
        };
        return new KeyChord(false, ctrl, alt, shift, key);
    }

    private static string TildeKey(int code) => code switch
    {
        1 or 7 => "home",
        2 => "insert",
        3 => "delete",
        4 or 8 => "end",
        5 => "pageup",
        6 => "pagedown",
        _ => "csi",
    };

    private static void ParseCsiParams(
        ReadOnlySpan<byte> csi,
        out int code,
        out int extra,
        out bool shift,
        out bool alt,
        out bool ctrl)
    {
        code = 0;
        extra = 0;
        shift = false;
        alt = false;
        ctrl = false;
        var first = 0;
        var second = 0;
        var third = 0;
        var field = 0;
        for (var i = 1; i < csi.Length - 1; i++)
        {
            var b = csi[i];
            if (b == (byte)';')
            {
                field++;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'9')
                continue;
            if (field == 0)
                first = (first * 10) + (b - '0');
            else if (field == 1)
                second = (second * 10) + (b - '0');
            else if (field == 2)
                third = (third * 10) + (b - '0');
        }

        code = first;
        extra = third;
        var mod = field >= 1 ? second : 0;
        if (mod >= 2)
        {
            var bits = mod - 1;
            shift = (bits & 1) != 0;
            alt = (bits & 2) != 0;
            ctrl = (bits & 4) != 0;
        }
    }

    private static bool IsCsiFinal(byte b) => b is >= 0x40 and <= 0x7E;
}
