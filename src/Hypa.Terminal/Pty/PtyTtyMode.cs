namespace Hypa.Terminal.Pty;

/// <summary>The termios ECHO and ICANON state of a pane PTY.</summary>
public readonly record struct PtyTtyMode(bool Echo, bool Canonical)
{
    public const byte EchoFlag = 0x01;
    public const byte CanonicalFlag = 0x02;

    public static PtyTtyMode FromFlags(byte flags) =>
        new((flags & EchoFlag) != 0, (flags & CanonicalFlag) != 0);

    public byte ToFlags() =>
        (byte)((Echo ? EchoFlag : 0) | (Canonical ? CanonicalFlag : 0));
}

/// <summary>
/// Puts each TtyMode frame in the output byte order. The frame pump stamps a
/// change with the count of output bytes that it put in the output channel
/// before the change. The output reader counts the bytes that it has read.
/// The current mode is the newest change whose stamp is at or below that
/// count. One lock guards the state: the pump and the reader run on two
/// threads.
/// </summary>
internal sealed class PtyTtyModeTracker
{
    private readonly object _gate = new();
    private readonly Queue<(long At, PtyTtyMode Mode)> _pending = new();
    private long _enqueued;
    private long _read;
    private PtyTtyMode? _current;

    /// <summary>Raised when a change applies at once: its bytes are already read.</summary>
    public event Action? Changed;

    public PtyTtyMode? Current
    {
        get
        {
            lock (_gate)
            {
                AdvanceLocked();
                return _current;
            }
        }
    }

    /// <summary>The pump put <paramref name="count"/> output bytes in the channel.</summary>
    public void NoteEnqueued(int count)
    {
        if (count <= 0)
            return;
        lock (_gate)
            _enqueued += count;
    }

    /// <summary>The reader took <paramref name="count"/> output bytes from the channel.</summary>
    public void NoteRead(int count)
    {
        if (count <= 0)
            return;
        lock (_gate)
            _read += count;
    }

    /// <summary>A TtyMode frame, after the output bytes that the pump enqueued so far.</summary>
    public void Record(PtyTtyMode mode)
    {
        bool now;
        lock (_gate)
        {
            _pending.Enqueue((_enqueued, mode));
            AdvanceLocked();
            now = _pending.Count == 0;
        }

        if (now)
            Changed?.Invoke();
    }

    private void AdvanceLocked()
    {
        while (_pending.Count > 0 && _pending.Peek().At <= _read)
            _current = _pending.Dequeue().Mode;
    }
}
