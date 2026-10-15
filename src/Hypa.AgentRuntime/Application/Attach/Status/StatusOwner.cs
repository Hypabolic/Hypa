namespace Hypa.AgentRuntime.Application.Attach.Status;

/// <summary>
/// The feature area that owns one attach status entry. An owner has at most
/// one entry. Only the owner clears its entry.
/// </summary>
public enum StatusOwner
{
    /// <summary>An activation, a handoff, a cube connect, or the local attach surface.</summary>
    Connection,

    /// <summary>The endpoint connection supervisor that redials a lost cube.</summary>
    Reconnect,

    /// <summary>The input stall and the input sender faults of the attach transport.</summary>
    Transport,

    /// <summary>A generic command, control, or key dispatch failure.</summary>
    Command,

    /// <summary>A chrome refresh, a compose, or a pane resize failure.</summary>
    Chrome,

    /// <summary>The host terminal theme decode and the theme RPC.</summary>
    Theme,

    /// <summary>The configuration reload, the settings, the integrations, and the plugin settings.</summary>
    Settings,

    /// <summary>The move of work between placements.</summary>
    MoveWork,

    /// <summary>The cube pairing and the cube device revoke.</summary>
    Pairing,

    /// <summary>The worktree dialog and the worktree actions.</summary>
    Worktrees,

    /// <summary>The overlay, the popup, and the command overlay restore.</summary>
    Overlay,

    /// <summary>The semantic action of a pane.</summary>
    Semantic,

    /// <summary>A request that the attach cannot start while another dialog or picker is open.</summary>
    UiBusy,
}
