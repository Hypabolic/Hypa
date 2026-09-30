namespace Hypa.Terminal.Pty;

/// <summary>
/// Private hypa-pty-host frame type codes (not control-plane NDJSON).
/// See docs/plans/AgentRuntime/pty-host-ipc.md.
/// </summary>
public enum PtyHostFrameType : byte
{
    Hello = 1,
    Spawn = 2,
    Output = 3,
    Input = 4,
    Resize = 5,
    Signal = 6,
    Exit = 7,
    Close = 8,
    Error = 9,

    /// <summary>Pause Output and SCM_RIGHTS master FD to managed via AF_UNIX path.</summary>
    PauseOutput = 10,

    /// <summary>Adopt master FD plus existing child_pid (no fork).</summary>
    Adopt = 11,

    /// <summary>Helper to server ack after successful Adopt.</summary>
    Adopted = 12,

    /// <summary>Old helper closes master only; never kills child.</summary>
    CloseOldOwner = 13,

    /// <summary>
    /// Helper → server after successful Spawn. Payload: int32 little-endian child_pid.
    /// </summary>
    Spawned = 14,

    /// <summary>
    /// old helper unpauses after failed export (clear pause identity; keep session).
    /// </summary>
    ResumeHandoff = 15,

    /// <summary>
    /// new helper aborts after Adopt — close master only; never kill child.
    /// </summary>
    ReleaseAdopted = 16,
}
