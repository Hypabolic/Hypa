namespace Hypa.Terminal.Pty;

/// <summary>
/// Optional capability on interactive PTY providers (hypa-pty-host).
/// Process I/O fallback does not implement this interface and never claims H2.
/// </summary>
public interface IPtyProcessControl
{
    /// <summary>
    /// Deliver a signal to the child process group (session leader called setsid).
    /// Common values: 15 (SIGTERM), 9 (SIGKILL), 2 (SIGINT).
    /// </summary>
    ValueTask SendSignalAsync(int signal, CancellationToken ct = default);

    /// <summary>
    /// Same-host H2 handoff: export the live session via <paramref name="handoff"/>
    /// (private UDS + SCM_RIGHTS). Never uses a managed Stream as FD fiction.
    /// On failure the old owner remains authoritative.
    /// </summary>
    ValueTask<PtyHandoffResult> ExportHandoffAsync(
        IPtyHandoffPort handoff,
        PtyHandoffExportOptions? options = null,
        CancellationToken ct = default);
}
