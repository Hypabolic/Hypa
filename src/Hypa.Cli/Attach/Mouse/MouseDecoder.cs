using System.Globalization;
using System.Text;

namespace Hypa.Cli.Attach.Mouse;

/// <summary>
/// Mouse protocol encoding. Default is X10. Unobserved attach state uses SGR.
/// </summary>
public enum MouseProtocolEncoding
{
    Default,
    Utf8,
    Sgr,
    Urxvt,
    SgrPixels,
}

/// <summary>SGR 1006 and X10 mouse CSI. Coordinates become 0-based.</summary>
public static class MouseDecoder
{
    public static bool TryParse(ReadOnlySpan<byte> seq, out MouseEvent ev)
    {
        ev = new MouseEvent(MouseButton.None, MouseAction.Press, 0, 0);
        if (seq.Length < 6 || seq[0] != 0x1b || seq[1] != (byte)'[')
            return false;

        if (seq.Length == 6 && seq[2] == (byte)'M')
            return TryParseX10(seq, out ev);

        if (seq[2] != (byte)'<')
            return false;

        var final = seq[^1];
        if (final is not ((byte)'M' or (byte)'m'))
            return false;

        var body = seq[3..^1];
        if (!TryReadSgrFields(body, out var btn, out var x, out var y))
            return false;

        DecodeButton(btn, final == (byte)'m', out var button, out var action, out var shift, out var alt, out var ctrl);
        ev = new MouseEvent(button, action, Math.Max(0, x - 1), Math.Max(0, y - 1), shift, alt, ctrl);
        return true;
    }

    public static byte[] EncodeSgr(MouseEvent ev, int col1Based, int row1Based)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var btn = EncodeButton(ev);
        var final = ev.Action is MouseAction.Release ? 'm' : 'M';
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[<{btn};{Math.Max(1, col1Based)};{Math.Max(1, row1Based)}{final}");
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>
    // / Encode a pane mouse report.
    /// </summary>
    public static byte[]? Encode(
        MouseEvent ev,
        int col1Based,
        int row1Based,
        MouseProtocolEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(ev);
        col1Based = Math.Max(1, col1Based);
        row1Based = Math.Max(1, row1Based);
        return encoding switch
        {
            MouseProtocolEncoding.Sgr or MouseProtocolEncoding.SgrPixels =>
                EncodeSgr(ev, col1Based, row1Based),
            MouseProtocolEncoding.Default => EncodeX10(ev, col1Based, row1Based),
            MouseProtocolEncoding.Utf8 => EncodeUtf8(ev, col1Based, row1Based),
            MouseProtocolEncoding.Urxvt => EncodeUrxvt(ev, col1Based, row1Based),
            _ => EncodeSgr(ev, col1Based, row1Based),
        };
    }

    private static byte[]? EncodeX10(MouseEvent ev, int col1Based, int row1Based)
    {
        var cb = EncodeLegacyCb(ev);
        if (cb + 32 > 255 || col1Based + 32 > 255 || row1Based + 32 > 255)
            return null;
        return
        [
            0x1b,
            (byte)'[',
            (byte)'M',
            (byte)(cb + 32),
            (byte)(col1Based + 32),
            (byte)(row1Based + 32),
        ];
    }

    private static byte[]? EncodeUtf8(MouseEvent ev, int col1Based, int row1Based)
    {
        var bytes = new List<byte>(16) { 0x1b, (byte)'[', (byte)'M' };
        if (!TryPushMouseCodepoint(bytes, EncodeLegacyCb(ev) + 32)
            || !TryPushMouseCodepoint(bytes, col1Based + 32)
            || !TryPushMouseCodepoint(bytes, row1Based + 32))
        {
            return null;
        }

        return [.. bytes];
    }

    private static byte[] EncodeUrxvt(MouseEvent ev, int col1Based, int row1Based)
    {
        var cb = EncodeLegacyCb(ev) + 32;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[{cb};{col1Based};{row1Based}M");
        return Encoding.ASCII.GetBytes(text);
    }

    private static bool TryPushMouseCodepoint(List<byte> bytes, int value)
    {
        if (!Rune.TryCreate(value, out var rune))
            return false;
        Span<byte> buf = stackalloc byte[4];
        var written = rune.EncodeToUtf8(buf);
        for (var i = 0; i < written; i++)
            bytes.Add(buf[i]);
        return true;
    }

    private static bool TryParseX10(ReadOnlySpan<byte> seq, out MouseEvent ev)
    {
        var rawBtn = seq[3] - 32;
        var x = seq[4] - 32;
        var y = seq[5] - 32;
        DecodeButton(rawBtn, release: rawBtn == 3, out var button, out var action, out var shift, out var alt, out var ctrl);
        ev = new MouseEvent(button, action, Math.Max(0, x - 1), Math.Max(0, y - 1), shift, alt, ctrl);
        return true;
    }

    private static bool TryReadSgrFields(ReadOnlySpan<byte> body, out int btn, out int x, out int y)
    {
        btn = 0;
        x = 0;
        y = 0;
        var field = 0;
        var value = 0;
        var any = false;
        foreach (var b in body)
        {
            if (b == (byte)';')
            {
                if (field == 0)
                    btn = value;
                else if (field == 1)
                    x = value;
                else if (field == 2)
                    y = value;
                field++;
                value = 0;
                any = false;
                continue;
            }

            if (b is < (byte)'0' or > (byte)'9')
                return false;
            value = (value * 10) + (b - '0');
            any = true;
        }

        if (any)
        {
            if (field == 0)
                btn = value;
            else if (field == 1)
                x = value;
            else if (field == 2)
                y = value;
        }

        return field >= 2;
    }

    private static void DecodeButton(
        int btn,
        bool release,
        out MouseButton button,
        out MouseAction action,
        out bool shift,
        out bool alt,
        out bool ctrl)
    {
        shift = (btn & 4) != 0;
        alt = (btn & 8) != 0;
        ctrl = (btn & 16) != 0;
        var motion = (btn & 32) != 0;
        var wheel = (btn & 64) != 0;
        var low = btn & 3;

        if (wheel)
        {
            button = low == 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
            action = MouseAction.Wheel;
            return;
        }

        button = low switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.None,
        };

        if (release)
        {
            if (low == 3)
                button = MouseButton.None;
            action = MouseAction.Release;
            return;
        }

        if (motion && low == 3)
        {
            button = MouseButton.None;
            action = MouseAction.Move;
            return;
        }

        action = motion ? MouseAction.Drag : MouseAction.Press;
    }

    private static int EncodeButton(MouseEvent ev) =>
        EncodeBaseButton(ev) + EncodeModifiers(ev);

    private static int EncodeLegacyCb(MouseEvent ev) =>
        (ev.Action is MouseAction.Release ? 3 : EncodeBaseButton(ev)) + EncodeModifiers(ev);

    private static int EncodeBaseButton(MouseEvent ev)
    {
        if (ev.Action is MouseAction.Move)
            return 35;

        var code = ev.Button switch
        {
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            MouseButton.WheelUp => 64,
            MouseButton.WheelDown => 65,
            _ => 0,
        };
        if (ev.Action is MouseAction.Drag && ev.Button is not (MouseButton.WheelUp or MouseButton.WheelDown))
            code += 32;
        return code;
    }

    private static int EncodeModifiers(MouseEvent ev)
    {
        var code = 0;
        if (ev.Shift)
            code += 4;
        if (ev.Alt)
            code += 8;
        if (ev.Ctrl)
            code += 16;
        return code;
    }
}
