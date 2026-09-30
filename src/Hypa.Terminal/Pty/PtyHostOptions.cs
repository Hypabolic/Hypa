namespace Hypa.Terminal.Pty;

/// <summary>
/// Options for starting a supervised hypa-pty-host process.
/// </summary>
public sealed record PtyHostOptions
{
    /// <summary>
    /// Absolute path to hypa-pty-host. When null, resolution uses
    /// HYPA_PTY_HOST → beside apphost → native/runtimes/&lt;rid&gt;/native/hypa-pty-host.
    /// </summary>
    public string? HelperPath { get; init; }

    /// <summary>Timeout for the Hello handshake.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Working directory for the helper process itself (not the child session).</summary>
    public string? HelperWorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment variables for the helper process (not the PTY child).
    /// Used by tests to set <c>HYPTY_HANDOFF_PAUSE_MS</c> for pause-safety expiry.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HelperEnvironment { get; init; }
}
