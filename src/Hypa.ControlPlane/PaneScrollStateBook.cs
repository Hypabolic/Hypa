using System.Collections.Concurrent;
using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// One scroll state of a pane. <see cref="Revision"/> goes up for each new
/// state of the pane, so an attach can drop a state that is older than the
/// last state that it applied.
/// </summary>
internal readonly record struct PaneScrollState(int Offset, int MaxOffset, long Revision);

/// <summary>
/// The latest scroll state of each pane, and the last state that went out
/// in <c>pane.scroll_changed</c>. Each pane has its own lock, so a slow VT
/// read of one pane does not hold back the scroll of another pane. A read
/// of the VT metrics runs under the lock of the pane, so the stored states
/// of a pane and their revisions have the same order.
/// </summary>
internal sealed class PaneScrollStateBook
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>
    /// The id of this mux process: its start time in UTC ticks (20 digits),
    /// then a random part. Revisions compare only inside one epoch. An
    /// attach compares epochs for equality only, because the wall clock can
    /// go back across a restart (see <c>PaneScrollEpochs</c> in the attach).
    /// The start time stays in the text for an older attach, which sorts
    /// epochs as text.
    /// </summary>
    public string Epoch { get; } =
        DateTime.UtcNow.Ticks.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Read the metrics of a pane and store them when they changed.
    /// Returns the stored state and true when it is new.
    /// </summary>
    public (PaneScrollState State, bool Changed) Observe(string paneId, Func<(int Offset, int MaxOffset)> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var slot = SlotOf(paneId);
        lock (slot)
        {
            var (offset, maxOffset) = read();
            return slot.Store(offset, maxOffset);
        }
    }

    /// <summary>
    /// Run <paramref name="apply"/> under the lock of the pane, then read and
    /// store the metrics. A <c>pane.scroll</c> request uses it, so that its
    /// reply has the state that its own change made.
    /// </summary>
    public (PaneScrollState State, bool Changed)? ApplyAndObserve(
        string paneId,
        Func<bool> apply,
        Func<(int Offset, int MaxOffset)?> read)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(read);
        var slot = SlotOf(paneId);
        lock (slot)
        {
            if (!apply() || read() is not { } metrics)
                return null;
            return slot.Store(metrics.Offset, metrics.MaxOffset);
        }
    }

    /// <summary>
    /// The PTY output path reads only the stored VT origin. The first origin
    /// of a pane is stored with no emit. An origin above the last known
    /// <c>max_offset</c> is an incomplete pair: the next full paint sends the
    /// offset and the max together.
    /// </summary>
    public (PaneScrollState State, bool Changed)? ObserveOrigin(string paneId, Func<int?> readOrigin)
    {
        ArgumentNullException.ThrowIfNull(readOrigin);
        var slot = SlotOf(paneId);
        lock (slot)
        {
            // Read under the lock of the pane. An origin read before a wheel
            // step and stored after it would get a newer revision than the
            // step and set the view back.
            if (readOrigin() is not { } origin)
                return null;
            if (slot.Latest is not { } previous)
            {
                slot.Store(origin, 0);
                return (slot.Latest!.Value, false);
            }

            if (previous.Offset == origin || origin > previous.MaxOffset)
                return (previous, false);
            return slot.Store(origin, previous.MaxOffset);
        }
    }

    /// <summary>Store a state that a test sets. Returns true when it is new.</summary>
    public bool Set(string paneId, int offset, int maxOffset)
    {
        var slot = SlotOf(paneId);
        lock (slot)
            return slot.Store(offset, maxOffset).Changed;
    }

    public bool TryGetLatest(string paneId, out PaneScrollState state)
    {
        state = default;
        if (!_slots.TryGetValue(paneId, out var slot))
            return false;
        lock (slot)
        {
            if (slot.Latest is not { } latest)
                return false;
            state = latest;
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="state"/> must go out in
    /// <c>pane.scroll_changed</c>. A pane that follows its output (offset 0)
    /// changes <c>max_offset</c> with each frame. That change goes out only
    /// when the scrollbar shows or hides, or when the thumb moves or changes
    /// its length on a track of any height up to
    /// <see cref="MaxTrackRows"/> (or <paramref name="paneRows"/> when it is
    /// more). An attach whose pane size another attach sets has a track of a
    /// different height than the pane rows. Any other change goes out.
    /// </summary>
    public bool ShouldEmit(string paneId, PaneScrollState state, int paneRows)
    {
        if (!_slots.TryGetValue(paneId, out var slot))
            return true;
        lock (slot)
        {
            if (slot.Sent is not { } sent)
                return true;
            if (state.Offset != 0 || sent.Offset != 0)
                return true;
            if ((state.MaxOffset > 0) != (sent.MaxOffset > 0))
                return true;
            return !SameThumbOnEachTrack(sent.MaxOffset, state.MaxOffset, Math.Max(paneRows, MaxTrackRows));
        }
    }

    /// <summary>
    /// The highest track that the offset-0 check covers. A pane box of more
    /// rows than this, on a mux whose pane has fewer rows, can show an old
    /// thumb length until the next event.
    /// </summary>
    public const int MaxTrackRows = 256;

    /// <summary>
    /// True when the thumb at offset 0 is the same for <paramref name="a"/>
    /// and <paramref name="b"/> on each track of 1 to
    /// <paramref name="maxTrack"/> rows. The attach shows a box of N rows
    /// with a track of N rows. At offset 0 the thumb sits at the bottom, so
    /// only its length can change.
    /// </summary>
    internal static bool SameThumbOnEachTrack(int a, int b, int maxTrack)
    {
        if (a == b)
            return true;
        // The length is 1 on each track when the highest track gives less
        // than 1.5 rows: the length goes down as max_offset goes up, and up
        // as the track goes up.
        if (OneRowThumb(a, maxTrack) && OneRowThumb(b, maxTrack))
            return true;
        for (var rows = 1; rows <= maxTrack; rows++)
        {
            if (ScrollbarThumbMath.Compute(0, a, rows, rows) != ScrollbarThumbMath.Compute(0, b, rows, rows))
                return false;
        }

        return true;
    }

    private static bool OneRowThumb(int maxOffset, int track) =>
        (long)track * track * 2 < 3L * (maxOffset + (long)track);

    /// <summary>The latest state of a pane, marked as sent. The emit journals this state.</summary>
    public PaneScrollState? TakeForEmit(string paneId)
    {
        if (!_slots.TryGetValue(paneId, out var slot))
            return null;
        lock (slot)
        {
            if (slot.Latest is not { } latest)
                return null;
            slot.Sent = latest;
            return latest;
        }
    }

    private Slot SlotOf(string paneId) => _slots.GetOrAdd(paneId, static _ => new Slot());

    private sealed class Slot
    {
        private long _revision;

        public PaneScrollState? Latest { get; private set; }

        public PaneScrollState? Sent { get; set; }

        public (PaneScrollState State, bool Changed) Store(int offset, int maxOffset)
        {
            if (Latest is { } latest && latest.Offset == offset && latest.MaxOffset == maxOffset)
                return (latest, false);
            var next = new PaneScrollState(offset, maxOffset, ++_revision);
            Latest = next;
            return (next, true);
        }
    }
}
