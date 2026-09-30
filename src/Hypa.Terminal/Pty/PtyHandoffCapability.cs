namespace Hypa.Terminal.Pty;

/// <summary>
/// Capability probe for H2 handoff. Process-I/O fallback never implements
/// <see cref="IPtyProcessControl"/> and never claims H2.
/// </summary>
public static class PtyHandoffCapability
{
    /// <summary>
    /// Returns the exporter when <paramref name="pty"/> supports H2 export; otherwise null.
    /// </summary>
    public static IPtyProcessControl? TryGetExporter(IPtyProcess? pty) =>
        pty as IPtyProcessControl;

    /// <summary>
    /// True only when the process is a helper-backed control surface on a Unix host.
    /// </summary>
    public static bool SupportsH2(IPtyProcess? pty)
    {
        if (pty is not IPtyProcessControl)
            return false;
        if (OperatingSystem.IsWindows())
            return false;
        return pty is PtyHostProcess;
    }

    /// <summary>
    /// Capability-absent result for providers that do not implement handoff.
    /// </summary>
    public static PtyHandoffResult AbsentResult(IPtyProcess? pty)
    {
        if (pty is null)
            return PtyHandoffResult.CapabilityAbsent("No PTY process.");
        if (OperatingSystem.IsWindows())
            return PtyHandoffResult.PlatformUnsupported("H2 handoff is Unix-only.");
        if (pty is not IPtyProcessControl)
            return PtyHandoffResult.CapabilityAbsent(
                "Provider does not implement IPtyProcessControl; process-io never claims H2.");
        return PtyHandoffResult.CapabilityAbsent();
    }
}
