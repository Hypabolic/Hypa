namespace Hypa.AgentRuntime.Application;

/// <summary>What the caller of <see cref="ProgramStatusEmitCoalescer.Request"/> does next.</summary>
public enum ProgramStatusEmitDecision
{
    /// <summary>Emit now. The caller must call <see cref="ProgramStatusEmitCoalescer.Complete"/> when the emit ends.</summary>
    EmitNow,

    /// <summary>Do not emit now. A later emit will carry this change.</summary>
    Deferred,
}

/// <summary>
/// The timing rule for <c>pane.program_status_changed</c>. This type does not emit and does not
/// hold a timer. The caller emits and wakes this type at <see cref="NextDueAt"/>.
/// <para>
/// Each pane has a minimum interval between two emits. The first change after a quiet period
/// goes out at once (leading edge). A change inside the interval waits and goes out once at the
/// end of the interval (trailing edge). A pane has at most one emit in flight and at most one
/// pending emit. The pending emit is a flag, not a copy of a state: the caller reads the latest
/// state when the emit runs.
/// </para>
/// The clock is injected. One instance serves one session. Calls are thread safe.
/// </summary>
public sealed class ProgramStatusEmitCoalescer
{
    /// <summary>The minimum time between two emits of one pane.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeProvider _time;
    private readonly TimeSpan _minInterval;
    private readonly object _gate = new();
    private readonly Dictionary<string, PaneEmitState> _panes = new(StringComparer.Ordinal);

    public ProgramStatusEmitCoalescer(TimeProvider time, TimeSpan? minInterval = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _minInterval = minInterval ?? DefaultMinInterval;
    }

    /// <summary>The state of a pane changed. Says if the caller emits now.</summary>
    public ProgramStatusEmitDecision Request(string paneId)
    {
        lock (_gate)
        {
            var state = _panes.GetValueOrDefault(paneId) ?? new PaneEmitState();
            _panes[paneId] = state;
            if (state.Pending)
                return ProgramStatusEmitDecision.Deferred;

            var now = _time.GetUtcNow();
            if (!state.InFlight && IsDue(state, now))
            {
                state.InFlight = true;
                state.LastStart = now;
                return ProgramStatusEmitDecision.EmitNow;
            }

            state.Pending = true;
            return ProgramStatusEmitDecision.Deferred;
        }
    }

    /// <summary>An emit that <see cref="Request"/> or <see cref="TakeDue"/> started has ended.</summary>
    public void Complete(string paneId)
    {
        lock (_gate)
        {
            if (_panes.TryGetValue(paneId, out var state))
                state.InFlight = false;
        }
    }

    /// <summary>
    /// Takes the panes that have a pending emit that the interval now allows.
    /// The caller emits each one and calls <see cref="Complete"/> when it ends.
    /// </summary>
    public IReadOnlyList<string> TakeDue()
    {
        lock (_gate)
        {
            List<string>? due = null;
            var now = _time.GetUtcNow();
            foreach (var (paneId, state) in _panes)
            {
                if (!state.Pending || state.InFlight || !IsDue(state, now))
                    continue;

                state.Pending = false;
                state.InFlight = true;
                state.LastStart = now;
                (due ??= []).Add(paneId);
            }

            return due ?? (IReadOnlyList<string>)[];
        }
    }

    /// <summary>
    /// When the caller must call <see cref="TakeDue"/> next. Null when no pane waits.
    /// A pane with an emit in flight does not count: its <see cref="Complete"/> call is the wake-up.
    /// </summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var state in _panes.Values)
                {
                    if (!state.Pending || state.InFlight)
                        continue;

                    var at = (state.LastStart ?? DateTimeOffset.MinValue) + _minInterval;
                    if (next is null || at < next)
                        next = at;
                }

                return next;
            }
        }
    }

    /// <summary>True when a pane has a pending emit or an emit in flight.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _panes.Values.Any(s => s.Pending || s.InFlight);
        }
    }

    /// <summary>The pane is closed. Drops its pending emit and its history.</summary>
    public void Forget(string paneId)
    {
        lock (_gate)
            _panes.Remove(paneId);
    }

    private bool IsDue(PaneEmitState state, DateTimeOffset now) =>
        state.LastStart is not { } last || now - last >= _minInterval;

    private sealed class PaneEmitState
    {
        public DateTimeOffset? LastStart { get; set; }

        public bool InFlight { get; set; }

        public bool Pending { get; set; }
    }
}
