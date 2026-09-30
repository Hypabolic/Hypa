namespace Hypa.Terminal.Pty;

/// <summary>
/// Selection and path options for <see cref="IPtyProcessFactory"/>.
/// Prefer <c>HYPA_PTY_PROVIDER</c> (process-io | hypa-pty-host).
/// </summary>
public sealed record PtyProviderOptions
{
    /// <summary>
    /// Selected provider. Record default is process-io for explicit construction.
    /// <see cref="FromEnvironment"/> selects hypa-pty-host on Unix when unset.
    /// </summary>
    public PtyProviderKind Provider { get; init; } = PtyProviderKind.ProcessIo;

    /// <summary>Optional absolute path to hypa-pty-host (else <c>HYPA_PTY_HOST</c> / resolve).</summary>
    public string? HelperPath { get; init; }

    /// <summary>Hello handshake timeout for the helper.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Spawn/Spawned wait timeout for hypa-pty-host.</summary>
    public TimeSpan SpawnTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When true and <see cref="Provider"/> is <see cref="PtyProviderKind.HypaPtyHost"/>,
    /// missing binary throws. When false, factory may fall back to process-io after log.
    /// Production selection fails closed (true).
    /// </summary>
    public bool RequireHelperBinary { get; init; } = true;

    /// <summary>
    /// Builds options from environment:
    /// <c>HYPA_PTY_PROVIDER</c> (process-io | hypa-pty-host),
    /// <c>HYPA_PTY_HOST</c> for helper path (absolute only).
    /// Unix default is hypa-pty-host. Windows default is process-io.
    /// </summary>
    public static PtyProviderOptions FromEnvironment()
    {
        var providerEnv = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        PtyProviderKind kind;

        if (!string.IsNullOrWhiteSpace(providerEnv))
        {
            kind = ParseProvider(providerEnv);
        }
        else if (OperatingSystem.IsWindows())
        {
            kind = PtyProviderKind.ProcessIo;
        }
        else
        {
            kind = PtyProviderKind.HypaPtyHost;
        }

        var helper = Environment.GetEnvironmentVariable("HYPA_PTY_HOST");
        if (string.IsNullOrWhiteSpace(helper))
            helper = null;

        // Fail closed when host is selected (Unix default and explicit).
        var require = kind == PtyProviderKind.HypaPtyHost;

        return new PtyProviderOptions
        {
            Provider = kind,
            HelperPath = helper,
            RequireHelperBinary = require,
        };
    }

    public static PtyProviderKind ParseProvider(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return PtyProviderKind.ProcessIo;

        var v = value.Trim();
        if (v.Equals("process-io", StringComparison.OrdinalIgnoreCase)
            || v.Equals("process_io", StringComparison.OrdinalIgnoreCase)
            || v.Equals("process", StringComparison.OrdinalIgnoreCase))
            return PtyProviderKind.ProcessIo;

        if (v.Equals("hypa-pty-host", StringComparison.OrdinalIgnoreCase)
            || v.Equals("hypa_pty_host", StringComparison.OrdinalIgnoreCase)
            || v.Equals("pty-host", StringComparison.OrdinalIgnoreCase)
            || v.Equals("host", StringComparison.OrdinalIgnoreCase))
            return PtyProviderKind.HypaPtyHost;

        throw new ArgumentException(
            $"Unknown HYPA_PTY_PROVIDER '{value}'. Use process-io or hypa-pty-host.",
            nameof(value));
    }

    /// <summary>Wire / health provider string (snake_case).</summary>
    public string ProviderWireName => Provider switch
    {
        PtyProviderKind.HypaPtyHost => "hypa-pty-host",
        _ => "process-io",
    };

    /// <summary>Whether the selected provider claims interactive TTY semantics.</summary>
    public bool Interactive => Provider is PtyProviderKind.HypaPtyHost;
}
