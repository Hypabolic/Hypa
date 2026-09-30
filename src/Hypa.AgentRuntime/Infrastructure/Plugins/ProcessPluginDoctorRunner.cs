using System.Diagnostics;
using System.Text;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

/// <summary>
/// Wait for one doctor argv. The host does not invoke a shell.
/// </summary>
public sealed class ProcessPluginDoctorRunner : IPluginDoctorRunner
{
    public PluginDoctorRunResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            foreach (var kv in environment)
                info.Environment[kv.Key] = kv.Value;

            using var process = Process.Start(info);
            if (process is null)
                return new PluginDoctorRunResult(null, "", "failed to start doctor command", TimedOut: false);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.AppendLine(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }

                return new PluginDoctorRunResult(null, stdout.ToString(), stderr.ToString(), TimedOut: true);
            }

            process.WaitForExit();
            return new PluginDoctorRunResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
        }
        catch (Exception ex) when (
            ex is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return new PluginDoctorRunResult(null, "", ex.Message, TimedOut: false);
        }
    }
}
