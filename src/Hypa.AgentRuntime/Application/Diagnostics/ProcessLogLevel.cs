namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Process log filter levels. Filter env is <c>HYPA_LOG_LEVEL</c>.
/// Default is <see cref="Information"/>. <see cref="Off"/> disables the file.
/// </summary>
public enum ProcessLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Off = 3,
}
