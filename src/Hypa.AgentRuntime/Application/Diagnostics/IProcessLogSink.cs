namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Process-log file sink. A sink error must not fail a product operation.
/// failure. Callers must not hold an emit gate or a pane gate during
/// <see cref="Write"/>.
/// </summary>
public interface IProcessLogSink
{
    bool IsEnabled(ProcessLogLevel level);

    bool Disabled { get; }

    string? Path { get; }

    void Write(ProcessLogRecord record);
}
