using System.Diagnostics;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Runs a pull-refresh command with one timeout and one output cap.
/// A timeout or crash does not throw to the mux caller.
/// </summary>
public sealed class ProcessPluginRefreshRunner : IPluginRefreshRunner
{
    private readonly IPluginProcessRegistry? _registry;

    public ProcessPluginRefreshRunner(IPluginProcessRegistry? registry = null) =>
        _registry = registry;

    public PluginProcessExit Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int outputCapBytes,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        ProcessStartInfo info;
        try
        {
            info = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments)
                info.ArgumentList.Add(arg);
            foreach (var kv in environment)
                info.Environment[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new PluginProcessExit(null, "", "", ex.Message);
        }

        Process? process = null;
        var registeredPid = 0;
        try
        {
            process = Process.Start(info);
            if (process is null)
                return new PluginProcessExit(null, "", "", "failed to start plugin command");

            registeredPid = process.Id;
            _registry?.Register(registeredPid);

            var stdoutTask = ProcessPluginLauncher.ReadCappedAsync(process.StandardOutput.BaseStream, outputCapBytes);
            var stderrTask = ProcessPluginLauncher.ReadCappedAsync(process.StandardError.BaseStream, outputCapBytes);
            var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            var timedOut = !process.WaitForExit(waitMs);
            if (timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                process.WaitForExit();
                return new PluginProcessExit(
                    process.ExitCode,
                    stdoutTask.GetAwaiter().GetResult(),
                    stderrTask.GetAwaiter().GetResult(),
                    "refresh timed out");
            }

            return new PluginProcessExit(
                process.ExitCode,
                stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult(),
                null);
        }
        catch (Exception ex)
        {
            return new PluginProcessExit(null, "", "", ex.Message);
        }
        finally
        {
            if (registeredPid > 0)
                _registry?.Unregister(registeredPid);
            try
            {
                process?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
