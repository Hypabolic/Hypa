using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// A pane that needs the person, because of a program status record.
/// The mux publishes it. Each frontend decides how to tell the person.
/// <see cref="Msg"/> is safe display text. <see cref="Source"/> names the pane.
/// </summary>
public sealed record ProgramStatusAttentionItem
{
    public required string PaneId { get; init; }

    public required string TabId { get; init; }

    public required string WorkspaceId { get; init; }

    /// <summary>Blocked, done, or error.</summary>
    public required ProgramStatusState State { get; init; }

    public ProgramStatusKind? Kind { get; init; }

    public required ProgramStatusSourceLabel Source { get; init; }

    public string? App { get; init; }

    public string? Msg { get; init; }

    public required AttentionPriority Priority { get; init; }
}

/// <summary>Rate limits of <see cref="ProgramStatusAttentionPolicy"/>.</summary>
public sealed record ProgramStatusAttentionLimits
{
    public static ProgramStatusAttentionLimits Default { get; } = new();

    public TimeSpan PaneWindow { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan SessionWindow { get; init; } = TimeSpan.FromSeconds(60);

    public int SessionMax { get; init; } = 6;
}

/// <summary>
/// The policy for when a pane needs the person. The rule itself is
/// <see cref="ProgramStatusAttentionRule"/> (Domain). This class keeps the memory of each pane
/// and the rate limits. It is policy, not delivery:
/// a frontend picks how to tell the person (toast, OSC 9, system notice, sound, push).
/// <para>
/// A pane needs the person when its program status changes to blocked, done, or error,
/// and program status owns the pane status. A change is a new state, or a new blocked request
/// (a different kind or message).
/// </para>
/// <para>
/// Rate limit: at most one item for each pane in
/// <see cref="ProgramStatusAttentionLimits.PaneWindow"/>, and at most
/// <see cref="ProgramStatusAttentionLimits.SessionMax"/> items for the session in
/// <see cref="ProgramStatusAttentionLimits.SessionWindow"/>.
/// A change inside the limit becomes the pending item of its pane and replaces the older one.
/// There is no queue. The pending item goes out when the limit allows
/// (<see cref="Release"/>, <see cref="NextReleaseAt"/>). It goes away when the pane no longer
/// needs the person, or when the pane is forgotten.
/// </para>
/// The clock is injected. One instance serves one session. Calls are thread safe.
/// </summary>
public sealed class ProgramStatusAttentionPolicy
{
    private readonly TimeProvider _time;
    private readonly ProgramStatusAttentionLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<string, ProgramStatusAttentionSignature> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _sent = [];
    private long _sequence;

    public ProgramStatusAttentionPolicy(TimeProvider time, ProgramStatusAttentionLimits? limits = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _limits = limits ?? ProgramStatusAttentionLimits.Default;
    }

    /// <summary>
    /// The program status of a pane changed. <paramref name="view"/> is the new view (null when the
    /// pane has no records). <paramref name="programStatusOwnsPaneStatus"/> is false when a higher
    /// source holds the pane status. That source has its own notice, so this policy stays quiet.
    /// Returns the items to publish now: this change if the limit allows, and any pending item that is due.
    /// </summary>
    public IReadOnlyList<ProgramStatusAttentionItem> Observe(
        string paneId,
        string tabId,
        string workspaceId,
        ProgramStatusView? view,
        bool programStatusOwnsPaneStatus = true)
    {
        ArgumentNullException.ThrowIfNull(paneId);
        ArgumentNullException.ThrowIfNull(tabId);
        ArgumentNullException.ThrowIfNull(workspaceId);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var released = ReleaseUnlocked(now);

            if (view is null || !programStatusOwnsPaneStatus)
            {
                _last.Remove(paneId);
                _pending.Remove(paneId);
                return released;
            }

            var signature = new ProgramStatusAttentionSignature(view.State, view.Kind, view.Msg);
            _last.TryGetValue(paneId, out var previous);
            _last[paneId] = signature;

            if (!ProgramStatusAttentionRule.NeedsPerson(view.State))
            {
                _pending.Remove(paneId);
                return released;
            }

            if (previous is not null && !ProgramStatusAttentionRule.IsNewRequest(previous, signature))
                return released;

            var item = new ProgramStatusAttentionItem
            {
                PaneId = paneId,
                TabId = tabId,
                WorkspaceId = workspaceId,
                State = view.State,
                Kind = view.Kind,
                Source = view.Source,
                App = view.App,
                Msg = view.Msg,
                Priority = ProgramStatusAttentionRule.PriorityOf(view.State),
            };

            if (CanSendUnlocked(paneId, now))
            {
                _pending.Remove(paneId);
                MarkSentUnlocked(paneId, now);
                released.Add(item);
            }
            else
            {
                _pending[paneId] = new Pending(item, ++_sequence);
            }

            return released;
        }
    }

    /// <summary>The pane left the session. The policy drops all it holds for the pane.</summary>
    public void Forget(string paneId)
    {
        ArgumentNullException.ThrowIfNull(paneId);
        lock (_gate)
        {
            _last.Remove(paneId);
            _lastSent.Remove(paneId);
            _pending.Remove(paneId);
        }
    }

    /// <summary>The pending items that the limit allows now, oldest change first.</summary>
    public IReadOnlyList<ProgramStatusAttentionItem> Release()
    {
        lock (_gate)
            return ReleaseUnlocked(_time.GetUtcNow());
    }

    /// <summary>
    /// When the next pending item can go out. Null when nothing is pending.
    /// The time can be in the past. Call <see cref="Release"/> then.
    /// </summary>
    public DateTimeOffset? NextReleaseAt
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                PruneUnlocked(now);
                DateTimeOffset? next = null;
                foreach (var (paneId, _) in _pending)
                {
                    var ready = ReadyAtUnlocked(paneId, now);
                    if (next is null || ready < next)
                        next = ready;
                }

                return next;
            }
        }
    }

    private List<ProgramStatusAttentionItem> ReleaseUnlocked(DateTimeOffset now)
    {
        var released = new List<ProgramStatusAttentionItem>();
        if (_pending.Count == 0)
            return released;

        foreach (var (paneId, pending) in _pending.OrderBy(p => p.Value.Sequence).ToList())
        {
            if (!CanSendUnlocked(paneId, now))
                continue;

            _pending.Remove(paneId);
            MarkSentUnlocked(paneId, now);
            released.Add(pending.Item);
        }

        return released;
    }

    private bool CanSendUnlocked(string paneId, DateTimeOffset now)
    {
        PruneUnlocked(now);
        return _sent.Count < _limits.SessionMax && PaneReadyAtUnlocked(paneId) <= now;
    }

    private void MarkSentUnlocked(string paneId, DateTimeOffset now)
    {
        _sent.Add(now);
        _lastSent[paneId] = now;
    }

    private DateTimeOffset ReadyAtUnlocked(string paneId, DateTimeOffset now)
    {
        var ready = PaneReadyAtUnlocked(paneId);
        if (_sent.Count >= _limits.SessionMax)
        {
            var sessionReady = _sent[_sent.Count - _limits.SessionMax] + _limits.SessionWindow;
            if (sessionReady > ready)
                ready = sessionReady;
        }

        return ready < now ? now : ready;
    }

    private DateTimeOffset PaneReadyAtUnlocked(string paneId) =>
        _lastSent.TryGetValue(paneId, out var last) ? last + _limits.PaneWindow : DateTimeOffset.MinValue;

    private void PruneUnlocked(DateTimeOffset now)
    {
        var edge = now - _limits.SessionWindow;
        _sent.RemoveAll(at => at <= edge);
    }

    private sealed record Pending(ProgramStatusAttentionItem Item, long Sequence);
}
