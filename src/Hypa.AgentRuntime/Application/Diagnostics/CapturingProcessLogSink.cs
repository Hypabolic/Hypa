using System.Collections.Concurrent;

namespace Hypa.AgentRuntime.Application;

/// <summary>In-process capture for tests. Never writes a file.</summary>
public sealed class CapturingProcessLogSink : IProcessLogSink
{
    private readonly ConcurrentQueue<ProcessLogRecord> _records = new();
    private readonly ProcessLogLevel _filter;

    public CapturingProcessLogSink(ProcessLogLevel filter = ProcessLogLevel.Information)
    {
        _filter = filter;
    }

    public bool Disabled { get; set; }

    public string? Path { get; init; }

    public IReadOnlyList<ProcessLogRecord> Records => _records.ToArray();

    public bool IsEnabled(ProcessLogLevel level) =>
        !Disabled && ProcessLogFilter.Allows(_filter, level);

    public void Write(ProcessLogRecord record)
    {
        if (!IsEnabled(record.Level))
            return;
        _records.Enqueue(record);
    }
}
