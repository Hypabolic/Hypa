namespace Hypa.Terminal.Vt;

/// <summary>
/// Terminal state machine. Product engines are Ghostty VT.
/// Tests may inject a double. This interface stays AOT-friendly.
/// </summary>
/// <remarks>
/// <see cref="IDisposable"/> so pane teardown always releases native engines
/// (Ghostty terminals with off-heap scrollback).
/// </remarks>
public interface IVtEngine : IDisposable
{
    int Cols { get; }
    int Rows { get; }
    int CursorCol { get; }
    int CursorRow { get; }

    /// <summary>
    // / Ghostty DEC 2026 after the last feed.
    /// <c>src/pane/terminal.rs:1327-1345</c>. Default false so test
    /// doubles keep today's always-request paint.
    /// </summary>
    bool IsSynchronizedOutputActive => false;

    /// <summary>
    /// End an open DEC 2026 synchronized output block: reset mode 2026, as
    /// Ghostty does when its 1 s sync timer expires (<c>termio/Thread.zig</c>
    /// <c>sync_reset_ms</c>). The grid does not change. Returns true only when
    /// the mode was set and is now reset. The default returns false, so a
    /// test double keeps its own mode.
    /// </summary>
    bool TryEndSynchronizedOutput() => false;

    /// <summary>
    // / Alternate screen is active.
    /// <c>src/pane/terminal.rs:3304-3314</c> skips host-theme restore
    /// while the child is on the alternate screen. Default false so
    /// doubles stay on the primary screen.
    /// </summary>
    bool IsAlternateScreen => false;

    void Resize(int cols, int rows);
    void Feed(ReadOnlySpan<byte> data);
    void Feed(ReadOnlySpan<char> text);

    /// <summary>
    /// Feed live child output and append each reply that the engine writes
    /// back to the program (for example DSR, DA1, DECRQM) to
    /// <paramref name="replies"/>, in stream order. The engine does not write
    /// to the PTY. Only the live child byte path calls this. A restore, a
    /// replay, or a host feed uses <see cref="Feed(ReadOnlySpan{byte})"/>,
    /// which sends no reply. The default feeds and adds no reply.
    /// </summary>
    void FeedChildOutput(ReadOnlySpan<byte> data, List<byte[]> replies) => Feed(data);

    /// <summary>Take and clear Ground-state BELs accumulated by the last feed(s).</summary>
    int TakePendingBellCount();

    /// <summary>Visible screen as plain text lines (no trailing spaces strip optional).</summary>
    string GetVisibleText(bool trimTrailingWhitespace = true);

    /// <summary>Scrollback + screen, newest at end. Soft wrap stays as newlines.</summary>
    string GetRecentText(int maxLines, bool trimTrailingWhitespace = true);

    /// <summary>
    /// Same physical-row window as <see cref="GetRecentText"/>, then join wrap-flagged rows.
    /// Trailing whitespace is trimmed on each completed logical line, not on wrap rows.
    /// </summary>
    string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true);

    /// <summary>
    /// Same window as <see cref="GetRecentUnwrappedText"/> as VT (ANSI).
    /// Default is plain unwrapped text so test doubles compile.
    /// </summary>
    string GetRecentUnwrappedAnsi(int maxLines, bool trimTrailingWhitespace = true) =>
        GetRecentUnwrappedText(maxLines, trimTrailingWhitespace);

    /// <summary>
    /// Provider-neutral structured grid snapshot (cells, cursor, modes, screens).
    /// Contract: <c>VT-structured-snapshot.md</c>. No native handles on the result.
    /// </summary>
    VtStructuredSnapshot CaptureSnapshot();

    void Reset();
}
