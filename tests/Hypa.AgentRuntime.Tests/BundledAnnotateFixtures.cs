using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Tests;

internal static class BundledAnnotateFixtures
{
    public static string WriteHypaPair(
        string directory,
        string? annotateContents = null)
    {
        Directory.CreateDirectory(directory);
        var hypa = Path.Combine(directory, "hypa");
        File.WriteAllText(hypa, string.Empty);
        var sibling = Path.Combine(directory, BundledPluginLayout.AnnotateProductFileName);
        File.WriteAllText(sibling, annotateContents ?? "#!/bin/sh\nexit 0\n");
        TryUnixExecute(sibling);
        return hypa;
    }

    public static string WriteNotifyRecorder(string directory) =>
        WriteArgvRecorder(directory, "notify.args");

    public static string WriteArgvRecorder(string directory, string logFileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "hypa");
        var logName = logFileName.Replace("\"", "\\\"", StringComparison.Ordinal);
        File.WriteAllText(path, $$"""
            #!/bin/sh
            log="$(dirname "$0")/{{logName}}"
            : > "$log"
            for arg in "$@"; do
              printf '%s\n' "$arg" >> "$log"
            done
            exit 0
            """);
        TryUnixExecute(path);
        return path;
    }

    public static void TryUnixExecute(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static PluginCommandLog WaitFinished(
        IPluginHost host,
        string pluginId,
        string actionId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        PluginCommandLog? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var logs = host.ListLogs(pluginId, 32);
            if (logs.IsOk)
            {
                last = logs.Value.FirstOrDefault(log =>
                    string.Equals(log.ActionId, actionId, StringComparison.Ordinal));
                if (last is { FinishedUnixMs: not null })
                    return last;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            "plugin log did not finish: " + (last?.Status ?? "missing"));
    }
}
