namespace Hypa.AgentRuntime.Application.Attach.Status;

/// <summary>
/// How a status entry is shown. An error is shown with the error chip. A
/// notice is a neutral line. An error wins over a notice.
/// </summary>
public enum StatusSeverity
{
    /// <summary>A neutral line with no error chip.</summary>
    Notice,

    /// <summary>A failure with the error chip.</summary>
    Error,
}
