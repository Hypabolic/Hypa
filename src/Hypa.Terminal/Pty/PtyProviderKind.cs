namespace Hypa.Terminal.Pty;

/// <summary>
/// Strategy selector for pane process I/O.
/// Unix default (unset <c>HYPA_PTY_PROVIDER</c>) is <see cref="HypaPtyHost"/>.
/// Windows default is <see cref="ProcessIo"/>. Tests opt in to process-io.
/// </summary>
public enum PtyProviderKind
{
    /// <summary>Redirected Process streams — not a full interactive TTY.</summary>
    ProcessIo = 0,

    /// <summary>Production interactive PTY via native hypa-pty-host (no managed fork).</summary>
    HypaPtyHost = 1,
}
