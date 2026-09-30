namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Process-level VT health snapshot for <c>runtime.health.vt</c>.
/// Constructed once at AgentServer startup and injected into the control plane.
/// Immutable after construction (record-like init properties).
/// Ghostty is the only pane VT. Missing native fails closed.
/// </summary>
public sealed class VtRuntimeHealth
{
    public string Provider { get; init; } = "ghostty";

    public string? FallbackReason { get; init; }

    public GhosttyProviderInfo? Ghostty { get; init; }

    /// <summary>Capability tokens advertised on <c>runtime.health.vt.capabilities</c>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = GhosttyCapabilities;

    public static readonly string[] GhosttyCapabilities =
    [
        "utf8", "cursor", "erase", "sgr", "alt_screen", "scroll_region", "wide_char",
    ];

    public static VtRuntimeHealth ForGhostty(GhosttyProviderInfo info) => new()
    {
        Provider = "ghostty",
        FallbackReason = null,
        Ghostty = info,
        Capabilities = GhosttyCapabilities,
    };
}
