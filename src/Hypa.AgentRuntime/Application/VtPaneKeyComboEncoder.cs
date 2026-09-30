using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// Rejects <c>prefix+</c>.
/// </summary>
public sealed class VtPaneKeyComboEncoder : IPaneKeyComboEncoder
{
    public bool TryEncode(IReadOnlyList<string> keys, out byte[] bytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var buffer = new List<byte>(keys.Count);
        foreach (var raw in keys)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                bytes = [];
                error = "key is required";
                return false;
            }

            var key = NormalizeAlias(raw.Trim());
            if (ContainsPrefixToken(key))
            {
                bytes = [];
                error = "prefix+ is not accepted";
                return false;
            }

            if (!TryEncodeOne(key, out var encoded, out error))
            {
                bytes = [];
                return false;
            }

            buffer.AddRange(encoded);
        }

        bytes = buffer.ToArray();
        error = null;
        return true;
    }

    internal static string NormalizeAlias(string key) =>
        key switch
        {
            "C-c" or "c-c" => "ctrl+c",
            "+" => "plus",
            _ => key,
        };

    internal static bool ContainsPrefixToken(string key)
    {
        foreach (var part in key.Split('+'))
        {
            if (part.Trim().Equals("prefix", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool TryEncodeOne(string key, out byte[] bytes, out string? error)
    {
        bytes = [];
        error = null;
        var parts = key.Split('+');
        var ctrl = false;
        var alt = false;
        var shift = false;
        string? keyToken = null;

        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                error = "invalid key";
                return false;
            }

            if (IsModifier(token, out var kind))
            {
                switch (kind)
                {
                    case ModifierKind.Ctrl:
                        ctrl = true;
                        break;
                    case ModifierKind.Alt:
                        alt = true;
                        break;
                    case ModifierKind.Shift:
                        shift = true;
                        break;
                }

                continue;
            }

            if (keyToken is not null)
            {
                error = $"unsupported key {key}";
                return false;
            }

            keyToken = token;
        }

        if (keyToken is null)
        {
            error = $"unsupported key {key}";
            return false;
        }

        if (!TryEncodeKey(keyToken, ctrl, alt, shift, out bytes))
        {
            error = $"unsupported key {key}";
            return false;
        }

        return true;
    }

    private static bool IsModifier(string token, out ModifierKind kind)
    {
        kind = default;
        switch (token.ToLowerInvariant())
        {
            case "ctrl" or "control":
                kind = ModifierKind.Ctrl;
                return true;
            case "alt" or "option" or "meta":
                kind = ModifierKind.Alt;
                return true;
            case "shift":
                kind = ModifierKind.Shift;
                return true;
            default:
                return false;
        }
    }

    private static bool TryEncodeKey(string token, bool ctrl, bool alt, bool shift, out byte[] bytes)
    {
        bytes = [];
        var lower = token.ToLowerInvariant();
        if (lower is "tab" && shift && !ctrl)
        {
            bytes = PrefixAlt(alt, [0x1B, (byte)'[', (byte)'Z']);
            return true;
        }

        if (TryNamedSpecial(lower, out var special, out var csiLetter))
        {
            if (csiLetter is char letter)
            {
                bytes = EncodeCsiSpecial(letter, shift, alt, ctrl);
                return true;
            }

            if (lower is "space" && ctrl)
                return TryEncodeChar(' ', ctrl: true, alt, shift, out bytes);

            if (ctrl || shift)
                return false;

            bytes = PrefixAlt(alt, special);
            return true;
        }

        if (TryNamedPunctuation(lower, out var punct))
            return TryEncodeChar(punct, ctrl, alt, shift, out bytes);

        if (lower.Length >= 2 && lower[0] == 'f' && byte.TryParse(lower[1..], out var fn) && fn is >= 1 and <= 12)
        {
            bytes = EncodeFunctionKey(fn, shift, alt, ctrl);
            return true;
        }

        var rune = token;
        if (rune.Length != 1)
            return false;

        var ch = rune[0];
        if (char.IsControl(ch))
            return false;

        return TryEncodeChar(ch, ctrl, alt, shift, out bytes);
    }

    private static bool TryNamedSpecial(string lower, out byte[] bytes, out char? csiLetter)
    {
        csiLetter = lower switch
        {
            "left" => 'D',
            "right" => 'C',
            "up" => 'A',
            "down" => 'B',
            _ => null,
        };
        bytes = lower switch
        {
            "enter" or "return" => [0x0D],
            "esc" or "escape" => [0x1B],
            "tab" => [0x09],
            "backspace" or "bs" => [0x7F],
            "space" => [(byte)' '],
            "left" => [0x1B, (byte)'[', (byte)'D'],
            "right" => [0x1B, (byte)'[', (byte)'C'],
            "up" => [0x1B, (byte)'[', (byte)'A'],
            "down" => [0x1B, (byte)'[', (byte)'B'],
            _ => [],
        };
        return bytes.Length > 0;
    }

    /// <summary>
    // alt is the xterm bit, not an ESC prefix.
    /// </summary>
    private static byte[] EncodeCsiSpecial(char letter, bool shift, bool alt, bool ctrl)
    {
        var modifier = XtermModifier(shift, alt, ctrl);
        if (modifier > 1)
            return Encoding.ASCII.GetBytes($"\u001b[1;{modifier}{letter}");
        return [0x1B, (byte)'[', (byte)letter];
    }

    private static bool TryNamedPunctuation(string lower, out char ch)
    {
        ch = lower switch
        {
            "minus" => '-',
            "plus" => '+',
            "comma" => ',',
            "period" => '.',
            "slash" => '/',
            "backslash" => '\\',
            "quote" => '\'',
            "double_quote" or "double-quote" => '"',
            "semicolon" => ';',
            "colon" => ':',
            "percent" => '%',
            "ampersand" => '&',
            "backtick" => '`',
            _ => '\0',
        };
        return ch != '\0';
    }

    private static byte[] EncodeFunctionKey(byte n, bool shift, bool alt, bool ctrl)
    {
        var modifier = XtermModifier(shift, alt, ctrl);
        if (modifier > 1)
        {
            return n switch
            {
                1 => Encoding.ASCII.GetBytes($"\u001b[1;{modifier}P"),
                2 => Encoding.ASCII.GetBytes($"\u001b[1;{modifier}Q"),
                3 => Encoding.ASCII.GetBytes($"\u001b[1;{modifier}R"),
                4 => Encoding.ASCII.GetBytes($"\u001b[1;{modifier}S"),
                5 => Encoding.ASCII.GetBytes($"\u001b[15;{modifier}~"),
                6 => Encoding.ASCII.GetBytes($"\u001b[17;{modifier}~"),
                7 => Encoding.ASCII.GetBytes($"\u001b[18;{modifier}~"),
                8 => Encoding.ASCII.GetBytes($"\u001b[19;{modifier}~"),
                9 => Encoding.ASCII.GetBytes($"\u001b[20;{modifier}~"),
                10 => Encoding.ASCII.GetBytes($"\u001b[21;{modifier}~"),
                11 => Encoding.ASCII.GetBytes($"\u001b[23;{modifier}~"),
                12 => Encoding.ASCII.GetBytes($"\u001b[24;{modifier}~"),
                _ => [],
            };
        }

        return n switch
        {
            1 => [0x1B, (byte)'O', (byte)'P'],
            2 => [0x1B, (byte)'O', (byte)'Q'],
            3 => [0x1B, (byte)'O', (byte)'R'],
            4 => [0x1B, (byte)'O', (byte)'S'],
            5 => [0x1B, (byte)'[', (byte)'1', (byte)'5', (byte)'~'],
            6 => [0x1B, (byte)'[', (byte)'1', (byte)'7', (byte)'~'],
            7 => [0x1B, (byte)'[', (byte)'1', (byte)'8', (byte)'~'],
            8 => [0x1B, (byte)'[', (byte)'1', (byte)'9', (byte)'~'],
            9 => [0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'~'],
            10 => [0x1B, (byte)'[', (byte)'2', (byte)'1', (byte)'~'],
            11 => [0x1B, (byte)'[', (byte)'2', (byte)'3', (byte)'~'],
            12 => [0x1B, (byte)'[', (byte)'2', (byte)'4', (byte)'~'],
            _ => [],
        };
    }

    /// <summary>
    // Rejects ctrl on characters
    /// that have no control encoding so we never drop the modifier.
    /// </summary>
    private static bool TryEncodeChar(char ch, bool ctrl, bool alt, bool shift, out byte[] bytes)
    {
        bytes = [];
        if (ctrl)
        {
            if (!TryControlByte(ch, out var ctrlByte))
                return false;

            bytes = PrefixAlt(alt, [ctrlByte]);
            return true;
        }

        var emit = ch;
        if (shift && char.IsAsciiLetter(ch))
            emit = char.ToUpperInvariant(ch);
        bytes = PrefixAlt(alt, Encoding.UTF8.GetBytes(char.ToString(emit)));
        return true;
    }

    private static bool TryControlByte(char ch, out byte ctrlByte)
    {
        var upper = char.ToUpperInvariant(ch);
        switch (upper)
        {
            case >= 'A' and <= 'Z':
                ctrlByte = (byte)(upper - 64);
                return true;
            case ' ' or '@' or '2':
                ctrlByte = 0x00;
                return true;
            case '[' or '3':
                ctrlByte = 0x1B;
                return true;
            case '\\' or '4':
                ctrlByte = 0x1C;
                return true;
            case ']' or '5':
                ctrlByte = 0x1D;
                return true;
            case '^' or '6':
                ctrlByte = 0x1E;
                return true;
            case '_' or '/' or '7' or '-':
                ctrlByte = 0x1F;
                return true;
            default:
                ctrlByte = 0;
                return false;
        }
    }

    private static byte[] PrefixAlt(bool alt, byte[] inner)
    {
        if (!alt || inner.Length == 0)
            return inner;
        var prefixed = new byte[inner.Length + 1];
        prefixed[0] = 0x1B;
        Buffer.BlockCopy(inner, 0, prefixed, 1, inner.Length);
        return prefixed;
    }

    private static int XtermModifier(bool shift, bool alt, bool ctrl)
    {
        var m = 1;
        if (shift)
            m += 1;
        if (alt)
            m += 2;
        if (ctrl)
            m += 4;
        return m;
    }

    private enum ModifierKind
    {
        Ctrl,
        Alt,
        Shift,
    }
}
