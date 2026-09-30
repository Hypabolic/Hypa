namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Periodic hub drop record. Diagnostic only. Do not hold a pane gate or a
/// paint gate. Do not block the producer.
/// </summary>
public sealed class EventQueueLogProbe
{
    private readonly IProcessLogSink _sink;
    private readonly string? _sessionId;
    private long _lastDrops = -1;

    public EventQueueLogProbe(IProcessLogSink sink, string? sessionId = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sessionId = sessionId;
    }

    public void WriteIfChanged(EventHubQueueSnapshot snapshot)
    {
        if (!_sink.IsEnabled(ProcessLogLevel.Information))
            return;
        if (snapshot.OutputDrops == _lastDrops && _lastDrops >= 0)
            return;

        _lastDrops = snapshot.OutputDrops;
        _sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.EventsQueue,
            Subsystem = ProcessLogEvents.SubsystemEvents,
            Outcome = ProcessLogEvents.OutcomeOk,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Information,
            SessionId = _sessionId,
            DropCount = snapshot.OutputDrops,
            QueueDepth = snapshot.LiveItemCount,
        });
    }
}
