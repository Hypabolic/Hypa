namespace Hypa.Terminal.Vt;

/// <summary>
/// Provider-neutral scanner for Ground-state BEL bytes. String controls consume
/// BEL as a terminator or payload, so OSC/DCS/APC/PM/SOS BELs are not pane bells.
/// </summary>
public sealed class VtBellCounter
{
    private enum ParseState
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEsc,
        String,
        StringEsc,
    }

    private ParseState _state;
    private int _pending;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            Process(value);
    }

    public int TakePendingCount()
    {
        var count = _pending;
        _pending = 0;
        return Math.Min(count, ushort.MaxValue);
    }

    public void Reset()
    {
        _state = ParseState.Ground;
        _pending = 0;
    }

    private void Process(byte value)
    {
        switch (_state)
        {
            case ParseState.Ground:
                if (value == 0x07)
                {
                    if (_pending < ushort.MaxValue)
                        _pending++;
                    return;
                }

                if (value == 0x1B)
                {
                    _state = ParseState.Escape;
                    return;
                }

                if (value is 0x9D)
                {
                    _state = ParseState.Osc;
                    return;
                }

                if (value is 0x90 or 0x98 or 0x9E or 0x9F)
                    _state = ParseState.String;
                return;

            case ParseState.Escape:
                if (value == (byte)']')
                    _state = ParseState.Osc;
                else if (value == (byte)'[')
                    _state = ParseState.Csi;
                else if (value is (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
                    _state = ParseState.String;
                else if (value == 0x1B)
                    _state = ParseState.Escape;
                else
                    _state = ParseState.Ground;
                return;

            case ParseState.Csi:
                if (value is (>= 0x30 and <= 0x3F) or (>= 0x20 and <= 0x2F))
                    return;
                if (value is >= 0x40 and <= 0x7E)
                {
                    _state = ParseState.Ground;
                    return;
                }

                if (value == 0x1B)
                {
                    _state = ParseState.Escape;
                    return;
                }

                _state = ParseState.Ground;
                return;

            case ParseState.Osc:
                if (value is 0x07 or 0x18 or 0x1A or 0x9C)
                    _state = ParseState.Ground;
                else if (value == 0x1B)
                    _state = ParseState.OscEsc;
                return;

            case ParseState.OscEsc:
                _state = value == (byte)'\\' ? ParseState.Ground : ParseState.Osc;
                return;

            case ParseState.String:
                if (value is 0x18 or 0x1A or 0x9C)
                    _state = ParseState.Ground;
                else if (value == 0x1B)
                    _state = ParseState.StringEsc;
                return;

            case ParseState.StringEsc:
                if (value is (byte)'\\' or 0x18 or 0x1A)
                    _state = ParseState.Ground;
                else if (value != 0x1B)
                    _state = ParseState.String;
                return;
        }
    }
}
