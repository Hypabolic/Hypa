using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Terminal.Vt;

/// <summary>
// / Child OSC 10/11/110/111 tracker.
/// <c>src/pane/osc.rs</c> <c>DefaultColorEventTracker</c> for those four
/// commands only. Skip OSC 4 and OSC 12.
/// </summary>
public sealed class DefaultColorOscTracker
{
    private const int MaxBody = 1024;

    private enum State
    {
        Ground,
        Escape,
        OscBody,
        OscEscape,
        IgnoreString,
        IgnoreStringEscape,
        OversizedOsc,
        OversizedOscEscape,
    }

    private State _state;
    private readonly List<byte> _body = [];
    private readonly List<DefaultColorOscEvent> _pending = [];

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            switch (_state)
            {
                case State.Ground:
                    if (b == 0x1b)
                        _state = State.Escape;
                    break;
                case State.Escape:
                    if (b == (byte)']')
                    {
                        _body.Clear();
                        _state = State.OscBody;
                    }
                    else if (b is (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
                    {
                        _body.Clear();
                        _state = State.IgnoreString;
                    }
                    else if (b == 0x1b)
                    {
                        _state = State.Escape;
                    }
                    else
                    {
                        _state = State.Ground;
                    }

                    break;
                case State.OscBody:
                    if (b == 0x07)
                    {
                        FinalizeBody(i + 1);
                        _state = State.Ground;
                    }
                    else if (b == 0x1b)
                    {
                        _state = State.OscEscape;
                    }
                    else
                    {
                        _body.Add(b);
                    }

                    break;
                case State.OscEscape:
                    if (b == (byte)'\\')
                    {
                        FinalizeBody(i + 1);
                        _state = State.Ground;
                    }
                    else
                    {
                        _body.Add(0x1b);
                        _body.Add(b);
                        _state = State.OscBody;
                    }

                    break;
                case State.IgnoreString:
                    if (b == 0x1b)
                        _state = State.IgnoreStringEscape;
                    break;
                case State.IgnoreStringEscape:
                    if (b == (byte)'\\')
                        _state = State.Ground;
                    else if (b != 0x1b)
                        _state = State.IgnoreString;
                    break;
                case State.OversizedOsc:
                    if (b == 0x1b)
                        _state = State.OversizedOscEscape;
                    else if (b == 0x07)
                        _state = State.Ground;
                    break;
                case State.OversizedOscEscape:
                    if (b == (byte)'\\')
                        _state = State.Ground;
                    else if (b != 0x1b)
                        _state = State.OversizedOsc;
                    break;
            }

            if (_body.Count > MaxBody)
            {
                _body.Clear();
                _state = State.OversizedOsc;
            }
        }
    }

    public List<DefaultColorOscEvent> DrainPending()
    {
        if (_pending.Count == 0)
            return [];
        var drained = new List<DefaultColorOscEvent>(_pending);
        _pending.Clear();
        return drained;
    }

    private void FinalizeBody(int endOffset)
    {
        foreach (var ev in Parse(_body))
            _pending.Add(ev with { EndOffset = endOffset });
        _body.Clear();
    }

    internal static IReadOnlyList<DefaultColorOscEvent> Parse(IReadOnlyList<byte> body)
    {
        if (body.Count == 0)
            return [];

        if (IsExact(body, "10;?"u8))
            return [DefaultColorOscEvent.Query(HostDefaultColorKind.Foreground)];
        if (IsExact(body, "11;?"u8))
            return [DefaultColorOscEvent.Query(HostDefaultColorKind.Background)];
        if (IsExact(body, "110"u8) || IsExact(body, "110;"u8))
            return [DefaultColorOscEvent.Reset(HostDefaultColorKind.Foreground)];
        if (IsExact(body, "111"u8) || IsExact(body, "111;"u8))
            return [DefaultColorOscEvent.Reset(HostDefaultColorKind.Background)];

        // commands 10 and 11 only. Skip OSC 12. Split on ';', drop empty
        // values before offset, skip '?', map start+offset onto fg/bg.
        return ParseSetEvents(body);
    }

    private static IReadOnlyList<DefaultColorOscEvent> ParseSetEvents(IReadOnlyList<byte> body)
    {
        var split = IndexOf(body, (byte)';');
        if (split != 2)
            return [];

        int start;
        if (body[0] == (byte)'1' && body[1] == (byte)'0')
            start = 10;
        else if (body[0] == (byte)'1' && body[1] == (byte)'1')
            start = 11;
        else
            return [];

        var events = new List<DefaultColorOscEvent>();
        var offset = 0;
        var i = split + 1;
        while (i <= body.Count)
        {
            var end = i;
            while (end < body.Count && body[end] != (byte)';')
                end++;
            var len = end - i;
            if (len > 0)
            {
                var isQuery = len == 1 && body[i] == (byte)'?';
                if (!isQuery)
                {
                    var command = start + offset;
                    if (command is 10 or 11)
                    {
                        HostRgb? color = null;
                        if (TryParseRgbSlice(body, i, len, out var parsed))
                            color = parsed;
                        events.Add(DefaultColorOscEvent.Set(
                            command == 10
                                ? HostDefaultColorKind.Foreground
                                : HostDefaultColorKind.Background,
                            color));
                    }
                }

                offset++;
            }

            if (end >= body.Count)
                break;
            i = end + 1;
        }

        return events;
    }

    private static bool TryParseRgbSlice(
        IReadOnlyList<byte> body,
        int start,
        int length,
        out HostRgb color)
    {
        var chars = new char[length];
        for (var n = 0; n < length; n++)
            chars[n] = (char)body[start + n];
        return HostThemeParser.TryParseRgbColor(new string(chars), out color);
    }

    private static bool IsExact(IReadOnlyList<byte> body, ReadOnlySpan<byte> expected)
    {
        if (body.Count != expected.Length)
            return false;
        for (var i = 0; i < expected.Length; i++)
        {
            if (body[i] != expected[i])
                return false;
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<byte> body, byte value)
    {
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i] == value)
                return i;
        }

        return -1;
    }
}

public readonly record struct DefaultColorOscEvent(
    DefaultColorOscKind Kind,
    HostDefaultColorKind Channel,
    HostRgb? Color,
    int EndOffset = 0)
{
    public static DefaultColorOscEvent Query(HostDefaultColorKind channel) =>
        new(DefaultColorOscKind.Query, channel, null);

    public static DefaultColorOscEvent Set(HostDefaultColorKind channel, HostRgb? color) =>
        new(DefaultColorOscKind.Set, channel, color);

    public static DefaultColorOscEvent Reset(HostDefaultColorKind channel) =>
        new(DefaultColorOscKind.Reset, channel, null);
}

public enum DefaultColorOscKind
{
    Query = 0,
    Set = 1,
    Reset = 2,
}
