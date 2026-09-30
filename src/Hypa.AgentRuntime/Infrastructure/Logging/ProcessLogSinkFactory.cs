using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
/// Open a JSON Lines file sink from <c>HYPA_LOG_LEVEL</c>.
/// <c>off</c> returns <see cref="NullProcessLogSink"/>.
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

        return new JsonLinesFileLogSink(
            path,
            level,
            maxBytes ?? ProcessLogPaths.DefaultMaxBytes,
            retainFiles ?? ProcessLogPaths.DefaultRetainFiles);
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
