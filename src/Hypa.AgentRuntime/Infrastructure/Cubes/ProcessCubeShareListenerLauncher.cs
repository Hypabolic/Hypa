using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Cubes;

namespace Hypa.AgentRuntime.Infrastructure.Cubes;

/// <summary>
/// Runs <c>hypa connectivity accept</c> as a mux child. The listener stays a
/// separate process so a fault in the network stack cannot take down panes.
/// The mux holds the child's stdin open. The child exits when that pipe
/// closes, so it never outlives the mux, even after SIGKILL.
/// </summary>
public sealed class ProcessCubeShareListenerLauncher : ICubeShareListenerLauncher
{
    internal static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(10);

    private readonly string? _cliPath;
    private readonly string _session;
    private readonly string _socketPath;

    public ProcessCubeShareListenerLauncher(string? cliPath, string session, string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _cliPath = cliPath;
        _session = session;
        _socketPath = socketPath;
    }

    public async Task<Result<ICubeShareListener, string>> LaunchAsync(
        CubeShareSettings settings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(_cliPath))
            return Result<ICubeShareListener, string>.Fail("hypa binary was not found beside the mux");

        Process process;
        try
        {
            process = Process.Start(CreateStartInfo(_cliPath, _session, _socketPath, settings))
                ?? throw new InvalidOperationException("share listener did not start");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return Result<ICubeShareListener, string>.Fail(ex.Message);
        }

        var stderr = new StderrTail();
        var stderrPump = PumpStderrAsync(process, stderr);
        string? line;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ListenTimeout);
            line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process, stderrPump).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Result<ICubeShareListener, string>.Fail("share listener start timed out");
        }

        if (string.IsNullOrWhiteSpace(line) || !TryParseListen(line, out var listen))
        {
            await KillAsync(process, stderrPump).ConfigureAwait(false);
            return Result<ICubeShareListener, string>.Fail(
                FormatStartError(stderr.Last, settings.Port));
        }

        return Result<ICubeShareListener, string>.Ok(
            new ProcessCubeShareListener(process, listen, stderr, stderrPump));
    }

    internal static ProcessStartInfo CreateStartInfo(
        string cliPath,
        string session,
        string socketPath,
        CubeShareSettings settings)
    {
        var start = new ProcessStartInfo
        {
            FileName = cliPath,
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
        start.ArgumentList.Add("--socket");
        start.ArgumentList.Add(socketPath);
        start.ArgumentList.Add("--bind");
        start.ArgumentList.Add(settings.Bind);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--exit-on-stdin-eof");
        return start;
    }

    internal static bool TryParseListen(string line, out CubeShareListen listen)
    {
        listen = null!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("port", out var port)
                || !port.TryGetInt32(out var portValue))
            {
                return false;
            }

            listen = new CubeShareListen
            {
                Bind = ReadString(root, "bind") ?? "",
                Port = portValue,
                CertificateSha256 = ReadString(root, "certificate_sha256"),
                QuicListening = root.TryGetProperty("quic_listening", out var quic)
                    && quic.ValueKind == JsonValueKind.True,
                QuicDetail = ReadString(root, "quic_detail"),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string FormatStartError(string? stderr, int port)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "share listener exited before it was ready";
        var text = stderr.Trim();
        if (text.Contains("already in use", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return $"port {port} is already in use";
        }

        const string prefix = "Unhandled exception: ";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..].Trim()
            : text;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task PumpStderrAsync(Process process, StderrTail tail)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                tail.Offer(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task KillAsync(Process process, Task stderrPump)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderrPump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Last useful stderr line. QUIC fallback notes stay out of the exit detail.</summary>
    private sealed class StderrTail
    {
        private readonly object _gate = new();
        private string? _last;
        private string? _ignored;

        public string? Last
        {
            get
            {
                lock (_gate)
                    return _last;
            }
        }

        public void Offer(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (_gate)
            {
                if (!string.Equals(line.Trim(), _ignored, StringComparison.Ordinal))
                    _last = line;
            }
        }

        /// <summary>The listener writes its QUIC fallback reason to stderr once it is ready.</summary>
        public void Ignore(string? line)
        {
            lock (_gate)
            {
                _ignored = line?.Trim();
                if (string.Equals(_last?.Trim(), _ignored, StringComparison.Ordinal))
                    _last = null;
            }
        }
    }

    private sealed class ProcessCubeShareListener : ICubeShareListener
    {
        private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(3);

        private readonly Process _process;
        private readonly StderrTail _stderr;
        private readonly Task _stderrPump;
        private readonly Task _stdoutPump;
        private int _disposed;

        public ProcessCubeShareListener(
            Process process,
            CubeShareListen listen,
            StderrTail stderr,
            Task stderrPump)
        {
            _process = process;
            _stderr = stderr;
            _stderrPump = stderrPump;
            _stderr.Ignore(listen.QuicDetail);
            Listen = listen;
            // Paired lines follow the listen line. Drain them so the pipe never fills.
            _stdoutPump = DrainStdoutAsync(process);
            Exited = WaitExitAsync();
        }

        public CubeShareListen Listen { get; }

        public Task<string> Exited { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }

            try
            {
                await _process.WaitForExitAsync().WaitAsync(GracefulExit).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }
            }

            try
            {
                await Exited.ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }

        private async Task<string> WaitExitAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await _stderrPump.ConfigureAwait(false);
            await _stdoutPump.ConfigureAwait(false);
            var code = _process.ExitCode;
            var detail = _stderr.Last;
            return string.IsNullOrWhiteSpace(detail)
                ? $"share listener exited with code {code}"
                : $"share listener exited with code {code}: {detail.Trim()}";
        }

        private static async Task DrainStdoutAsync(Process process)
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }
}
