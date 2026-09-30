using System.Diagnostics;
using System.Text;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// finish on a worker. Does not wait on the caller.
/// </summary>
public sealed class ProcessPluginLauncher : IPluginProcessLauncher
{
    private readonly IPluginProcessRegistry? _registry;

    public ProcessPluginLauncher(IPluginProcessRegistry? registry = null) => _registry = registry;

    public bool TryStart(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int outputCapBytes,
        Action<PluginProcessExit> onExit,
        out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(onExit);

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
            error = ex.Message;
            return false;
        }

        _ = Task.Run(() => Run(info, outputCapBytes, onExit, _registry));
        return true;
    }

    private static void Run(
        ProcessStartInfo info,
        int cap,
        Action<PluginProcessExit> onExit,
        IPluginProcessRegistry? registry)
    {
        Process? process = null;
        var registeredPid = 0;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                onExit(new PluginProcessExit(null, "", "", "failed to start plugin command"));
                return;
            }

            registeredPid = process.Id;
            registry?.Register(registeredPid);

            var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, cap);
            var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, cap);
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            onExit(new PluginProcessExit(process.ExitCode, stdout, stderr, null, registeredPid));
        }
        catch (Exception ex)
        {
            onExit(new PluginProcessExit(null, "", "", ex.Message, registeredPid > 0 ? registeredPid : null));
        }
        finally
        {
            if (registeredPid > 0)
                registry?.Unregister(registeredPid);
            try
            {
                process?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static async Task<string> ReadCappedAsync(Stream stream, int cap)
    {
        var kept = new MemoryStream(Math.Min(cap, 8192));
        var buf = new byte[8192];
        var truncated = false;
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), CancellationToken.None)
                    .ConfigureAwait(false);
                if (n == 0)
                    break;
                var remaining = cap - (int)kept.Length;
                if (remaining > 0)
                    kept.Write(buf, 0, Math.Min(n, remaining));
                if (n > remaining)
                    truncated = true;
            }
        }
        catch (IOException)
        {
        }

        var text = Encoding.UTF8.GetString(kept.ToArray());
        if (truncated)
            text += $"\n[hypa truncated plugin output after {cap} bytes]";
        return text;
    }
}
