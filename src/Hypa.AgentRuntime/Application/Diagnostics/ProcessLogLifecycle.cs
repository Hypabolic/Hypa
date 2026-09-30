namespace Hypa.AgentRuntime.Application;

/// <summary>
/// </summary>
public static class ProcessLogLifecycle
{
    public static void Startup(IProcessLogSink sink, string subsystem, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(ProcessLogLevel.Information))
            return;
        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.AppStartup,
            Subsystem = subsystem,
            Outcome = ProcessLogEvents.OutcomeStarted,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Information,
            SessionId = sessionId,
        });
    }

    public static void Shutdown(IProcessLogSink sink, string subsystem, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(ProcessLogLevel.Information))
            return;
        sink.Write(new ProcessLogRecord
        {
            Event = ProcessLogEvents.AppShutdown,
            Subsystem = subsystem,
            Outcome = ProcessLogEvents.OutcomeCompleted,
            Ts = DateTimeOffset.UtcNow,
            Pid = Environment.ProcessId,
            Level = ProcessLogLevel.Information,
            SessionId = sessionId,
        });
    }
}
