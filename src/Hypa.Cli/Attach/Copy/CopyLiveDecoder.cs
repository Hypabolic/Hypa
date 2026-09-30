using System.Text;

namespace Hypa.Cli.Attach.Copy;

/// <summary>
/// Carries CSI state across live chunks. CR overwrites the open row.
/// OSC is dropped at a chunk boundary so an unterminated sequence cannot stall.
/// </summary>
internal sealed class CopyLiveDecoder
{
    internal const int MaxControlBytes = 4096;

    private enum State
    {
        Text,
        Esc,
        Csi,
        Osc,
    }

    private readonly List<byte> _pendingUtf8 = [];
    private readonly StringBuilder _control = new();
    private State _state;
    private bool _pendingCr;

    public void Reset()
    {
        _pendingUtf8.Clear();
        _control.Clear();
        _state = State.Text;
        _pendingCr = false;
    }

    public List<string> Decode(ReadOnlySpan<byte> bytes, string? openLine, int cols, out bool incomplete)
    {
        cols = Math.Max(1, cols);
        var text = DecodeUtf8(bytes);
        var rows = new List<string>();
        var hadPendingCr = _pendingCr;
        var current = new StringBuilder(hadPendingCr ? "" : (openLine ?? ""));
        var wrapped = false;

        void FlushRow(bool fromWrap)
        {
            rows.Add(current.ToString());
            current.Clear();
            wrapped = fromWrap;
        }

        foreach (var ch in text)
        {
            if (ch == '\u001b' && _state is not State.Text)
            {
                DropControl();
                _state = State.Esc;
                _control.Append(ch);
                continue;
            }

            if (_state is State.Esc)
            {
                _control.Append(ch);
                _state = ch switch
                {
                    '[' => State.Csi,
                    ']' => State.Osc,
                    _ => State.Text,
                };
                if (_state == State.Text)
                    _control.Clear();
                continue;
            }

            if (_state is State.Csi)
            {
                _control.Append(ch);
                if (ch is >= (char)0x40 and <= (char)0x7E || _control.Length >= MaxControlBytes)
                    DropControl();
                continue;
            }

            if (_state is State.Osc)
            {
                _control.Append(ch);
                if (ch == '\u0007'
                    || (ch == '\\' && _control.Length >= 2 && _control[^2] == '\u001b')
                    || _control.Length >= MaxControlBytes)
                {
                    DropControl();
                }

                continue;
            }

            if (ch == '\u001b')
            {
                _state = State.Esc;
                _control.Clear();
                _control.Append(ch);
                continue;
            }

            if (ch == '\r')
            {
                current.Clear();
                _pendingCr = true;
                continue;
            }

            if (ch == '\n')
            {
                _pendingCr = false;
                if (current.Length > 0 || !wrapped)
                    FlushRow(fromWrap: false);
                wrapped = false;
                continue;
            }

            if (ch != '\t' && ch < ' ')
                continue;

            wrapped = false;
            _pendingCr = false;
            current.Append(ch);
            if (current.Length >= cols)
                FlushRow(fromWrap: true);
        }

        var leftover = current.ToString();
        if (leftover.Length > 0)
            rows.Add(leftover);
        else if (rows.Count == 0 && (openLine is not null || hadPendingCr || _pendingCr))
            rows.Add("");

        incomplete = leftover.Length > 0 || (rows.Count > 0 && text.Length > 0 && !text.EndsWith('\n'));
        if (_state is State.Osc)
            DropControl();
        if (_state != State.Text && leftover.Length == 0 && rows.Count == 0)
            incomplete = false;

        return rows;
    }

    private void DropControl()
    {
        _state = State.Text;
        _control.Clear();
    }

    private string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty && _pendingUtf8.Count == 0)
            return "";

        var buf = new byte[_pendingUtf8.Count + bytes.Length];
        _pendingUtf8.CopyTo(buf);
        bytes.CopyTo(buf.AsSpan(_pendingUtf8.Count));
        var usable = CompleteUtf8Length(buf);
        _pendingUtf8.Clear();
        if (usable < buf.Length)
            _pendingUtf8.AddRange(buf.AsSpan(usable).ToArray());
        return usable == 0 ? "" : Encoding.UTF8.GetString(buf, 0, usable);
    }

    private static int CompleteUtf8Length(ReadOnlySpan<byte> buf)
    {
        if (buf.Length == 0)
            return 0;

        var trail = 0;
        while (trail < 4 && buf.Length - 1 - trail >= 0 && (buf[buf.Length - 1 - trail] & 0xC0) == 0x80)
            trail++;
        if (buf.Length - 1 - trail < 0)
            return 0;

        var lead = buf[buf.Length - 1 - trail];
        var need = lead switch
        {
            < 0x80 => 1,
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1,
        };
        var have = trail + 1;
        return have >= need ? buf.Length : buf.Length - have;
    }
}
