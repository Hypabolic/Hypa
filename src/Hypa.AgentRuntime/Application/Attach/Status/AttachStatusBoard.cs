namespace Hypa.AgentRuntime.Application.Attach.Status;

/// <summary>
/// The attach status. Each owner has one entry. An owner sets or clears
/// only its own entry, so one owner cannot remove the message of another.
/// The board is thread-safe. The pump, the reconnect supervisor, and the
/// health monitor can write from their own threads.
/// </summary>
public sealed class AttachStatusBoard
{
    private readonly object _gate = new();
    private readonly Dictionary<StatusOwner, StatusEntry> _entries = [];
    private long _sequence;

    /// <summary>
    /// Sets the entry of an owner. A null or blank text clears the entry of
    /// the owner instead.
    /// </summary>
    public void Set(StatusOwner owner, StatusSeverity severity, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Clear(owner);
            return;
        }

        lock (_gate)
        {
            _sequence++;
            _entries[owner] = new StatusEntry(owner, severity, text, _sequence);
        }
    }

    /// <summary>Clears the entry of one owner. Other owners keep their entries.</summary>
    public void Clear(StatusOwner owner)
    {
        lock (_gate)
            _ = _entries.Remove(owner);
    }

    /// <summary>Clears every entry. Use this only for a full reset.</summary>
    public void ClearAll()
    {
        lock (_gate)
            _entries.Clear();
    }

    /// <summary>The entry of one owner, or null when the owner has none.</summary>
    public StatusEntry? Entry(StatusOwner owner)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(owner);
    }

    /// <summary>The entry to paint. An error wins over a notice. The newest entry wins within a severity.</summary>
    public StatusEntry? Top
    {
        get
        {
            lock (_gate)
                return LatestOf(StatusSeverity.Error) ?? LatestOf(StatusSeverity.Notice);
        }
    }

    /// <summary>The newest error entry, or null when no owner has an error.</summary>
    public StatusEntry? TopError
    {
        get
        {
            lock (_gate)
                return LatestOf(StatusSeverity.Error);
        }
    }

    /// <summary>The newest notice entry, or null when no owner has a notice.</summary>
    public StatusEntry? TopNotice
    {
        get
        {
            lock (_gate)
                return LatestOf(StatusSeverity.Notice);
        }
    }

    private StatusEntry? LatestOf(StatusSeverity severity)
    {
        StatusEntry? latest = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.Severity != severity)
                continue;
            if (latest is null || entry.Sequence > latest.Sequence)
                latest = entry;
        }

        return latest;
    }
}
