using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;

namespace Hypa.App;

internal static class MuxBootstrap
{
    public static async Task<string> EnsureReadyAsync(
        string session,
        string cwd,
        string? socketOverride,
        CancellationToken ct)
    {
        var socketPath = socketOverride ?? UnixSocketServer.ResolveSocketPath(session);
        if (await TryPingAsync(socketPath, ct).ConfigureAwait(false))
            return socketPath;

        var bin = ResolveHypaBin();
        var logDir = Path.GetDirectoryName(socketPath);
        // 0700 via the server's guard. A umask-mode parent fails the server's
        // socket guard and the mux.log sink, so the failure leaves no log.
        if (!string.IsNullOrEmpty(logDir))
            new UnixSocketOwnerGuard().EnsurePrivateDirectory(logDir);
        var logPath = Path.Combine(logDir ?? ".", "mux.log");

        var psi = new ProcessStartInfo
        {
            FileName = bin,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("mux");
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--session");
        psi.ArgumentList.Add(session);
        psi.ArgumentList.Add("--cwd");
        psi.ArgumentList.Add(cwd);
        psi.Environment["HYPA_RUNTIME_SOCKET"] = socketPath;
        psi.Environment["HYPA_MUX_DAEMONIZE"] = "1";
        psi.Environment["HYPA_MUX_LOG"] = logPath;

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start mux: {bin}");
        try
        {
            child.StandardInput.Close();
        }
        catch
        {
            // ignore
        }

        await WaitForReadyAsync(socketPath, logPath, bin, ct, child: child).ConfigureAwait(false);
        return socketPath;
    }

    internal static async Task WaitForReadyAsync(
        string socketPath,
        string logPath,
        string bin,
        CancellationToken ct,
        TimeSpan? readyTimeout = null,
        TimeProvider? time = null,
        Process? child = null)
    {
        var clock = time ?? TimeProvider.System;
        var deadline = clock.GetUtcNow() + (readyTimeout ?? MuxReadyBudget.Timeout);
        while (clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryPingAsync(socketPath, ct).ConfigureAwait(false))
                return;

            // A ping cancelled by ct returns not-ready. Report the cancel, not a failed start.
            ct.ThrowIfCancellationRequested();
            if (child is { HasExited: true })
                break;

            await Task.Delay(MuxReadyBudget.PollInterval, clock, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Mux did not become ready at {socketPath}. See {logPath}. bin={bin}");
    }

    private static async Task<bool> TryPingAsync(string socketPath, CancellationToken ct)
    {
        try
        {
            if (!Path.Exists(socketPath) && !File.Exists(socketPath))
                return false;
            await using var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var result = await client.CallAsync("ping", ct: ct).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True
                && result.TryGetProperty("protocol", out var proto)
                && proto.TryGetInt32(out var version)
                && version == 1;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveHypaBin()
    {
        foreach (var key in new[] { "HYPA_MUX_BIN", "HYPA_MUX_SERVER" })
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;
        }

        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDir))
        {
            var sibling = Path.Combine(processDir, "hypa");
            if (File.Exists(sibling))
                return sibling;
            var sidecar = Path.Combine(processDir, "sidecar", "hypa");
            if (File.Exists(sidecar))
                return sidecar;
        }

        if (File.Exists("/tmp/hypa-cli/hypa"))
            return "/tmp/hypa-cli/hypa";

        return "hypa";
    }
}
