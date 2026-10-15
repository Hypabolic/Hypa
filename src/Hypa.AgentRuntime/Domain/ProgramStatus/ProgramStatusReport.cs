namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>
/// One validated OSC 7501 report. Every field is already checked.
/// <see cref="Kind"/> and <see cref="Progress"/> are null when they do not apply to <see cref="State"/>.
/// </summary>
public sealed record ProgramStatusReport
{
    /// <summary>Record id. Null addresses the root record.</summary>
    public string? Id { get; init; }

    public required ProgramStatusState State { get; init; }

    public ProgramStatusKind? Kind { get; init; }

    public int? Progress { get; init; }

    public string? App { get; init; }

    public string? Title { get; init; }

    public string? Msg { get; init; }
}

/// <summary>Why the parser did not accept a report.</summary>
public enum ProgramStatusRejection
{
    SequenceTooLong,
    KeyTooLong,
    MsgTooLong,
    TitleTooLong,
    InvalidBase64,
    InvalidText,
    ControlCharacter,
    MissingState,
    UnknownState,
    InvalidId,
}

/// <summary>One stored record of a pane.</summary>
public sealed record ProgramStatusRecord
{
    /// <summary>Record id. Null is the root record.</summary>
    public string? Id { get; init; }

    public required ProgramStatusState State { get; init; }

    public ProgramStatusKind? Kind { get; init; }

    public int? Progress { get; init; }

    public string? App { get; init; }

    public string? Title { get; init; }

    public string? Msg { get; init; }

    /// <summary>Per-pane sequence number of the report that wrote this record.</summary>
    public long UpdatedSeq { get; init; }

    public bool SameContent(ProgramStatusRecord other) =>
        Id == other.Id
        && State == other.State
        && Kind == other.Kind
        && Progress == other.Progress
        && App == other.App
        && Title == other.Title
        && Msg == other.Msg;
}
