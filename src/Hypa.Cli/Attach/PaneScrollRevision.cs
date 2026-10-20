using System.Text.Json;

namespace Hypa.Cli.Attach;

/// <summary>
/// The order of one scroll state of a pane, as the mux sends it in a
/// <c>pane.scroll</c> reply and in <c>pane.scroll_changed</c>.
/// <see cref="Revision"/> goes up for each new state of the pane.
/// <see cref="Epoch"/> names the mux process: revisions compare only inside
/// one epoch, so a new mux after a restart is never older.
/// </summary>
internal readonly record struct PaneScrollRevision(string Epoch, long Revision)
{
    public const string EpochField = "revision_epoch";

    public const string RevisionField = "revision";

    /// <summary>True when this state came before <paramref name="applied"/> from the same mux.</summary>
    public bool IsOlderThan(PaneScrollRevision applied) =>
        string.Equals(Epoch, applied.Epoch, StringComparison.Ordinal) && Revision < applied.Revision;

    /// <summary>The revision of a reply or an event payload. Null from a mux that sends none.</summary>
    public static PaneScrollRevision? TryRead(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(EpochField, out var epoch)
            || epoch.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty(RevisionField, out var revision)
            || revision.ValueKind != JsonValueKind.Number
            || !revision.TryGetInt64(out var value))
        {
            return null;
        }

        var name = epoch.GetString();
        return string.IsNullOrEmpty(name) ? null : new PaneScrollRevision(name, value);
    }
}

/// <summary>
/// The mux epochs that the attach saw, and which one is current. Epochs
/// compare for equality only. The text of an epoch starts with the start
/// time of the mux, but the wall clock can go back across a restart, so
/// the attach does not sort epochs.
/// <list type="bullet">
/// <item>A <c>pane.scroll</c> reply comes from the mux that serves the
/// attach. Its epoch becomes current and is pinned. Until the next event
/// subscription, an event of another epoch is a replayed record of an
/// earlier mux, and it is dropped.</item>
/// <item>One subscription sends its events in journal order, so an epoch
/// that comes later in that stream is a later mux. While no reply pins the
/// epoch, an event of another epoch becomes current. The epoch before it is
/// retired when the same stream sent it.</item>
/// <item>An event of a retired epoch is dropped.</item>
/// </list>
/// The caller holds the live gate.
/// </summary>
internal sealed class PaneScrollEpochs
{
    /// <summary>Each mux restart retires one epoch. This limit keeps the set small.</summary>
    private const int RetiredLimit = 32;

    /// <summary>The retired epochs, the oldest first.</summary>
    private readonly List<string> _retired = [];
    private readonly HashSet<string> _seenInStream = new(StringComparer.Ordinal);
    private string? _current;
    private bool _pinned;

    /// <summary>
    /// True when a state of <paramref name="epoch"/> can apply. A true
    /// result makes <paramref name="epoch"/> the current epoch.
    /// </summary>
    public bool Admit(string epoch, bool fromReply)
    {
        ArgumentException.ThrowIfNullOrEmpty(epoch);
        if (fromReply)
        {
            // The mux that answers is the latest mux. Each other epoch is earlier.
            if (!string.Equals(epoch, _current, StringComparison.Ordinal))
                Switch(epoch, retirePrevious: _current is not null);
            _retired.Remove(epoch);
            _pinned = true;
            return true;
        }

        if (string.Equals(epoch, _current, StringComparison.Ordinal))
        {
            _seenInStream.Add(epoch);
            return true;
        }

        if (_pinned || _retired.Contains(epoch))
            return false;

        // Only the stream order says that this epoch is later. An epoch of
        // an earlier subscription can come again in a replay.
        Switch(epoch, retirePrevious: _current is { } previous && _seenInStream.Contains(previous));
        return true;
    }

    /// <summary>
    /// True when <paramref name="epoch"/> is the current epoch and the mux
    /// showed it again after the last event subscription started: a reply
    /// pinned it, or an event of the current stream had it. Before that, the
    /// attach does not know which mux serves it. A mux that restarted can be
    /// a mux that sends no epoch.
    /// </summary>
    public bool IsConfirmed(string epoch) =>
        string.Equals(epoch, _current, StringComparison.Ordinal)
        && (_pinned || _seenInStream.Contains(epoch));

    /// <summary>
    /// A new event subscription started. It can come from a restarted mux,
    /// so no epoch is pinned. The stream order of the earlier subscription
    /// does not order the epochs of this one.
    /// </summary>
    public void Unpin()
    {
        _pinned = false;
        _seenInStream.Clear();
    }

    /// <summary>The mux that serves the attach sends no epoch, so the current epoch is an earlier mux.</summary>
    public void RetireCurrent()
    {
        if (_current is { } current)
            Retire(current);
        _current = null;
        _pinned = false;
    }

    public void Clear()
    {
        _retired.Clear();
        _seenInStream.Clear();
        _current = null;
        _pinned = false;
    }

    private void Switch(string epoch, bool retirePrevious)
    {
        if (retirePrevious && _current is { } previous)
            Retire(previous);
        _current = epoch;
        _seenInStream.Add(epoch);
    }

    private void Retire(string epoch)
    {
        if (_retired.Contains(epoch))
            return;
        _retired.Add(epoch);
        if (_retired.Count > RetiredLimit)
            _retired.RemoveAt(0);
    }
}
