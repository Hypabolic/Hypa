using System.Text;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Retains OSC 0/2 title and OSC 9 progress for agent detection.
/// Passive capture only. Does not affect render.
/// </summary>
public sealed class AgentOscStateTracker
{
    public const int MaxBodyBytes = 4096;
    public const int MaxChars = 256;

    private enum StreamState
    {
        Ground,
        Escape,
        Body,
        BodyEscape,
        IgnoringString,
        IgnoringStringEscape,
        Discarding,
        DiscardingEscape,
    }

    private StreamState _state;
    private readonly List<byte> _body = [];
    private string? _latestTitle;
    private string? _terminalTitle;
    private string? _latestProgress;

    public string LatestTitle => _latestTitle ?? "";

    public string LatestProgress => _latestProgress ?? "";

    public string? TerminalTitle => _terminalTitle;

    public bool Observe(ReadOnlySpan<byte> bytes)
    {
        var titleChanged = false;
        for (var i = 0; i < bytes.Length; i++)
            titleChanged |= ObserveByte(bytes[i]);
        return titleChanged;
    }

    public void ClearRetained()
    {
        _latestTitle = null;
        _latestProgress = null;
    }

    private bool ObserveByte(byte b)
    {
        switch (_state)
        {
            case StreamState.Ground:
                if (b == 0x1b)
                    _state = StreamState.Escape;
                return false;
            case StreamState.Escape:
                if (b == (byte)']')
                {
                    _body.Clear();
                    _state = StreamState.Body;
                }
                else if (b == 0x1b)
                {
                    _state = StreamState.Escape;
                }
                else if (b is (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
                {
                    _state = StreamState.IgnoringString;
                }
                else
                {
                    _state = StreamState.Ground;
                }

                return false;
            case StreamState.Body:
                if (b == 0x07)
                    return Finish();
                if (b == 0x1b)
                {
                    _state = StreamState.BodyEscape;
                    return false;
                }

                Push(b);
                return false;
            case StreamState.BodyEscape:
                if (b == (byte)'\\')
                    return Finish();
                if (b == 0x07)
                {
                    Push(0x1b);
                    if (_state is StreamState.Body)
                        return Finish();
                    _state = StreamState.Ground;
                    return false;
                }

                if (b == 0x1b)
                {
                    Push(0x1b);
                    if (_state is StreamState.Body)
                        _state = StreamState.BodyEscape;
                    else if (_state is StreamState.Discarding)
                        _state = StreamState.DiscardingEscape;
                    return false;
                }

                Push(0x1b);
                if (_state is StreamState.Body)
                    Push(b);
                return false;
            case StreamState.IgnoringString:
                if (b == 0x1b)
                    _state = StreamState.IgnoringStringEscape;
                return false;
            case StreamState.IgnoringStringEscape:
                if (b == (byte)'\\')
                    _state = StreamState.Ground;
                else if (b != 0x1b)
                    _state = StreamState.IgnoringString;
                return false;
            case StreamState.Discarding:
                if (b == 0x07)
                    _state = StreamState.Ground;
                else if (b == 0x1b)
                    _state = StreamState.DiscardingEscape;
                return false;
            case StreamState.DiscardingEscape:
                if (b == (byte)'\\')
                    _state = StreamState.Ground;
                else if (b != 0x1b)
                    _state = StreamState.Discarding;
                return false;
            default:
                return false;
        }
    }

    private void Push(byte b)
    {
        _body.Add(b);
        if (_body.Count > MaxBodyBytes)
        {
            _body.Clear();
            _state = StreamState.Discarding;
        }
        else
        {
            _state = StreamState.Body;
        }
    }

    private bool Finish()
    {
        var changed = ApplyBody(_body);
        _body.Clear();
        _state = StreamState.Ground;
        return changed;
    }

    private bool ApplyBody(List<byte> body)
    {
        var sep = body.IndexOf((byte)';');
        if (sep < 0)
            return false;
        var command = body.GetRange(0, sep);
        var payload = body.GetRange(sep + 1, body.Count - sep - 1);
        if (command.Count == 1 && (command[0] == (byte)'0' || command[0] == (byte)'2'))
        {
            var title = Sanitize(payload);
            var stored = title.Length == 0 ? null : title;
            var changed = !string.Equals(_terminalTitle, stored, StringComparison.Ordinal);
            _terminalTitle = stored;
            _latestTitle = stored;
            return changed;
        }

        if (command.Count == 1 && command[0] == (byte)'9')
        {
            _latestProgress = Sanitize(payload);
            return false;
        }

        return false;
    }

    private static string Sanitize(List<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload.ToArray());
        var sb = new StringBuilder();
        var n = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                continue;
            sb.Append(rune);
            n++;
            if (n >= MaxChars)
                break;
        }

        return sb.ToString();
    }
}
