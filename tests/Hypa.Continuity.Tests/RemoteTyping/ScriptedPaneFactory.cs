using System.IO.Pipelines;
using System.Text;
using Hypa.Continuity.Tests.Support;
using Hypa.Terminal.Pty;

namespace Hypa.Continuity.Tests.RemoteTyping;

/// <summary>
/// A pane process factory for the remote typing bench. The pane runtime, its
/// input actor and its Ghostty VT are real. Only the child process is a
/// stand-in that runs in this process:
/// <list type="bullet">
/// <item><c>bench-typing:NAME</c> replays the typing fixture NAME. When it reads
/// the bytes of a key on stdin, it writes the recorded echo of that key.</item>
/// <item><c>bench-bulk</c> writes base64 lines with no pause, like
/// <c>base64 /dev/urandom</c>.</item>
/// </list>
/// </summary>
internal sealed class ScriptedPaneFactory : IPtyProcessFactory
{
    public const string TypingPrefix = "bench-typing:";
    public const string BulkCommand = "bench-bulk";

    private int _nextPid = 1_000_000;
    private long _bulkBytes;
    private long _inputMismatches;

    /// <summary>
    /// Keys whose stdin bytes did not equal the recorded input. The replay is
    /// then out of step with the keys, so a cell with a mismatch is not valid.
    /// </summary>
    public long InputMismatches => Interlocked.Read(ref _inputMismatches);

    /// <summary>The bytes that every bulk pane wrote to its stdout.</summary>
    public long BulkBytesWritten => Interlocked.Read(ref _bulkBytes);

    public PtyProviderOptions Options { get; } = new()
    {
        Provider = PtyProviderKind.ProcessIo,
        RequireHelperBinary = false,
    };

    public IPtyProcess Spawn(
        string fileName,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        IReadOnlyDictionary<string, string>? env = null)
    {
        _ = args;
        _ = cwd;
        _ = env;
        // A pid that no real process has, so no probe reads another process.
        var pid = Interlocked.Increment(ref _nextPid);
        if (fileName.StartsWith(TypingPrefix, StringComparison.Ordinal))
        {
            var script = TypingScript.Load(fileName[TypingPrefix.Length..]);
            return new ScriptedProcess(
                pid,
                cols,
                rows,
                (process, stdin, stdout, ct) => ReplayTypingAsync(script, process, stdin, stdout, ct));
        }

        if (string.Equals(fileName, BulkCommand, StringComparison.Ordinal))
            return new ScriptedProcess(pid, cols, rows, (_, stdin, stdout, ct) => WriteBulkAsync(stdin, stdout, ct));
        throw new InvalidOperationException("the bench pane factory has no command " + fileName);
    }

    /// <summary>
    /// Writes the startup reads, then answers each key. The gaps between the
    /// reads of one key are the recorded gaps, clamped to 0-20 ms, as in the
    /// typing conformance harness.
    /// </summary>
    private async Task ReplayTypingAsync(
        TypingScript script,
        ScriptedProcess process,
        PipeReader stdin,
        PipeWriter stdout,
        CancellationToken ct)
    {
        // The recorder ran at the script size. Write the startup reads only
        // after the attach resized the pane to that size, so the pane VT reads
        // the same bytes at the same size as the recorder terminal.
        try
        {
            await process.WaitForSizeAsync(script.Cols, script.Rows).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
        }

        foreach (var key in script.StartupKeys)
            await WriteChunksAsync(key, stdout, ct);

        var pending = new List<byte>();
        foreach (var key in script.TypedKeys)
        {
            while (pending.Count < key.Input.Length)
            {
                var read = await stdin.ReadAsync(ct);
                foreach (var segment in read.Buffer)
                    pending.AddRange(segment.Span.ToArray());
                stdin.AdvanceTo(read.Buffer.End);
                // The pane VT answers the queries of the startup output (device
                // attributes, cursor position, colors) on this stdin. Those
                // replies are not keys.
                StripTerminalReplies(pending);
                if (read.IsCompleted && pending.Count < key.Input.Length)
                    return;
            }

            if (!pending.Take(key.Input.Length).SequenceEqual(key.Input))
                Interlocked.Increment(ref _inputMismatches);
            pending.RemoveRange(0, key.Input.Length);
            await WriteChunksAsync(key, stdout, ct);
        }

        // The script is done. Keep the process alive like an idle shell.
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>
    /// Removes each complete terminal reply from <paramref name="pending"/>:
    /// a CSI sequence with a private marker or with a final byte that is not
    /// a cursor key (A to D), and each OSC, DCS or APC string. A key of the
    /// fixtures is plain text, a control byte, or a cursor key (CSI or SS3).
    /// An incomplete sequence stays until its end arrives.
    /// </summary>
    internal static void StripTerminalReplies(List<byte> pending)
    {
        var i = 0;
        while (i < pending.Count)
        {
            if (pending[i] != 0x1b || i + 1 >= pending.Count)
            {
                i++;
                continue;
            }

            var kind = pending[i + 1];
            var end = -1;
            if (kind == (byte)'[')
            {
                var j = i + 2;
                var privateMarker = j < pending.Count && pending[j] is (byte)'?' or (byte)'>' or (byte)'<' or (byte)'=';
                while (j < pending.Count && pending[j] is >= 0x20 and <= 0x3f)
                    j++;
                if (j < pending.Count && pending[j] is >= 0x40 and <= 0x7e)
                {
                    var cursorKey = pending[j] is >= (byte)'A' and <= (byte)'D';
                    if (privateMarker || !cursorKey)
                        end = j;
                }
            }
            else if (kind is (byte)']' or (byte)'P' or (byte)'_')
            {
                for (var j = i + 2; j < pending.Count; j++)
                {
                    if (pending[j] == 0x07)
                    {
                        end = j;
                        break;
                    }

                    if (pending[j] == 0x1b && j + 1 < pending.Count && pending[j + 1] == (byte)'\\')
                    {
                        end = j + 1;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                i++;
                continue;
            }

            pending.RemoveRange(i, end - i + 1);
        }
    }

    private static async Task WriteChunksAsync(TypingScriptKey key, PipeWriter stdout, CancellationToken ct)
    {
        var previous = 0.0;
        foreach (var chunk in key.Chunks)
        {
            var gap = Math.Clamp(chunk.Ms - previous, 0, 20);
            previous = chunk.Ms;
            if (gap >= 1)
                await Pacing.GapAsync(TimeSpan.FromMilliseconds(gap), ct);
            await stdout.WriteAsync(chunk.Bytes, ct);
        }
    }

    private async Task WriteBulkAsync(PipeReader stdin, PipeWriter stdout, CancellationToken ct)
    {
        _ = stdin;
        var random = new Random(1337);
        var raw = new byte[57 * 48];
        while (!ct.IsCancellationRequested)
        {
            random.NextBytes(raw);
            var text = new StringBuilder(raw.Length * 2);
            for (var offset = 0; offset < raw.Length; offset += 57)
            {
                text.Append(Convert.ToBase64String(raw, offset, 57));
                text.Append("\r\n");
            }

            // The pipe holds the writer when the pane reads slower than this.
            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            await stdout.WriteAsync(bytes, ct);
            Interlocked.Add(ref _bulkBytes, bytes.Length);
        }
    }

    /// <summary>A stand-in child: stdin and stdout are in-memory pipes.</summary>
    private sealed class ScriptedProcess : IPtyProcess
    {
        private readonly Pipe _stdin = new();
        private readonly Pipe _stdout = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sizeGate = new();
        private readonly List<(int Cols, int Rows, TaskCompletionSource Done)> _sizeWaiters = [];
        private readonly Task _run;
        private int _cols;
        private int _rows;

        public ScriptedProcess(
            int pid,
            int cols,
            int rows,
            Func<ScriptedProcess, PipeReader, PipeWriter, CancellationToken, Task> body)
        {
            Pid = pid;
            _cols = cols;
            _rows = rows;
            StandardInput = _stdin.Writer.AsStream();
            StandardOutput = _stdout.Reader.AsStream();
            _run = Task.Run(async () =>
            {
                try
                {
                    await body(this, _stdin.Reader, _stdout.Writer, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (InvalidOperationException)
                {
                    // The pane closed its end of a pipe.
                }
                finally
                {
                    await _stdout.Writer.CompleteAsync();
                    _exited.TrySetResult();
                }
            });
        }

        public int Pid { get; }

        public bool IsRunning => !_exited.Task.IsCompleted;

        public int? ExitCode => _exited.Task.IsCompleted ? 0 : null;

        public Stream StandardInput { get; }

        public Stream StandardOutput { get; }

        public void Resize(int cols, int rows)
        {
            lock (_sizeGate)
            {
                _cols = cols;
                _rows = rows;
                foreach (var waiter in _sizeWaiters.Where(w => w.Cols == cols && w.Rows == rows))
                    waiter.Done.TrySetResult();
                _sizeWaiters.RemoveAll(w => w.Done.Task.IsCompleted);
            }
        }

        /// <summary>Completes when the pane size is <paramref name="cols"/> x <paramref name="rows"/>.</summary>
        public Task WaitForSizeAsync(int cols, int rows)
        {
            lock (_sizeGate)
            {
                if (_cols == cols && _rows == rows)
                    return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sizeWaiters.Add((cols, rows, done));
                return done.Task;
            }
        }

        public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _stdin.Writer.CompleteAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }

            _stop.Dispose();
        }
    }
}
