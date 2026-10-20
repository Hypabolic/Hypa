namespace Hypa.Cli.Attach;

/// <summary>
/// The reanchor requests of the panes whose cells generation chain broke on
/// this client. A cells delta that does not chain on the applied generation
/// of its pane is dropped. Every later delta of that pane is dropped too,
/// until a full or reanchor frame of the pane comes. This type keeps one
/// request for each pane, so a stream of dropped deltas asks the mux for a
/// full frame one time.
/// <para>
/// A request is queued, then in flight, then sent. A pane has at most one
/// request in flight. A full or reanchor frame of the pane that the host
/// shows clears the request. A sent request that got no such frame in
/// <see cref="ResendAfter"/> can be queued again: the full frame can be lost,
/// for example while the presentation is frozen. A failed send also waits
/// <see cref="ResendAfter"/>, so a slow or broken link does not get one
/// request for each dropped delta.
/// </para>
/// <para>
/// The full frame of a request can come and be shown while its reply is
/// still in flight. A new break of the chain after that frame is recorded.
/// When the reply comes, the pane asks again at once. When the request
/// failed, the pane asks again after <see cref="ResendAfter"/>. A full
/// frame that the host shows before then ends the new break.
/// </para>
/// <para>
/// A pane asks again at once only one time until the host shows a full
/// frame of the pane. When the full frame of that request is refused too,
/// the pane asks again only after <see cref="ResendAfter"/>. Thus a full
/// frame that the host refuses each time cannot start one request for each
/// round trip.
/// </para>
/// <para>
/// Each request in flight and each retry after the wait has a ticket.
/// <see cref="Finish"/> and <see cref="TakeRetry"/> change the state only
/// for the ticket that is current for its pane. <see cref="Clear"/> ends the
/// retry of the pane, and <see cref="Reset"/> ends all tickets. Thus a late
/// reply of a request from before a reset, or the timer of an earlier retry,
/// cannot end or start a later request of the same pane.
/// </para>
/// All members are safe to call from more than one thread.
/// </summary>
internal sealed class PaneChainReanchorRequests
{
    /// <summary>The time after which a sent request with no full frame can go again.</summary>
    internal static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<string> _queued = [];
    private readonly Dictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);

    // The ticket of the request in flight of each pane.
    private readonly Dictionary<string, long> _inFlight = new(StringComparer.Ordinal);

    // In flight, and the host showed a full frame of the pane since the send.
    private readonly HashSet<string> _shownInFlight = new(StringComparer.Ordinal);

    // In flight, and the chain broke again after that full frame.
    private readonly HashSet<string> _brokenInFlight = new(StringComparer.Ordinal);

    // The pane asks again after the wait: the ticket of that retry.
    private readonly Dictionary<string, long> _retryAfterWait = new(StringComparer.Ordinal);

    // The pane asked again at once, and the host showed no full frame of the
    // pane since. The next ask waits for ResendAfter.
    private readonly HashSet<string> _askedAgainAtOnce = new(StringComparer.Ordinal);

    // The retries after the wait that need a timer that no reply starts.
    private readonly List<ChainReanchorTicket> _retryTimers = [];
    private long _requestsSent;
    private long _retriesChecked;

    // The last ticket. Tickets only go up, also across a reset.
    private long _lastTicket;

    /// <summary>True when at least one request waits to be sent.</summary>
    public bool HasQueued
    {
        get
        {
            lock (_gate)
                return _queued.Count > 0;
        }
    }

    /// <summary>
    /// True when at least one request waits to be sent, or a retry after the
    /// wait needs its timer.
    /// </summary>
    public bool HasWork
    {
        get
        {
            lock (_gate)
                return _queued.Count > 0 || _retryTimers.Count > 0;
        }
    }

    /// <summary>The number of requests that went to the mux.</summary>
    public long RequestsSent => Interlocked.Read(ref _requestsSent);

    /// <summary>
    /// The number of retry tickets that <see cref="TakeRetry"/> checked after
    /// their wait. The count and the queue change under the same lock.
    /// </summary>
    public long RetriesChecked
    {
        get
        {
            lock (_gate)
                return _retriesChecked;
        }
    }

    /// <summary>The number of requests that wait for their reply.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
                return _inFlight.Count;
        }
    }

    /// <summary>
    /// Queues a request for <paramref name="paneId"/>. Returns false when the
    /// pane already has a queued request, a request in flight, or a sent
    /// request that is younger than <see cref="ResendAfter"/>.
    /// </summary>
    public bool Request(string paneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
        {
            if (_inFlight.ContainsKey(paneId))
            {
                // A break after the full frame of this request is a new break.
                if (_shownInFlight.Remove(paneId))
                    _brokenInFlight.Add(paneId);
                return false;
            }

            if (_queued.Contains(paneId, StringComparer.Ordinal))
                return false;
            if (_sent.TryGetValue(paneId, out var sentAt) && now - sentAt < ResendAfter)
            {
                // A failed request of a new break waits; the retry covers this drop.
                return false;
            }

            _sent.Remove(paneId);
            _retryAfterWait.Remove(paneId);
            _queued.Add(paneId);
            return true;
        }
    }

    /// <summary>
    /// The host did not show a full frame of <paramref name="paneId"/> that
    /// came. Queues a request at once, also when a sent request is younger
    /// than <see cref="ResendAfter"/>. Returns false when a request is queued
    /// or in flight.
    /// <para>
    /// The pane asks again at once only one time until the host shows a full
    /// frame of the pane. After that, a refusal within
    /// <see cref="ResendAfter"/> of the last send records a retry after the
    /// wait: <see cref="TakeRetryTimers"/> gives its ticket, and the caller
    /// calls <see cref="TakeRetry"/> after <see cref="ResendAfter"/>.
    /// </para>
    /// </summary>
    public bool Reopen(string paneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
        {
            if (_inFlight.ContainsKey(paneId))
            {
                _shownInFlight.Remove(paneId);
                _brokenInFlight.Add(paneId);
                return false;
            }

            if (_queued.Contains(paneId, StringComparer.Ordinal))
                return false;
            if (_askedAgainAtOnce.Contains(paneId)
                && _sent.TryGetValue(paneId, out var sentAt)
                && now - sentAt < ResendAfter)
            {
                if (!_retryAfterWait.ContainsKey(paneId))
                    _retryTimers.Add(NewRetryUnlocked(paneId));
                return false;
            }

            _askedAgainAtOnce.Add(paneId);
            _sent.Remove(paneId);
            _retryAfterWait.Remove(paneId);
            _queued.Add(paneId);
            return true;
        }
    }

    /// <summary>
    /// Takes the retries after the wait that need a timer. The caller calls
    /// <see cref="TakeRetry"/> with each ticket after <see cref="ResendAfter"/>.
    /// </summary>
    public IReadOnlyList<ChainReanchorTicket> TakeRetryTimers()
    {
        lock (_gate)
        {
            if (_retryTimers.Count == 0)
                return [];
            var retries = _retryTimers.ToArray();
            _retryTimers.Clear();
            return retries;
        }
    }

    /// <summary>
    /// Takes the queued requests and records them as in flight and sent at
    /// <paramref name="now"/>. Each pane comes one time, with the ticket of
    /// its request. The caller gives that ticket to <see cref="Finish"/>.
    /// </summary>
    public IReadOnlyList<ChainReanchorTicket> TakeQueued(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_queued.Count == 0)
                return [];
            var requests = new ChainReanchorTicket[_queued.Count];
            for (var i = 0; i < _queued.Count; i++)
            {
                var paneId = _queued[i];
                var ticket = ++_lastTicket;
                _sent[paneId] = now;
                _inFlight[paneId] = ticket;
                requests[i] = new ChainReanchorTicket(paneId, ticket);
            }

            _queued.Clear();
            Interlocked.Add(ref _requestsSent, requests.Length);
            return requests;
        }
    }

    /// <summary>
    /// Ends the request in flight of <paramref name="request"/>. A failed
    /// request records <paramref name="now"/>, so the pane can ask again only
    /// after <see cref="ResendAfter"/>.
    /// <para>
    /// When the chain broke again after the full frame of this request, a
    /// reply queues a new request at once and returns
    /// <see cref="ChainReanchorFinish.Queued"/>. A failure returns
    /// <see cref="ChainReanchorFinish.RetryAfterWait"/>: the caller calls
    /// <see cref="TakeRetry"/> with <paramref name="retry"/> after
    /// <see cref="ResendAfter"/>. A reply also returns
    /// <see cref="ChainReanchorFinish.RetryAfterWait"/> when the pane already
    /// asked again at once and the host showed no full frame since.
    /// </para>
    /// <para>
    /// A ticket that is not the request in flight of its pane changes
    /// nothing and returns <see cref="ChainReanchorFinish.Stale"/>: a reset
    /// came after its send, and a later request can be in flight.
    /// </para>
    /// </summary>
    public ChainReanchorFinish Finish(
        ChainReanchorTicket request,
        bool failed,
        DateTimeOffset now,
        out ChainReanchorTicket retry)
    {
        retry = default;
        var paneId = request.PaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return ChainReanchorFinish.Stale;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(paneId, out var current) || current != request.Id)
                return ChainReanchorFinish.Stale;
            _inFlight.Remove(paneId);
            _shownInFlight.Remove(paneId);
            var broken = _brokenInFlight.Remove(paneId);
            if (failed)
                _sent[paneId] = now;
            if (!broken)
                return ChainReanchorFinish.Done;
            if (failed)
            {
                retry = NewRetryUnlocked(paneId);
                return ChainReanchorFinish.RetryAfterWait;
            }

            if (_askedAgainAtOnce.Contains(paneId))
            {
                _sent[paneId] = now;
                retry = NewRetryUnlocked(paneId);
                return ChainReanchorFinish.RetryAfterWait;
            }

            _askedAgainAtOnce.Add(paneId);
            _sent.Remove(paneId);
            if (!_queued.Contains(paneId, StringComparer.Ordinal))
                _queued.Add(paneId);
            return ChainReanchorFinish.Queued;
        }
    }

    /// <summary>
    /// After the wait of <paramref name="retry"/>: queues a request for its
    /// pane and returns true. Returns false when the ticket is not the
    /// current retry of the pane (the host showed a full frame of the pane in
    /// the meantime, a reset came, or a later retry replaced it), or when a
    /// request is already queued or in flight.
    /// </summary>
    public bool TakeRetry(ChainReanchorTicket retry)
    {
        var paneId = retry.PaneId;
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        lock (_gate)
        {
            _retriesChecked++;
            if (!_retryAfterWait.TryGetValue(paneId, out var current) || current != retry.Id)
                return false;
            _retryAfterWait.Remove(paneId);
            if (_inFlight.ContainsKey(paneId) || _queued.Contains(paneId, StringComparer.Ordinal))
                return false;
            _sent.Remove(paneId);
            _queued.Add(paneId);
            return true;
        }
    }

    /// <summary>
    /// Records a request of <paramref name="paneId"/> as sent without a send:
    /// another call already asks the mux for a full frame of the pane.
    /// </summary>
    public void NoteCovered(string paneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            if (_queued.Remove(paneId))
                _sent[paneId] = now;
        }
    }

    /// <summary>True when <paramref name="paneId"/> has a queued, in-flight or sent request.</summary>
    public bool IsOpen(string paneId)
    {
        lock (_gate)
        {
            return _queued.Contains(paneId, StringComparer.Ordinal)
                || _inFlight.ContainsKey(paneId)
                || _sent.ContainsKey(paneId);
        }
    }

    /// <summary>
    /// Clears the queued or sent request of <paramref name="paneId"/>: the
    /// host shows a full or reanchor frame of the pane, or the pane is gone
    /// or not visible. A request in flight stays in flight until its reply;
    /// a later break of the chain then counts as a new break. The retry
    /// after the wait of the pane ends, so its timer does nothing.
    /// </summary>
    public void Clear(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        lock (_gate)
        {
            _queued.Remove(paneId);
            _sent.Remove(paneId);
            _retryAfterWait.Remove(paneId);
            _askedAgainAtOnce.Remove(paneId);
            _retryTimers.RemoveAll(timer => string.Equals(timer.PaneId, paneId, StringComparison.Ordinal));
            _brokenInFlight.Remove(paneId);
            if (_inFlight.ContainsKey(paneId))
                _shownInFlight.Add(paneId);
        }
    }

    /// <summary>
    /// Forgets every request: the attach shows another mux connection, and
    /// each visible pane gets a full frame from its observe. A reply of a
    /// request to the earlier connection that comes later changes nothing,
    /// and a retry timer that started before the reset does nothing. The
    /// tickets do not start again, so no later request gets an earlier ticket.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queued.Clear();
            _sent.Clear();
            _inFlight.Clear();
            _shownInFlight.Clear();
            _brokenInFlight.Clear();
            _retryAfterWait.Clear();
            _askedAgainAtOnce.Clear();
            _retryTimers.Clear();
        }
    }

    private ChainReanchorTicket NewRetryUnlocked(string paneId)
    {
        var ticket = ++_lastTicket;
        _retryAfterWait[paneId] = ticket;
        return new ChainReanchorTicket(paneId, ticket);
    }
}

/// <summary>
/// One reanchor request in flight, or one retry after the wait, of a pane.
/// <paramref name="Id"/> is unique in its <see cref="PaneChainReanchorRequests"/>.
/// </summary>
internal readonly record struct ChainReanchorTicket(string PaneId, long Id);

/// <summary>What <see cref="PaneChainReanchorRequests.Finish"/> asks the caller to do next.</summary>
internal enum ChainReanchorFinish
{
    /// <summary>Nothing more.</summary>
    Done,

    /// <summary>A new request is queued. Start it.</summary>
    Queued,

    /// <summary>Call <see cref="PaneChainReanchorRequests.TakeRetry"/> after the wait.</summary>
    RetryAfterWait,

    /// <summary>The ticket is not the request in flight of its pane. Do nothing.</summary>
    Stale,
}
