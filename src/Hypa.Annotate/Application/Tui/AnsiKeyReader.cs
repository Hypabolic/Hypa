using System.Text;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Decode one key from stdin bytes.
/// </summary>
internal static class AnsiKeyReader
{
    public static AnnotateKey Decode(int first, Func<int> readByte)
    {
        return first switch
        {
            0x01 => CharKey('a', control: true),
            0x03 => CharKey('c', control: true),
            0x05 => CharKey('e', control: true),
            0x09 => new AnnotateKey(AnnotateKeyCode.Tab, '\0', false, false, false),
            0x0a or 0x0d => new AnnotateKey(AnnotateKeyCode.Enter, '\n', false, false, false),
            0x13 => CharKey('s', control: true),
            0x15 => CharKey('u', control: true),
            0x17 => CharKey('w', control: true),
            0x08 or 0x7f => new AnnotateKey(AnnotateKeyCode.Backspace, '\0', false, false, false),
            0x1b => DecodeEsc(readByte),
            _ => DecodeUtf8(first, readByte),
        };
    }

    private static AnnotateKey DecodeEsc(Func<int> readByte)
    {
        var next = readByte();
        if (next < 0)
            return new AnnotateKey(AnnotateKeyCode.Esc, '\0', false, false, false);
        if (next == '[')
            return DecodeCsi(readByte);
        if (next is 'b' or 'f')
            return CharKey((char)next, alt: true);
        if (next == 0x7f)
            return new AnnotateKey(AnnotateKeyCode.Backspace, '\0', false, true, false);
        return new AnnotateKey(AnnotateKeyCode.Esc, '\0', false, false, false);
    }

    private static AnnotateKey DecodeCsi(Func<int> readByte)
    {
        var body = new StringBuilder();
        int current;
        while ((current = readByte()) >= 0)
        {
            if (current is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '~')
            {
                body.Append((char)current);
                break;
            }

            body.Append((char)current);
        }

        var token = body.ToString();
        var (alt, control, super) = ParseModifiers(token);
        var code = token switch
        {
            _ when token.EndsWith('A') => AnnotateKeyCode.Up,
            _ when token.EndsWith('B') => AnnotateKeyCode.Down,
            _ when token.EndsWith('C') => AnnotateKeyCode.Right,
            _ when token.EndsWith('D') => AnnotateKeyCode.Left,
            _ when token.EndsWith('H') => AnnotateKeyCode.Home,
            _ when token.EndsWith('F') => AnnotateKeyCode.End,
            "3~" => AnnotateKeyCode.Delete,
            _ => AnnotateKeyCode.Other,
        };
        return new AnnotateKey(code, '\0', control, alt, super);
    }

    private static (bool Alt, bool Control, bool Super) ParseModifiers(string token)
    {
        var sep = token.IndexOf(';');
        if (sep < 0)
            return (false, false, false);
        var digits = new StringBuilder();
        for (var i = sep + 1; i < token.Length; i++)
        {
            if (!char.IsDigit(token[i]))
                break;
            digits.Append(token[i]);
        }

        if (!int.TryParse(digits.ToString(), out var modifier))
            return (false, false, false);
        // xterm: 1=none, 2=shift, 3=alt, 5=ctrl, 9=super
        return (modifier is 3 or 4 or 7 or 8, modifier is 5 or 6 or 7 or 8, modifier is 9 or 13 or 15);
    }

    private static AnnotateKey DecodeUtf8(int first, Func<int> readByte)
    {
        if (first < 0x20 || first == 0x7f)
            return new AnnotateKey(AnnotateKeyCode.Other, '\0', false, false, false);
        if (first < 0x80)
            return CharKey((char)first);

        var bytes = new List<byte> { (byte)first };
        var extra = first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : 1;
        for (var i = 0; i < extra; i++)
        {
            var next = readByte();
            if (next < 0)
                break;
            bytes.Add((byte)next);
        }

        var text = Encoding.UTF8.GetString(bytes.ToArray());
        return text.Length == 0
            ? new AnnotateKey(AnnotateKeyCode.Other, '\0', false, false, false)
            : CharKey(text[0]);
    }

    private static AnnotateKey CharKey(char character, bool control = false, bool alt = false) =>
        new(AnnotateKeyCode.Char, character, control, alt, false);
}
