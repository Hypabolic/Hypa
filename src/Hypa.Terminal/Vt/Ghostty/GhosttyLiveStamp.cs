using Hypa.AgentRuntime.Application;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// The terminal state that a retained live grid was stamped against. A
/// dirty-row stamp is correct only when the current state has the same
/// value. Ghostty marks no row dirty for some of these changes (a colour
/// change), so the stamp compares them itself.
/// </summary>
internal readonly record struct LiveStampBase(
    int Cols,
    int Rows,
    string Screen,
    ulong ScrollTotal,
    ulong ScrollOffset,
    long ColorEpoch,
    bool ReverseVideo,
    long DirtyClearEpoch)
{
    /// <summary>The first field that differs, as a fallback reason. None when equal.</summary>
    public LiveStampFallbackReason Compare(in LiveStampBase current)
    {
        if (Cols != current.Cols || Rows != current.Rows)
            return LiveStampFallbackReason.Resize;
        if (!string.Equals(Screen, current.Screen, StringComparison.Ordinal))
            return LiveStampFallbackReason.ScreenSwitch;
        if (ScrollTotal != current.ScrollTotal || ScrollOffset != current.ScrollOffset)
            return LiveStampFallbackReason.ScrollOffsetChange;
        if (ColorEpoch != current.ColorEpoch || ReverseVideo != current.ReverseVideo)
            return LiveStampFallbackReason.ColorChange;
        if (DirtyClearEpoch != current.DirtyClearEpoch)
            return LiveStampFallbackReason.DirtyFlagsCleared;
        return LiveStampFallbackReason.None;
    }
}

/// <summary>
/// The result of one live stamp. The pane reuses one instance for each
/// capture. Read it only while the pane capture lock is held.
/// </summary>
internal sealed class LiveStampOutcome
{
    /// <summary>The Ghostty dirty kind that the stamp acted on.</summary>
    public uint DirtyKind { get; set; }

    public VtFrameCursor Cursor { get; set; }

    public VtFrameModes Modes { get; set; }

    public string ActiveScreen { get; set; } = "main";

    /// <summary>False for a Clean capture: no cell was stamped.</summary>
    public bool Stamped { get; set; }

    /// <summary>True when the stamp wrote every viewport row into the destination.</summary>
    public bool StampedFull { get; set; }

    /// <summary>True when a full check stamp was also written into the check buffer.</summary>
    public bool Verified { get; set; }

    /// <summary>The reason for a full stamp. None for a dirty-row stamp.</summary>
    public LiveStampFallbackReason Reason { get; set; }

    /// <summary>The rows that a dirty-row stamp wrote, in ascending order.</summary>
    public List<int> DirtyRows { get; } = [];

    /// <summary>The cells that the stamp read. A check stamp adds its cells.</summary>
    public int CellsVisited { get; set; }

    public LiveStampBase Base { get; set; }

    public void Reset()
    {
        DirtyKind = GhosttyNative.RenderStateDirtyFull;
        Cursor = default;
        Modes = default;
        ActiveScreen = "main";
        Stamped = false;
        StampedFull = false;
        Verified = false;
        Reason = LiveStampFallbackReason.None;
        DirtyRows.Clear();
        CellsVisited = 0;
        Base = default;
    }
}
