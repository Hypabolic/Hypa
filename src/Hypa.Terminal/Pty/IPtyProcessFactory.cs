namespace Hypa.Terminal.Pty;

/// <summary>
/// Strategy factory for <see cref="IPtyProcess"/> (process-io | hypa-pty-host).
/// </summary>
public interface IPtyProcessFactory
{
    /// <summary>Resolved provider options (for health and diagnostics).</summary>
    PtyProviderOptions Options { get; }

    /// <summary>
    /// Spawns a child behind <see cref="IPtyProcess"/>.
    /// Synchronous to match existing PaneRuntime factory seam; host path blocks on async start.
    /// </summary>
    IPtyProcess Spawn(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env = null);
}
