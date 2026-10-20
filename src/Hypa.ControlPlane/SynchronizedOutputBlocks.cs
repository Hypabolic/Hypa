using System.Collections.Concurrent;

namespace Hypa.ControlPlane;

/// <summary>
/// Per pane: the start time of the open DEC 2026 synchronized output block.
/// The live paint path holds a paint inside a block until
/// <see cref="ControlPlaneService.SynchronizedOutputTimeout"/> after this time.
/// <para>
/// The PTY reader thread sets and clears the start in feed order. A flush
/// thread that sees a block with no start sets a provisional start, so a
/// block that the reader never recorded still times out. A flush can read
/// the VT sync mode before a close and set its start after it. For that
/// reason the reader replaces a provisional start when it sees the next
/// block open: a late flush cannot give the next block an old start.
/// </para>
/// </summary>
internal sealed class SynchronizedOutputBlocks
{
    private readonly ConcurrentDictionary<string, Start> _starts = new(StringComparer.Ordinal);

    /// <summary>
    /// PTY reader thread, in feed order. A feed that ends inside a block sets
    /// the start to <paramref name="now"/> when the pane has no start or only
    /// a provisional one. A feed that ends outside a block clears the start.
    /// <paramref name="observed"/> is false when no live observer can see the
    /// pane: the reader then records nothing, and the first held paint sets a
    /// provisional start.
    /// </summary>
    public void NoteFeed(string paneId, bool insideBlock, bool observed, DateTimeOffset now)
    {
        if (!insideBlock)
        {
            _ = _starts.TryRemove(paneId, out _);
            return;
        }

        if (observed)
        {
            _ = _starts.AddOrUpdate(
                paneId,
                new Start(now, Provisional: false),
                (_, old) => old.Provisional ? new Start(now, Provisional: false) : old);
        }
    }

    /// <summary>
    /// Flush thread. The start of the block that the caller saw open. When the
    /// pane has no start, <paramref name="now"/> becomes a provisional start.
    /// </summary>
    public DateTimeOffset StartOf(string paneId, DateTimeOffset now) =>
        _starts.GetOrAdd(paneId, new Start(now, Provisional: true)).At;

    /// <summary>Drop the start: the pane closed, or its occupant changed.</summary>
    public void Forget(string paneId) => _ = _starts.TryRemove(paneId, out _);

    private readonly record struct Start(DateTimeOffset At, bool Provisional);
}
