using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Redirected Process stdio with merged stdout/stderr.
/// Redirected process I/O — test/batch opt-in, and the Windows default.
/// Not a full interactive TTY. Unix default panes use <see cref="PtyHostProcess"/>.
/// Porta.Pty was not adopted.
///
/// Output is buffered through a <b>bounded</b> channel. When the consumer lags,
/// writers block (backpressure) so memory cannot grow without limit under runaway
/// agent or compiler output. Durable history lives in the VT scrollback, not here.
/// </summary>
public sealed class ProcessPtyFallback : IPtyProcess
{
    /// <summary>
    /// Default in-flight chunk capacity (~4 KiB × capacity ≈ 1 MiB before backpressure).
    /// </summary>
    public const int DefaultOutputChannelCapacity = 256;

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _channel;
    private readonly ChannelStream _output;
    private readonly CancellationTokenSource _pumpCts;
    private readonly Task _pumpTask;
    private int _disposed;

    private ProcessPtyFallback(
        Process process,
        Channel<byte[]> channel,
        ChannelStream output,
        CancellationTokenSource pumpCts,
        Task pumpTask,
        ILogger? logger)
    {
        _process = process;
        _channel = channel;
        _output = output;
        _pumpCts = pumpCts;
        _pumpTask = pumpTask;
        _logger = logger ?? NullLogger.Instance;
    }

    public int Pid => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _output;

    public static ProcessPtyFallback Spawn(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        IReadOnlyDictionary<string, string>? env = null,
        ILogger? logger = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Hosted allow-list only. Never dump the parent environment.
        psi.Environment.Clear();
        foreach (var kv in ChildEnvironmentBuilder.BuildHosted(env))
            psi.Environment[kv.Key] = kv.Value;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var channel = CreateOutputChannel();
        var output = new ChannelStream(channel.Reader);

        process.Start();

        // Drain both streams fully, then complete — avoid racing process.Exited.
        var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(
            () => PumpAllAsync(process, channel.Writer, pumpCts.Token),
            CancellationToken.None);

        return new ProcessPtyFallback(process, channel, output, pumpCts, pumpTask, logger);
    }

    /// <summary>
    /// Creates the bounded merged-output channel. Exposed for unit tests of
    /// backpressure behaviour without spawning a process.
    /// </summary>
    internal static Channel<byte[]> CreateOutputChannel(int capacity = DefaultOutputChannelCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        return Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    private static async Task PumpAllAsync(
        Process process,
        ChannelWriter<byte[]> writer,
        CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                PumpAsync(process.StandardOutput.BaseStream, writer, ct),
                PumpAsync(process.StandardError.BaseStream, writer, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal cancels both source reads and pending channel writes.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task PumpAsync(
        Stream source,
        ChannelWriter<byte[]> writer,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                    .ConfigureAwait(false);
                if (n <= 0)
                    break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                await writer.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal cancels both source reads and pending channel writes.
        }
        catch (ChannelClosedException)
        {
            // Disposal completes the channel to release blocked writers.
        }
        catch
        {
            // process exit / dispose
        }
    }

    public void Resize(int cols, int rows)
    {
        _logger.LogDebug("Resize ignored for process fallback ({Cols}x{Rows})", cols, rows);
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            // Release blocked readers and writers before waiting for the process.
            _pumpCts.Cancel();
            _channel.Writer.TryComplete();

            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
                try { await _process.WaitForExitAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            try { await _pumpTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Output pump ended during process disposal");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing process fallback");
        }
        finally
        {
            await _output.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
            _pumpCts.Dispose();
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private byte[]? _current;
        private int _offset;
        private bool _disposed;

        public ChannelStream(ChannelReader<byte[]> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var n = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                    _offset += n;
                    if (_offset >= _current.Length)
                    {
                        _current = null;
                        _offset = 0;
                    }
                    return n;
                }

                if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;

                if (!_reader.TryRead(out _current))
                    continue;
                _offset = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
