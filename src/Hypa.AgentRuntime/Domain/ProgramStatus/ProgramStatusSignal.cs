namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>What the pane output scanner saw that matters to program status.</summary>
public enum ProgramStatusSignalKind
{
    /// <summary>A valid report. <see cref="ProgramStatusSignal.Source"/> says where it came from.</summary>
    Report,

    /// <summary>The feature detection query <c>OSC 7501 ; ? ST</c>.</summary>
    Query,

    /// <summary>A full reset (RIS, <c>ESC c</c>).</summary>
    Reset,

    /// <summary>A new shell prompt began (OSC 133 A).</summary>
    PromptStart,
}

/// <summary>One scanner result, in output order.</summary>
public readonly record struct ProgramStatusSignal(
    ProgramStatusSignalKind Kind,
    ProgramStatusReport? Report = null,
    ProgramStatusReportSource Source = ProgramStatusReportSource.Osc7501)
{
    public static ProgramStatusSignal Query { get; } = new(ProgramStatusSignalKind.Query);

    public static ProgramStatusSignal Reset { get; } = new(ProgramStatusSignalKind.Reset);

    public static ProgramStatusSignal PromptStart { get; } = new(ProgramStatusSignalKind.PromptStart);

    /// <summary>
    /// A valid report. A real OSC 7501 report is the default source.
    /// A report that the terminal mapped from ConEmu progress has the source
    /// <see cref="ProgramStatusReportSource.ConEmuProgress"/>. The store may ignore it.
    /// </summary>
    public static ProgramStatusSignal FromReport(
        ProgramStatusReport report,
        ProgramStatusReportSource source = ProgramStatusReportSource.Osc7501) =>
        new(ProgramStatusSignalKind.Report, report, source);
}
