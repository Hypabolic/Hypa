namespace Hypa.Connectivity.Domain;

/// <summary>
/// Monotonic connection generation. Stale completions are rejected.
/// </summary>
public readonly record struct PeerConnectionGeneration(ulong Value)
{
    public static PeerConnectionGeneration None { get; } = new(0);

    public PeerConnectionGeneration Next() => new(Value + 1);
}

public sealed class PeerConnectionGenerationFence
{
    private readonly object _gate = new();
    private PeerConnectionGeneration _current = PeerConnectionGeneration.None;
    private PeerConnectionGeneration _inFlight = PeerConnectionGeneration.None;
    private PeerConnectionGeneration _next = PeerConnectionGeneration.None;

    public PeerConnectionGeneration Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public PeerConnectionGeneration BeginAttempt()
    {
        lock (_gate)
        {
            _next = _next.Next();
            _inFlight = _next;
            return _inFlight;
        }
    }

    public bool TryAccept(PeerConnectionGeneration completed)
    {
        lock (_gate)
        {
            if (completed != _inFlight)
                return false;
            _current = completed;
            return true;
        }
    }
}
