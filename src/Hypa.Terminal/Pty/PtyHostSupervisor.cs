using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Starts hypa-pty-host as a child Process and speaks the private frame IPC.
/// Does not call managed fork. Production adapter: <see cref="PtyHostProcess"/>.
///
/// Pump rule: always read Output while writing Input to avoid stdio deadlock.
/// </summary>
public sealed class PtyHostSupervisor : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private int _disposed;
    private bool _helloCompleted;
    /// <summary>
    /// After successful H2 CloseOldOwner / ReleaseAdopted: never SendClose or
    /// Kill(entireProcessTree) — those can murder the adopted child session.
    /// </summary>
    private int _abandonOnDispose;

    private PtyHostSupervisor(Process process, Stream stdin, Stream stdout, ILogger logger)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _logger = logger;
    }

    public int HelperPid => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public bool HasExited => _process.HasExited;
    public bool HelloCompleted => _helloCompleted;

    /// <summary>
    /// Mark dispose to abandon the helper only (no Close frame, no process-tree kill).
    /// Used after successful H2 handoff or ReleaseAdopted.
    /// </summary>
    public void RequestAbandonDispose() => Volatile.Write(ref _abandonOnDispose, 1);

    /// <summary>
    /// Close helper stdin (POLLHUP). Used by tests to force abnormal teardown while
    /// a session is paused — must leave the interactive child alive.
    /// </summary>
    public void CloseStandardInput()
    {
        try { _stdin.Close(); }
        catch { /* already closed */ }
    }

    /// <summary>
    /// Starts the helper and completes the Hello handshake.
    /// Throws if the binary is missing or Hello fails.
    /// </summary>
    public static async Task<PtyHostSupervisor> StartAsync(
        PtyHostOptions? options = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("hypa-pty-host is Unix-only in H-04.");

        options ??= new PtyHostOptions();
        logger ??= NullLogger.Instance;

        var path = ResolveHelperPath(options.HelperPath);
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "hypa-pty-host binary not found. Set HYPA_PTY_HOST or build with scripts/build-hypa-pty-host.sh.",
                path ?? "hypa-pty-host");
        }

        var psi = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = options.HelperWorkingDirectory
                ?? Path.GetDirectoryName(path)
                ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (options.HelperEnvironment is not null)
        {
            foreach (var kv in options.HelperEnvironment)
                psi.Environment[kv.Key] = kv.Value;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start hypa-pty-host.");

            var supervisor = new PtyHostSupervisor(
                process,
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                logger);

            // Drain stderr so a full stderr pipe cannot block the helper.
            _ = Task.Run(() => DrainStderrAsync(process, logger, CancellationToken.None), CancellationToken.None);

            await supervisor.HandshakeHelloAsync(options.HelloTimeout, ct).ConfigureAwait(false);
            return supervisor;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // preserve original
            }

            process.Dispose();
            throw;
        }
    }

    public async Task WriteFrameAsync(PtyHostFrame frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var bytes = PtyHostFrameCodec.Encode(frame);
        await _stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<PtyHostFrame> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var lenBuf = new byte[4];
        await ReadExactAsync(_stdout, lenBuf, ct).ConfigureAwait(false);
        var bodyLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (bodyLen == 0 || bodyLen > PtyHostFrameCodec.MaxBodyLength)
            throw new InvalidDataException($"Invalid helper frame length {bodyLen}.");

        var body = new byte[bodyLen];
        await ReadExactAsync(_stdout, body, ct).ConfigureAwait(false);

        var combined = new byte[4 + bodyLen];
        lenBuf.CopyTo(combined, 0);
        body.CopyTo(combined.AsSpan(4));
        var (frame, _) = PtyHostFrameCodec.Decode(combined);
        return frame;
    }

    public async Task SendCloseAsync(CancellationToken ct = default)
    {
        try
        {
            await WriteFrameAsync(PtyHostFrameCodec.CreateClose(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Close frame to hypa-pty-host failed");
        }
    }

    /// <summary>Wait until the helper process exits, or <paramref name="timeout"/> elapses.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_process.HasExited)
            return true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return _process.HasExited;
        }
    }

    /// <summary>
    /// Kill only the helper process (never the process tree). Used after handoff
    /// when CloseOldOwner should have released the master, or as a last resort.
    /// </summary>
    public void KillHelperOnly()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KillHelperOnly failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var abandon = Volatile.Read(ref _abandonOnDispose) != 0;

        try
        {
            if (!_process.HasExited)
            {
                if (abandon)
                {
                    // Handoff / ReleaseAdopted: never SendClose (native Close terminates
                    // the session when !handed_off) and never Kill(entireProcessTree)
                    // (tree kill can reap the PTY child still parented by the helper).
                    try
                    {
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        KillHelperOnly();
                        try
                        {
                            using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            await _process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }
                }
                else
                {
                    try
                    {
                        // Short Close wait only — caller (PtyHostProcess) already
                        // sent Close and waited for Exit. Do not add another multi-
                        // second grace on the helper; kill promptly if still up.
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                        await SendCloseAsync(cts.Token).ConfigureAwait(false);
                        await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { _process.Kill(entireProcessTree: true); }
                        catch { /* ignore */ }
                        try
                        {
                            using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            await _process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task HandshakeHelloAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await WriteFrameAsync(PtyHostFrameCodec.CreateHello(), cts.Token).ConfigureAwait(false);
        var reply = await ReadFrameAsync(cts.Token).ConfigureAwait(false);
        if (reply.Type != PtyHostFrameType.Hello)
            throw new InvalidDataException($"Expected Hello reply, got {reply.Type}.");

        if (!PtyHostFrameCodec.TryParseHello(reply.Payload, out _, out var error))
            throw new InvalidDataException(error ?? "Invalid Hello reply.");

        _helloCompleted = true;
        _logger.LogDebug("hypa-pty-host Hello completed (helper pid {Pid})", _process.Id);
    }

    public static string? ResolveHelperPath(string? explicitPath = null)
        => NativeAssetResolver.Resolve(
            NativeAssetResolver.PtyHostFileName,
            explicitPath,
            envVarName: "HYPA_PTY_HOST");

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("Helper closed stdout during frame read.");
            offset += n;
        }
    }

    private static async Task DrainStderrAsync(Process process, ILogger logger, CancellationToken ct)
    {
        try
        {
            var buf = new byte[1024];
            var stream = process.StandardError.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;
                logger.LogDebug("hypa-pty-host stderr: {Text}",
                    System.Text.Encoding.UTF8.GetString(buf, 0, n).TrimEnd());
            }
        }
        catch
        {
            // helper exit
        }
    }
}
