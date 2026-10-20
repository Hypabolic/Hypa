using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
/// Open a JSON Lines file sink from <c>HYPA_LOG_LEVEL</c>.
/// <c>off</c> returns <see cref="NullProcessLogSink"/>.
/// Any other level returns a <see cref="QueuedProcessLogSink"/> in front of a
/// <see cref="JsonLinesFileLogSink"/>, so that no product thread waits for a
/// disk write. The owner disposes the sink to drain the queue.
/// </summary>
public static class ProcessLogSinkFactory
{
    public static IProcessLogSink Open(
        string path,
        ProcessLogLevel? filter = null,
        long? maxBytes = null,
        int? retainFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var level = filter ?? ProcessLogFilter.FromEnvironment();
        if (level == ProcessLogLevel.Off)
            return NullProcessLogSink.Instance;

        var file = new JsonLinesFileLogSink(
            path,
            level,
            maxBytes ?? ProcessLogPaths.DefaultMaxBytes,
            retainFiles ?? ProcessLogPaths.DefaultRetainFiles);
        return new QueuedProcessLogSink(file, exitSignal: new AppDomainProcessExitSignal());
    }

    public static IProcessLogSink OpenMux(string socketPath, string? explicitPath)
    {
        var path = ProcessLogPaths.ResolveMuxPath(socketPath, explicitPath);
        return Open(path);
    }

    public static IProcessLogSink OpenAttach(string socketPath, int pid, bool remote)
    {
        string path;
        if (remote)
        {
            var dir = ProcessLogPaths.TryUserLogDirectory();
            if (string.IsNullOrEmpty(dir))
                return NullProcessLogSink.Instance;
            path = Path.Combine(dir, ProcessLogPaths.AttachFileName(pid));
        }
        else
        {
            path = ProcessLogPaths.ResolveAttachPath(socketPath, pid);
        }

        return Open(path);
    }
}
