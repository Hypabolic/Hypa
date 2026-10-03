using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Runtime side of a pane: owns PTY + VT. Separated from <see cref="PaneState"/>.
/// </summary>
public interface IPaneRuntime : IAsyncDisposable
{
    PaneId Id { get; }
    bool IsAlive { get; }
    int? ExitCode { get; }

    /// <summary>OS process id when a child is running; null before start or after dispose.</summary>
    int? Pid { get; }

    /// <summary>Live reported directory, then shell process directory; null when unavailable.</summary>
    string? ReadWorkingDirectory() => null;

    Task StartAsync(CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask WriteTextAsync(string text, CancellationToken ct);
    ValueTask ResizeAsync(int cols, int rows, CancellationToken ct);

    /// <summary>Visible grid text (no ANSI), for agent.read source=visible.</summary>
    string ReadVisibleText();

    /// <summary>Recent scrollback as plain text. Soft wrap stays as newlines.</summary>
    string ReadRecentText(int maxLines);

    /// <summary>Recent scrollback with soft wrap removed.</summary>
    string ReadRecentUnwrappedText(int maxLines);

    /// <summary>
    // / Recent unwrapped VT (ANSI) for file persist.
    /// <c>src/pane.rs:3075-3078</c> <c>snapshot_history</c>. Null when empty.
    /// Default is null so test doubles compile.
    /// </summary>
    string? SnapshotHistory() => null;

    /// <summary>
    // / Replay saved ANSI into the pane VT before PTY output.
    /// <c>src/pane/terminal.rs:1540-1554</c> <c>seed_history_ansi</c>.
    /// Default is a no-op so test doubles compile.
    /// </summary>
    void SeedHistoryAnsi(string ansi)
    {
    }

    /// <summary>Detection-friendly snapshot (ANSI stripped, stable controls).</summary>
    string ReadDetectionText();

    /// <summary>
    // / Latest OSC 0/2 title retained for detection.
    /// <c>src/pane/terminal.rs:1285-1292</c>. Default empty so test doubles
    /// compile.
    /// </summary>
    string ReadDetectionOscTitle() => "";

    /// <summary>
    // / Latest OSC 9 progress payload retained for detection.
    /// <c>src/pane/terminal.rs:1294-1301</c>. Default empty.
    /// </summary>
    string ReadDetectionOscProgress() => "";

    /// <summary>
    /// Drop retained OSC title/progress when the foreground agent changes.
    /// </summary>
    void ClearAgentOscState()
    {
    }

    event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
    event Action<IPaneRuntime, int>? BellReceived;
    event Action<IPaneRuntime, int>? Exited;

    /// <summary>
    /// Apply host OSC 10/11 into the pane VT when the child does not own
    // / that channel.
    /// Default is a no-op so test doubles compile.
    /// </summary>
    void ApplyHostTerminalTheme(HostTerminalTheme theme)
    {
    }

    /// <summary>
    /// Versioned apply. <paramref name="version"/> is the mux generation.
    /// A lower version must not replace a newer apply. Version 0 is
    /// unversioned and always applies.
    /// </summary>
    void ApplyHostTerminalTheme(HostTerminalTheme theme, long version) =>
        ApplyHostTerminalTheme(theme);
}
