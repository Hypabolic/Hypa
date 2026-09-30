namespace Hypa.AgentRuntime.Application;

/// <summary>No-op sink. Used when the filter is off or a caller has no file.</summary>
public sealed class NullProcessLogSink : IProcessLogSink
{
    public static NullProcessLogSink Instance { get; } = new();

    public bool IsEnabled(ProcessLogLevel level) => false;

    public bool Disabled => false;

    public string? Path => null;

    public void Write(ProcessLogRecord record)
    {
    }
}
