using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>
/// Stops a mux by <c>server.stop</c>, falling back to SIGTERM on a verified
/// mux pid. Shared by <c>hypa mux stop</c> and the attach restart prompt.
/// </summary>
internal static class MuxServerStopper
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopPoll = TimeSpan.FromMilliseconds(100);

    internal static async Task<int> StopAsync(
        string session,
        string socketPath,
        TextWriter output,
        TextWriter error)
    {
        var statusPath = Path.Combine(Path.GetDirectoryName(socketPath) ?? ".", "runtime.status.json");
        var ping = await MuxControlPlane.TryPingAsync(socketPath, CancellationToken.None).ConfigureAwait(false);
        var status = await TryReadStatusAsync(statusPath).ConfigureAwait(false);

        if (status is null || status.Pid <= 0)
        {
            if (ping is null)
            {
                await error.WriteLineAsync($"No mux server status for session '{session}'.")
                    .ConfigureAwait(false);
                return 1;
            }

            await error.WriteLineAsync(
                    $"Mux is live at {socketPath} but {statusPath} is missing or invalid. Refuse untargeted stop.")
                .ConfigureAwait(false);
            return 1;
        }

        if (ping is null)
        {
            await error.WriteLineAsync(
                    $"No live mux at {socketPath}. Status pid {status.Pid} left untouched.")
                .ConfigureAwait(false);
            return 1;
        }

        if (await TryServerStopRpcAsync(socketPath).ConfigureAwait(false)
            && await WaitUntilStoppedAsync(socketPath, status.Pid).ConfigureAwait(false))
        {
            output.WriteLine($"Stopped mux session={session} pid={status.Pid}");
            return 0;
        }

        if (!MuxProcessIdentity.TryAcquireMuxServer(status.Pid, out var lease) || lease is null)
        {
            await error.WriteLineAsync(
                    $"Status pid {status.Pid} is not a live hypa mux (dead or recycled). Not sending SIGTERM.")
                .ConfigureAwait(false);
            return 1;
        }

        using (lease)
        {
            if (!lease.TryTerminate(out var terminateError))
            {
                await error.WriteLineAsync(
                        string.IsNullOrWhiteSpace(terminateError)
                            ? $"Could not stop mux pid {status.Pid}."
                            : terminateError)
                    .ConfigureAwait(false);
                return 1;
            }
        }

        if (!await WaitUntilStoppedAsync(socketPath, status.Pid).ConfigureAwait(false))
        {
            await error.WriteLineAsync(
                    $"Mux session={session} pid={status.Pid} still running after SIGTERM.")
                .ConfigureAwait(false);
            return 1;
        }

        output.WriteLine($"Stopped mux session={session} pid={status.Pid}");
        return 0;
    
    }

    private static async Task<bool> TryServerStopRpcAsync(string socketPath)
    {
        try
        {
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync().ConfigureAwait(false);
            var result = await client.CallAsync(ProtocolMethods.ServerStop).ConfigureAwait(false);
            return result.ValueKind != System.Text.Json.JsonValueKind.Object
                || !result.TryGetProperty("ok", out var ok)
                || ok.ValueKind != System.Text.Json.JsonValueKind.False;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<RuntimeStatusFile?> TryReadStatusAsync(string statusPath)
    {
        if (!File.Exists(statusPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(statusPath);
            return await JsonSerializer.DeserializeAsync(stream, MuxJsonContext.Default.RuntimeStatusFile)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> WaitUntilStoppedAsync(string socketPath, int pid)
    {
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var ping = await MuxControlPlane.TryPingAsync(socketPath, CancellationToken.None)
                .ConfigureAwait(false);
            var socketGone = OperatingSystem.IsWindows()
                || (!File.Exists(socketPath) && !Directory.Exists(socketPath));
            var dead = !MuxProcessIdentity.IsAlive(pid);
            if (ping is null && (socketGone || dead))
                return true;

            await Task.Delay(StopPoll).ConfigureAwait(false);
        }

        var still = await MuxControlPlane.TryPingAsync(socketPath, CancellationToken.None)
            .ConfigureAwait(false);
        return still is null && !MuxProcessIdentity.IsAlive(pid);
    }
}
