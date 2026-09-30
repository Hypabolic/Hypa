namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Capability tokens advertised by <c>runtime.health</c> and used for method negotiation.
/// Constants only in; server negotiation algorithm lands with method implementations.
/// </summary>
public static class ProtocolCapabilities
{
    public const string Core = "core";
    public const string Events = "events";
    public const string Leases = "leases";
    public const string Terminal = "terminal";
    public const string Binding = "binding";
    public const string AgentWait = "agent_wait";

    /// <summary>G1 checkpoint prepare/export surface.</summary>
    public const string Checkpoint = "checkpoint";

    /// <summary>Same-host PTY handoff (Unix hypa-pty-host only).</summary>
    public const string Handoff = "handoff";

    /// <summary>F1 workspace tabs / layout / occupant start.</summary>
    public const string Layout = "layout";

    /// <summary>Core <c>notification.show</c>. Plugin sources use grant <c>notification.request</c>.</summary>
    public const string Notification = "notification";

    /// <summary>Attach endpoint handshake and surface-interest controls.</summary>
    public const string AttachEndpoint = "attach_endpoint";

    /// <summary>Stable ordered capability set.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Core,
        Events,
        Leases,
        Terminal,
        Binding,
        AgentWait,
        Checkpoint,
        Handoff,
        Layout,
        Notification,
        AttachEndpoint,
    ];
}
