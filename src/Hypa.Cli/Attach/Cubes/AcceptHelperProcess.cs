using System.Diagnostics;
using System.Text.Json;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;

namespace Hypa.Cli.Attach.Cubes;

/// <summary>
/// Child accept helper with readable stdout. Detached spawn is not this path.
/// </summary>
public sealed class AcceptHelperProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _watchCts = new();
    private readonly object _pairedGate = new();
    private readonly Task _watchTask;
    private int _pairedPending;
    private string? _pairedDeviceId;

    private AcceptHelperProcess(Process process, ConnectivityAcceptListenDocument listen)
    {
        _process = process;
        Listen = listen;
        _watchTask = WatchPairedAsync(_watchCts.Token);
    }

    public ConnectivityAcceptListenDocument Listen { get; }

    public static async Task<(AcceptHelperProcess? Helper, string? Error)> StartAsync(
        string session,
        string bindHost,
        int port,
        IReadOnlyList<string> advertiseHosts,
        CancellationToken cancellationToken)
    {
        var file = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(file))
            return (null, "hypa binary path is missing");
        if (advertiseHosts is not { Count: > 0 })
            return (null, "no reachable address");

        var start = CreateStartInfo(file, session, bindHost, port, advertiseHosts);

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("accept helper did not start");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, ex.Message);
        }

        try
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                var err = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                process.Kill(entireProcessTree: true);
                process.Dispose();
                return (null, FormatStartError(err, port));
            }

            var document = JsonSerializer.Deserialize(
                line,
                ConnectivityJsonContext.Default.ConnectivityAcceptListenDocument);
            if (document is null || !document.Ok || string.IsNullOrWhiteSpace(document.Invite))
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                return (null, document?.QuicDetail ?? "accept helper did not issue an invite");
            }

            return (new AcceptHelperProcess(process, document), null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            process.Dispose();
            return (null, "accept helper start timed out");
        }
        catch (JsonException)
        {
            TryKill(process);
            process.Dispose();
            return (null, "accept helper listen json is invalid");
        }
    }

    public bool TryTakePaired(out string? deviceId)
    {
        if (Interlocked.Exchange(ref _pairedPending, 0) == 0)
        {
            deviceId = null;
            return false;
        }

        lock (_pairedGate)
            deviceId = _pairedDeviceId;
        return true;
    }

    internal void OfferPaired(string? deviceId)
    {
        lock (_pairedGate)
            _pairedDeviceId = deviceId;
        Interlocked.Exchange(ref _pairedPending, 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _watchCts.CancelAsync().ConfigureAwait(false);
        TryKill(_process);
        try
        {
            await _watchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _watchCts.Dispose();
    }

    private async Task WatchPairedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                    return;
                if (!ConnectivityAcceptPairedDocument.TryRead(line, out var document))
                    continue;
                OfferPaired(document.DeviceId);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// at <c>/dev/null</c>. Do not inherit the attach TTY. A child that
    /// shares fd 0 can SIGHUP <c>docker exec -it</c> when it exits.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(
        string file,
        string session,
        string bindHost,
        int port,
        IReadOnlyList<string> advertiseHosts)
    {
        ArgumentNullException.ThrowIfNull(advertiseHosts);
        // The packaged UI runs in hypa-attach; connectivity commands live in hypa.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(file),
                MuxInvocation.LeanAttachFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            file = Path.Combine(
                Path.GetDirectoryName(file) ?? "",
                MuxInvocation.ProductFileName + Path.GetExtension(file));
        }

        var start = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("connectivity");
        start.ArgumentList.Add("accept");
        start.ArgumentList.Add("--session");
        start.ArgumentList.Add(session);
        start.ArgumentList.Add("--bind");
        start.ArgumentList.Add(bindHost);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString());
        foreach (var host in advertiseHosts)
        {
            start.ArgumentList.Add("--advertise-host");
            start.ArgumentList.Add(host);
        }

        start.ArgumentList.Add("--json");
        return start;
    }

    internal static string FormatStartError(string? raw, int port)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "accept helper wrote no listen json";

        var text = raw.Trim();
        if (LooksLikeAddressInUse(text))
            return $"port {port} is already in use";

        var first = FirstUsefulLine(text);
        const string prefix = "Unhandled exception: ";
        if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            first = first[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(first) ? "accept helper did not start" : first;
    }

    internal static bool LooksLikeAddressInUse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return raw.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("SocketException (98)", StringComparison.Ordinal)
            || raw.Contains("SocketException (48)", StringComparison.Ordinal)
            || raw.Contains("port ", StringComparison.OrdinalIgnoreCase)
                && raw.Contains("already in use", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstUsefulLine(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return text.Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
