namespace Hypa.AgentRuntime.Domain.ProgramStatus;

/// <summary>Where a report came from. The store treats the sources in different ways.</summary>
public enum ProgramStatusReportSource
{
    /// <summary>A real OSC 7501 report.</summary>
    Osc7501,

    /// <summary>A report that the terminal mapped from ConEmu progress (OSC 9;4).</summary>
    ConEmuProgress,
}
