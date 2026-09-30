using System.Diagnostics;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Spawn <c>HYPA_BIN_PATH</c> as argv.
/// </summary>
internal static class HypaBinCli
{
    public static Result<HypaBinRunResult, string> Run(
        IReadOnlyList<string> arguments,
        int timeoutMs = 30_000)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var bin = Environment.GetEnvironmentVariable(AnnotateEnv.BinPath);
        if (string.IsNullOrWhiteSpace(bin))
            return Result<HypaBinRunResult, string>.Fail(AnnotateEnv.BinPath + " is not set");

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return Result<HypaBinRunResult, string>.Fail("failed to start " + bin);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }

                return Result<HypaBinRunResult, string>.Fail("timed out waiting for " + bin);
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (string.IsNullOrWhiteSpace(message))
                    message = bin + " exited " + process.ExitCode;
                return Result<HypaBinRunResult, string>.Fail(message.Trim());
            }

            return Result<HypaBinRunResult, string>.Ok(new HypaBinRunResult(process.ExitCode, stdout, stderr));
        }
        catch (Exception ex) when (
            ex is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return Result<HypaBinRunResult, string>.Fail(ex.Message);
        }
    }
}

internal sealed record HypaBinRunResult(int ExitCode, string Stdout, string Stderr);
