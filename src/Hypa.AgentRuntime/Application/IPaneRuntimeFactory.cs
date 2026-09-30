using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.AgentRuntime.Application;

public sealed record PaneSpawnOptions
{
    public required PaneId Id { get; init; }
    public required string Cwd { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    /// <summary>
    /// </summary>
    public bool StripPaneIdEnv { get; init; }
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 40;
    public long ScrollbackLimitBytes { get; init; } = AttachAdvancedConfig.DefaultScrollbackLimitBytes;
    public AttachTerminalConfig Terminal { get; init; } = AttachTerminalConfig.Default;
    public HostTerminalTheme HostTheme { get; init; } = HostTerminalTheme.Empty;

    /// <summary>
    /// Seeded into the VT before the PTY starts.
    /// </summary>
    public string? InitialHistoryAnsi { get; init; }
}

public interface IPaneRuntimeFactory
{
    IPaneRuntime Create(PaneSpawnOptions options);

    /// <summary>
    /// Wire name for <c>runtime.health</c> <c>pty.provider</c>.
    /// Test stubs default to process-io. Production factory reports its selection.
    /// </summary>
    string PtyProvider => "process-io";

    /// <summary>Whether the factory claims interactive TTY semantics.</summary>
    bool PtyInteractive => false;
}
