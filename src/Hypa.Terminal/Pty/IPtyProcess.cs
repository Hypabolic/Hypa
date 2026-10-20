namespace Hypa.Terminal.Pty;

public interface IPtyProcess : IAsyncDisposable
{
    int Pid { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }

    Stream StandardInput { get; }
    Stream StandardOutput { get; }

    void Resize(int cols, int rows);
    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>
/// Optional PTY input seam that reports bytes accepted by the underlying
/// transport. Production PTY streams may implement this when the transport can
/// expose partial-write progress; ordinary Stream writes are all-or-fail.
/// </summary>
public interface IPtyInputProgress
{
    ValueTask<int> WriteAcceptedAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>
/// Optional PTY seam that reports the termios ECHO and ICANON state of the
/// pane PTY. hypa-pty-host minor 1 and higher implements it.
/// <see cref="ProcessPtyFallback"/> and an older helper do not: the mode is
/// then unknown.
/// </summary>
public interface IPtyTtyModeSource
{
    /// <summary>
    /// The mode of the output that the reader of
    /// <see cref="IPtyProcess.StandardOutput"/> has read: the newest mode
    /// change that came before or with those bytes. Null when unknown.
    /// </summary>
    PtyTtyMode? TtyMode { get; }

    /// <summary>
    /// A mode change arrived that applies to output the reader has already
    /// read, for example a silent <c>stty -echo</c> found at an input write.
    /// A change that applies to bytes not yet read raises nothing: the reader
    /// reads <see cref="TtyMode"/> after those bytes. The handler runs on the
    /// helper frame pump and must not block.
    /// </summary>
    event Action? TtyModeChanged;
}
