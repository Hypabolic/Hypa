namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Live VT paint kind after a coalesced tick. Basic always reports
/// <see cref="Full"/>. Ghostty may report clean, a dirty-row patch, or a
/// full grid.
/// </summary>
public enum PaneVtPaintKind
{
    Full = 0,
    Patch = 1,
    Clean = 2,
}

/// <summary>
// / Feed-bound paint decision.
/// captures <c>request_render</c> with that byte slice. Do not re-read
/// DEC 2026 after an async wait.
/// </summary>
public readonly record struct PaneFeedPaintDecision(long FeedGeneration, bool RequestPaint);

/// <summary>
/// Application port for a structured VT snapshot as compact JSON.
/// Implemented by pane runtimes that own an <c>IVtEngine</c>.
/// Not on <see cref="IPaneRuntime"/> — keeps Terminal types off this layer.
/// </summary>
public interface IPaneVtSnapshot
{
    /// <summary>
    /// Monotonic count of VT feeds. Incremented when bytes are applied to the grid.
    /// Capture and this value must describe the same grid generation.
    /// </summary>
    long FeedGeneration { get; }

    /// <summary>
    // / Paint decision captured with the last VT feed.
    /// <c>src/pane/terminal.rs:1345</c> <c>request_render = !synchronized_output</c>.
    /// Default requests paint so stubs keep today's always-request behaviour.
    /// </summary>
    PaneFeedPaintDecision LastFeedPaintDecision => new(FeedGeneration, true);

    /// <summary>
    /// Capture, normalize, and compact-serialize the current grid.
    /// <paramref name="feedGeneration"/> is the feed count at capture time.
    /// Returns false when capture is unavailable (disposed engine, stub pane).
    /// Must not throw for a disposed Ghostty engine.
    /// May update Ghostty render-state so cell colours resolve. Must not clear
    /// dirty flags. Call <see cref="CommitPostedPaint"/> after a successful post.
    /// </summary>
    bool TryCaptureSnapshotJson(out string json, out long feedGeneration);

    /// <summary>
    /// Capture a coalesced live paint. Ghostty may return clean, a dirty-row
    /// subset, or a full grid. Basic always returns a full grid.
    /// A clean capture is success with empty JSON and no epoch change.
    /// A non-zero scroll origin is Full: Ghostty reads the current viewport;
    /// Basic reads the origin window. Do not return Clean for offset != 0.
    /// A scrolled window posted from pane.scroll is a Full attach snapshot.
    /// Must not throw for a disposed Ghostty engine.
    /// Must not clear Ghostty dirty flags. Call <see cref="CommitPostedPaint"/>
    /// after a successful post.
    /// </summary>
    bool TryCaptureLivePaintJson(
        out string json,
        out long feedGeneration,
        out PaneVtPaintKind kind,
        out IReadOnlyList<int>? dirtyRows)
    {
        dirtyRows = null;
        kind = PaneVtPaintKind.Full;
        return TryCaptureSnapshotJson(out json, out feedGeneration);
    }

    /// <summary>
    /// Capture the semantic pane frame before JSON pack. Default is no frame.
    /// </summary>
    bool TryCaptureLivePaintFrame(
        out VtFrame? frame,
        out long feedGeneration,
        out PaneVtPaintKind kind)
    {
        return TryCaptureLivePaintFrame(out frame, out feedGeneration, out kind, out _);
    }

    /// <summary>
    /// Capture a live paint as a complete frame or a dirty-row patch.
    /// Partial returns <paramref name="patch"/> and a null frame. Full
    /// returns a complete <see cref="VtFrame"/>. Clean may return a
    /// modes-only frame so a mouse-only DECSET can pack. Default is no frame.
    /// </summary>
    bool TryCaptureLivePaintFrame(
        out VtFrame? frame,
        out long feedGeneration,
        out PaneVtPaintKind kind,
        out VtDirtyRowPatch? patch)
    {
        patch = null;
        frame = null;
        kind = PaneVtPaintKind.Full;
        feedGeneration = 0;
        return false;
    }

    /// <summary>Capture including scrolled origin. Used by attach Full re-anchor.</summary>
    bool TryCaptureAttachFrame(out VtFrame? frame, out long feedGeneration)
    {
        frame = null;
        feedGeneration = 0;
        return false;
    }

    /// <summary>
    // / Capture the attach snapshot as a typed frame.
    /// <c>src/server/render_stream.rs:138-143</c> moves the typed frame
    /// into one message. Default is no frame.
    /// </summary>
    bool TryCaptureAttachSnapshot(out VtAttachSnapshot? snapshot, out long feedGeneration)
    {
        snapshot = null;
        feedGeneration = 0;
        return false;
    }

    /// <summary>
    /// Clear Ghostty dirty after a successful attach or live snapshot post.
    /// No-op for Basic VT. Must not throw for a disposed Ghostty engine.
    /// </summary>
    void CommitPostedPaint()
    {
    }

    /// <summary>
    /// Server VT scroll origin and max offset. Ghostty polls scrollbar data
    /// id 9. Basic VT may copy scrollback text to measure max offset.
    /// </summary>
    bool TryGetScrollMetrics(out int offset, out int maxOffset);

    /// <summary>
    /// Stored scroll origin. Does not poll Ghostty or copy scrollback.
    /// PTY origin-only callbacks must use this. Capture, set, and metrics
    /// refresh the cache.
    /// </summary>
    bool TryGetScrollOrigin(out int offset);

    /// <summary>
    /// Set the server VT scroll origin. Offset is clamped to
    /// <c>[0, maxOffset]</c>. Returns false when the engine has no scroll
    /// metrics.
    /// </summary>
    bool TrySetScrollOrigin(int offset);
}
