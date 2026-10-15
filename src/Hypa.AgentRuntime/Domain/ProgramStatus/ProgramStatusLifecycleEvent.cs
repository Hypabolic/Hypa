namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// Event the terminal already sees that ends the life of some records.
/// See <see cref="ProgramStatusStore.On"/> for the effect of each event.
/// </summary>
public enum ProgramStatusLifecycleEvent
{
    /// <summary>The process attached to the pane exited.</summary>
    ProcessExit,

    /// <summary>A new shell prompt began (OSC 133 A).</summary>
    PromptStart,

    /// <summary>A full reset (RIS) happened.</summary>
    FullReset,
}
