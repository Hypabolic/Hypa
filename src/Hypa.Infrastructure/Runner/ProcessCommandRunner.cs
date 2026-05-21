using System.Diagnostics;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Infrastructure.Runner;

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<Result<CommandOutput, Error>> RunAsync(CommandInvocation invocation, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            UseShellExecute = false,
            CreateNoWindow = invocation.Mode == ToolRunMode.Buffered,
        };

        foreach (var arg in invocation.Arguments)
            psi.ArgumentList.Add(arg);

        if (invocation.WorkingDirectory is not null)
            psi.WorkingDirectory = invocation.WorkingDirectory;

        foreach (var (key, value) in invocation.EnvOverrides)
            psi.Environment[key] = value;

        if (invocation.Mode == ToolRunMode.Buffered)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        using var timeoutCts = new CancellationTokenSource(invocation.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Result<CommandOutput, Error>.Fail(new Error("PROCESS_START_FAILED", ex.Message));
        }

        if (process is null)
            return Result<CommandOutput, Error>.Fail(new Error("PROCESS_NULL", "Process.Start returned null."));

        using (process)
        {
            var sw = Stopwatch.StartNew();

            if (invocation.Mode == ToolRunMode.Buffered)
            {
                // Read both streams concurrently to prevent deadlock on large output.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    await WaitForKilledProcessAsync(process);
                    var (timedOutStdout, timedOutStderr) = await DrainBufferedOutputAsync(stdoutTask, stderrTask);
                    return Result<CommandOutput, Error>.Ok(
                        CommandOutput.CreateTimedOut(sw.Elapsed, timedOutStdout, timedOutStderr));
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    return Result<CommandOutput, Error>.Fail(new Error("CANCELLED", "Operation was cancelled."));
                }

                string stdout;
                string stderr;
                try
                {
                    stdout = await stdoutTask;
                    stderr = await stderrTask;
                }
                catch (OperationCanceledException)
                {
                    return Result<CommandOutput, Error>.Fail(new Error("CANCELLED", "Operation was cancelled."));
                }

                return Result<CommandOutput, Error>.Ok(
                    CommandOutput.Captured(stdout, stderr, process.ExitCode, sw.Elapsed));
            }
            else
            {
                // Passthrough: streams go directly to terminal.
                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    return Result<CommandOutput, Error>.Ok(CommandOutput.CreateTimedOut(sw.Elapsed));
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    return Result<CommandOutput, Error>.Fail(new Error("CANCELLED", "Operation was cancelled."));
                }

                return Result<CommandOutput, Error>.Ok(
                    CommandOutput.Captured(string.Empty, string.Empty, process.ExitCode, sw.Elapsed));
            }
        }
    }

    private static async Task WaitForKilledProcessAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* best-effort */ }
    }

    private static async Task<(string Stdout, string Stderr)> DrainBufferedOutputAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        var stdout = await DrainAsync(stdoutTask);
        var stderr = await DrainAsync(stderrTask);
        return (stdout, stderr);
    }

    private static async Task<string> DrainAsync(Task<string> task)
    {
        try { return await task; } catch { return string.Empty; }
    }
}
