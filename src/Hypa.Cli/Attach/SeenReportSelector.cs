namespace Hypa.Cli.Attach;

/// <summary>
/// Decides which panes the attach tells the mux that the person has seen.
/// A pane with an unseen done or error counts as seen when its tab is the tab in view.
/// The tab in view is the same tab that <c>NotificationDirector</c> uses for notices.
/// The type holds no I/O: the caller sends the panes that <see cref="Take"/> returns in one call.
/// </summary>
public sealed class SeenReportSelector
{
    private readonly Dictionary<string, string?> _unseen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _lastBatch = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _pending = new(StringComparer.Ordinal);

    /// <summary>True when a pane with an unseen result waits to be reported.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>True for a status that is a result the person has to see: done or error.</summary>
    public static bool IsResult(string? state) => state is "done" or "error";

    /// <summary>
    /// A pane reported its state. A done or error that is not seen is remembered.
    /// It is pending at once when the pane is on <paramref name="tabInView"/>.
    /// Any other state, or a seen result, removes the pane.
    /// </summary>
    public void NotePane(string paneId, string? tabId, string? state, bool seen, string? tabInView)
    {
        ArgumentNullException.ThrowIfNull(paneId);
        if (seen || !IsResult(state))
        {
            Forget(paneId);
            return;
        }

        _unseen[paneId] = tabId;
        if (IsInView(tabId, tabInView))
            _pending.Add(paneId);
    }

    /// <summary>The tab in view changed. Every unseen pane of that tab is pending.</summary>
    public void NoteTabInView(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return;
        foreach (var (paneId, paneTab) in _unseen)
        {
            if (IsInView(paneTab, tabId))
                _pending.Add(paneId);
        }
    }

    /// <summary>A pane left the session or has no result any more.</summary>
    public void Forget(string paneId)
    {
        _unseen.Remove(paneId);
        _pending.Remove(paneId);
    }

    /// <summary>
    /// The panes to report now, sorted. The batch is empty when nothing is pending, so the caller sends no call.
    /// A reported pane leaves the list. The event that shows the pane as seen would remove it too.
    /// </summary>
    public IReadOnlyList<string> Take()
    {
        _lastBatch.Clear();
        if (_pending.Count == 0)
            return [];
        var batch = _pending.ToArray();
        foreach (var paneId in batch)
        {
            _lastBatch[paneId] = _unseen.GetValueOrDefault(paneId);
            _unseen.Remove(paneId);
        }

        _pending.Clear();
        return batch;
    }

    /// <summary>The call of the last batch failed. Its panes wait for the next chance.</summary>
    public void RestoreLastBatch()
    {
        foreach (var (paneId, tabId) in _lastBatch)
        {
            _unseen[paneId] = tabId;
            _pending.Add(paneId);
        }

        _lastBatch.Clear();
    }

    private static bool IsInView(string? tabId, string? tabInView) =>
        !string.IsNullOrWhiteSpace(tabId)
        && string.Equals(tabId, tabInView, StringComparison.Ordinal);
}
