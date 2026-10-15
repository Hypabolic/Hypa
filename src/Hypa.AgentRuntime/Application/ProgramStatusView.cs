using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Where a status comes from: the workspace, tab, and pane labels.
/// The labels are safe display text.
/// </summary>
public sealed record ProgramStatusSourceLabel(string Workspace, string Tab, string Pane);

/// <summary>
/// What any frontend shows for the program status of one pane.
/// The mux builds it with <see cref="ProgramStatusViewProjection"/>.
/// Every text field is safe display text. The raw text stays in the record list.
/// </summary>
public sealed record ProgramStatusView
{
    /// <summary>State of the most urgent record.</summary>
    public required ProgramStatusState State { get; init; }

    public ProgramStatusKind? Kind { get; init; }

    public int? Progress { get; init; }

    /// <summary>Resolved <c>app</c> of the most urgent record.</summary>
    public string? App { get; init; }

    public string? Title { get; init; }

    public string? Msg { get; init; }

    /// <summary>Number of records the pane holds.</summary>
    public required int RecordCount { get; init; }

    public required ProgramStatusSourceLabel Source { get; init; }

    /// <summary>
    /// True when the state is done or error and the person has seen the pane (the one seen state of the pane).
    /// A frontend can show the row dimmed. The records do not change. It is false for the other states.
    /// </summary>
    public bool Seen { get; init; }

    /// <summary>
    /// True when the tab view and the session view count this pane. A seen done or error does not count.
    /// A blocked or working pane always counts.
    /// </summary>
    public bool CountsForSummary => !(Seen && State is ProgramStatusState.Done or ProgramStatusState.Error);

    /// <summary>
    /// When the program status of the pane last changed, by the clock of the mux.
    /// The projections of the tab and the session use it to break a tie of urgency.
    /// The mux does not publish it. Null when unknown, for example after a restore.
    /// </summary>
    public DateTimeOffset? ChangedAt { get; init; }
}

/// <summary>What a frontend shows for a tab: the most urgent state of its panes.</summary>
public sealed record TabProgramStatusView
{
    public required string TabId { get; init; }

    public required ProgramStatusState State { get; init; }

    /// <summary>Progress of the top pane. Set only when <see cref="State"/> is working.</summary>
    public int? Progress { get; init; }
}

/// <summary>
/// What a frontend shows for the whole session: the most urgent pane.
/// A frontend can pass it to an outer program, such as the host terminal of an attach.
/// </summary>
public sealed record SessionProgramStatusView
{
    public required ProgramStatusState State { get; init; }

    /// <summary>Set only when <see cref="State"/> is blocked.</summary>
    public ProgramStatusKind? Kind { get; init; }

    /// <summary>Set only when <see cref="State"/> is working.</summary>
    public int? Progress { get; init; }

    /// <summary>Resolved <c>app</c> of the top pane.</summary>
    public string? App { get; init; }

    public required string PaneId { get; init; }

    /// <summary>
    /// Safe display text that names the pane: <c>pane: msg</c>, or the pane label alone
    /// when the pane has no message. The text is short.
    /// </summary>
    public required string Msg { get; init; }

    public required ProgramStatusSourceLabel Source { get; init; }
}
