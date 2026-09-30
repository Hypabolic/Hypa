using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Event class for journal framing and subscribe filters (design §10.2).
/// Wire filter tokens are the lowercase names; binary framing uses the stable uint8 map.
/// </summary>
public enum EventClass : byte
{
    Control = 1,
    Lifecycle = 2,
    Output = 3,
    Render = 4,
}

/// <summary>Helpers for <see cref="EventClass"/> wire and filter mapping.</summary>
public static class EventClassMap
{
    public const string Control = "control";
    public const string Lifecycle = "lifecycle";
    public const string Output = "output";
    public const string Render = "render";

    public static string ToWire(EventClass c) => c switch
    {
        EventClass.Control => Control,
        EventClass.Lifecycle => Lifecycle,
        EventClass.Output => Output,
        EventClass.Render => Render,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown event class"),
    };

    public static bool TryParse(string? token, out EventClass c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        switch (token.Trim().ToLowerInvariant())
        {
            case Control:
                c = EventClass.Control;
                return true;
            case Lifecycle:
                c = EventClass.Lifecycle;
                return true;
            case Output:
                c = EventClass.Output;
                return true;
            case Render:
                c = EventClass.Render;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Map a known wire type. Unknown names fail closed. Explicit
    /// <c>checkpoint.lifecycle</c> is Control and wins before the
    /// <c>.lifecycle</c> suffix.
    /// </summary>
    public static bool TryFromWireType(string eventType, out EventClass c)
    {
        c = default;
        if (string.IsNullOrEmpty(eventType))
            return false;

        if (eventType.Equals(ProtocolEventTypes.CheckpointLifecycle, StringComparison.Ordinal))
        {
            c = EventClass.Control;
            return true;
        }

        if (eventType.Equals(ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
        {
            c = EventClass.Render;
            return true;
        }

        if (eventType.Equals(ProtocolEventTypes.TerminalOutput, StringComparison.Ordinal))
        {
            c = EventClass.Output;
            return true;
        }

        if (eventType is ProtocolEventTypes.PaneBell
            or ProtocolEventTypes.LeaseChanged
            or ProtocolEventTypes.BindingChanged
            or ProtocolEventTypes.ExportAcked
            or ProtocolEventTypes.NotificationShown
            or ProtocolEventTypes.ClientWindowTitleChanged
            or ProtocolEventTypes.ConfigReloaded
            or ProtocolEventTypes.PaneInputRejected
            or AttachEndpointProtocol.WelcomeEvent
            or AttachEndpointProtocol.ProjectionSnapshotEvent
            or AttachEndpointProtocol.PresentationSyncEvent
            or AttachEndpointProtocol.PresentationReadyEvent)
        {
            c = EventClass.Control;
            return true;
        }

        if (eventType is ProtocolEventTypes.SessionLifecycle
            or ProtocolEventTypes.WorkspaceLifecycle
            or ProtocolEventTypes.PaneLifecycle
            or ProtocolEventTypes.OccupantLifecycle
            or ProtocolEventTypes.TabLifecycle
            or ProtocolEventTypes.PopupLifecycle
            or ProtocolEventTypes.SettingsChanged
            or ProtocolEventTypes.OverlayLifecycle
            or ProtocolEventTypes.PaneAgentStatusChanged
            or ProtocolEventTypes.PaneScrollChanged
            or ProtocolEventTypes.LayoutUpdated
            or ProtocolEventTypes.PanePlacementChanged
            or ProtocolEventTypes.PaneOutputMatched
            or ProtocolEventTypes.WorkspaceMetadataUpdated
            or ProtocolEventTypes.PaneMetadataUpdated
            or ProtocolEventTypes.ResourceChanged
            or ProtocolEventTypes.ConfigChanged
            or ProtocolEventTypes.WorktreeCreated
            or ProtocolEventTypes.WorktreeOpened
            or ProtocolEventTypes.WorktreeRemoved
            or "worktree.created"
            or "worktree.opened"
            or "worktree.removed")
        {
            c = EventClass.Lifecycle;
            return true;
        }

        return false;
    }

    public static EventClass FromWireType(string eventType)
    {
        if (TryFromWireType(eventType, out var mapped))
            return mapped;
        throw new ArgumentException(
            "Unmapped runtime event type: " + eventType,
            nameof(eventType));
    }

    public static bool IsKnownWireToken(string token) =>
        TryParse(token, out _) || TryFromWireType(token, out _);
}
