using System.Text;
using System.Threading.Channels;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Microsoft.Extensions.Logging;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Test helpers: process-io is opt-in so suites do not require hypa-pty-host.
/// Production Unix default remains <see cref="PtyProviderKind.HypaPtyHost"/>.
/// </summary>
internal static class TestPaneFactories
{
    public static IPtyProcessFactory ProcessIo() =>
        new PtyProcessFactory(new PtyProviderOptions
        {
            Provider = PtyProviderKind.ProcessIo,
            RequireHelperBinary = false,
        });

    public static PaneRuntimeFactory Create(
        IIntelligencePipeline? intelligence = null,
        ILoggerFactory? loggerFactory = null,
        IPtyProcessFactory? ptyFactory = null,
        IVtEngineFactory? vtEngineFactory = null)
        => new(
            intelligence,
            loggerFactory,
            ptyFactory ?? ProcessIo(),
            vtEngineFactory ?? new StubVtEngineFactory());

    /// <summary>
    /// Track DEC 2026 from fed text for Basic test doubles. Last
    /// occurrence wins when both opener and closer are in one slice.
    /// </summary>
    internal static bool NoteSynchronizedOutput(string text, bool current)
    {
        var on = text.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        var off = text.LastIndexOf("\u001b[?2026l", StringComparison.Ordinal);
        if (on < 0 && off < 0)
            return current;
        return on > off;
    }

    /// <summary>Graph-only factory: no child process. Use for tab/layout unit tests.</summary>
    public static IPaneRuntimeFactory Stub() => new GraphPaneFactory();

    /// <summary>Records the last <see cref="PaneSpawnOptions"/> for occupant assertions.</summary>
    public static CapturingPaneFactory Capturing() => new();

    /// <summary>Holds <see cref="IPaneRuntime.StartAsync"/> after <paramref name="holdAfterStarts"/> completed starts.</summary>
    public static GatedPaneFactory Gated(int holdAfterStarts = 1) => new(holdAfterStarts);

    /// <summary>Fails <see cref="IPaneRuntimeFactory.Create"/> on the Nth create (1-based).</summary>
    public static FailingPaneFactory FailOnStart(int failOnCreateNumber) =>
        new(failOnCreateNumber, failAtCreate: true);

    /// <summary>Fails <see cref="IPaneRuntime.StartAsync"/> on the Nth create (1-based).</summary>
    public static FailingPaneFactory FailOnStartAsync(int failOnCreateNumber) =>
        new(failOnCreateNumber, failAtCreate: false);

    /// <summary>Scripted occupants that can hold StartAsync, emit, or fail on demand.</summary>
    public static ScriptedPaneFactory Scripted(
        int holdOnCreate = 0,
        int failOnCreate = 0,
        bool emitOutputOnHold = false,
        bool emitExitOnHold = false,
        string outputText = "Claude Code\nThinking") =>
        new()
        {
            HoldOnCreate = holdOnCreate,
            FailOnCreate = failOnCreate,
            EmitOutputOnHold = emitOutputOnHold,
            EmitExitOnHold = emitExitOnHold,
            OutputText = outputText,
        };

    internal sealed class GatedPaneFactory : IPaneRuntimeFactory
    {
        private readonly int _holdAfterStarts;
        private int _starts;
        public TaskCompletionSource StartHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedPaneFactory(int holdAfterStarts) => _holdAfterStarts = holdAfterStarts;

        public IPaneRuntime Create(PaneSpawnOptions options) => new GatedPaneRuntime(this, options.Id);

        private sealed class GatedPaneRuntime(GatedPaneFactory owner, PaneId id) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive { get; private set; } = true;
            public int? ExitCode { get; private set; }
            public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
            public async Task StartAsync(CancellationToken ct)
            {
                var n = Interlocked.Increment(ref owner._starts);
                if (n > owner._holdAfterStarts)
                {
                    owner.Registered.TrySetResult();
                    await owner.StartHold.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                IsAlive = true;
            }
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                return ValueTask.CompletedTask;
            }
            public ValueTask WriteTextAsync(string text, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                return ValueTask.CompletedTask;
            }
            public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                return ValueTask.CompletedTask;
            }
            public string ReadVisibleText() => "";
            public string ReadRecentText(int maxLines) => "";
            public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
            public string ReadDetectionText() => "";
            public ValueTask DisposeAsync()
            {
                IsAlive = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class FailingPaneFactory(int failOnCreateNumber, bool failAtCreate) : IPaneRuntimeFactory
    {
        private int _creates;

        public IPaneRuntime? FirstOccupant { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var n = Interlocked.Increment(ref _creates);
            if (n == failOnCreateNumber && failAtCreate)
                throw new InvalidOperationException("injected spawn fail");
            if (n == failOnCreateNumber)
                return new FailingStartRuntime(options.Id);
            var occupant = new GraphPaneRuntime(options.Id);
            FirstOccupant ??= occupant;
            return occupant;
        }
    }

    private sealed class FailingStartRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) =>
            throw new InvalidOperationException("injected start fail");
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ScriptedPaneFactory : IPaneRuntimeFactory
    {
        private int _creates;
        public List<ScriptedPaneRuntime> Created { get; } = [];
        public TaskCompletionSource StartHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DisposeHold { get; set; }
        public int HoldOnCreate { get; init; }
        public int FailOnCreate { get; init; }
        public bool EmitOutputOnHold { get; init; }
        public bool EmitExitOnHold { get; init; }
        public string OutputText { get; init; } = "Claude Code\nThinking";

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var n = Interlocked.Increment(ref _creates);
            var runtime = new ScriptedPaneRuntime(this, options.Id, n);
            Created.Add(runtime);
            return runtime;
        }
    }

    internal sealed class ScriptedPaneRuntime : IPaneRuntime
    {
        private readonly ScriptedPaneFactory _owner;
        private readonly int _ordinal;
        private string _detection = "";
        private int _detectionReads;
        private bool _disposed;

        public ScriptedPaneRuntime(ScriptedPaneFactory owner, PaneId id, int ordinal)
        {
            _owner = owner;
            _ordinal = ordinal;
            Id = id;
        }

        public PaneId Id { get; }
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_012;
        public int DetectionReadCount => Volatile.Read(ref _detectionReads);
        public List<byte[]> Writes { get; } = [];
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;

        public async Task StartAsync(CancellationToken ct)
        {
            if (_ordinal == _owner.HoldOnCreate)
            {
                _owner.Held.TrySetResult();
                await _owner.StartHold.Task.WaitAsync(ct).ConfigureAwait(false);
                if (_owner.EmitOutputOnHold)
                    FireOutput(_owner.OutputText);
                if (_owner.EmitExitOnHold)
                    FireExited(1);
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ordinal == _owner.FailOnCreate)
                throw new InvalidOperationException("injected start fail");
            IsAlive = true;
        }

        public void FireOutput(string text)
        {
            _detection = text;
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
        }

        public void FireExited(int code)
        {
            IsAlive = false;
            ExitCode = code;
            Pid = null;
            Exited?.Invoke(this, code);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            Writes.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            Writes.Add(Encoding.UTF8.GetBytes(text));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }

        public string DetectionOscTitle { get; set; } = "";
        public string DetectionOscProgress { get; set; } = "";
        public int OscClears { get; private set; }

        public string ReadDetectionOscTitle() => DetectionOscTitle;

        public string ReadDetectionOscProgress() => DetectionOscProgress;

        public void ClearAgentOscState()
        {
            OscClears++;
            DetectionOscTitle = "";
            DetectionOscProgress = "";
        }

        public string ReadVisibleText() => _detection;
        public string ReadRecentText(int maxLines) => _detection;
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText()
        {
            Interlocked.Increment(ref _detectionReads);
            return _detection;
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            IsAlive = false;
            if (_ordinal != _owner.FailOnCreate || _owner.DisposeHold is not { } hold)
                return;
            _owner.DisposeEntered.TrySetResult();
            await hold.Task.ConfigureAwait(false);
        }
    }

    internal sealed class CapturingPaneFactory : IPaneRuntimeFactory
    {
        public PaneSpawnOptions? LastOptions { get; private set; }
        public List<PaneSpawnOptions> Options { get; } = [];
        public List<string> Writes { get; } = [];
        /// <summary>When set, <see cref="IPaneRuntime.SnapshotHistory"/> returns this text.</summary>
        public string? ProgrammedHistory { get; set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            LastOptions = options;
            Options.Add(options);
            return new CapturingPaneRuntime(options, this);
        }

        internal void RecordWrite(string text) => Writes.Add(text);
    }

    private sealed class CapturingPaneRuntime : IPaneRuntime
    {
        private readonly CapturingPaneFactory _owner;
        private string? _seeded;

        public CapturingPaneRuntime(PaneSpawnOptions options, CapturingPaneFactory owner)
        {
            Id = options.Id;
            _owner = owner;
            _seeded = options.InitialHistoryAnsi;
        }

        public PaneId Id { get; }
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            _owner.RecordWrite(text);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public string ReadVisibleText() => _seeded ?? "";
        public string ReadRecentText(int maxLines) => _seeded ?? "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => _seeded ?? "";
        public string? SnapshotHistory()
        {
            if (!string.IsNullOrWhiteSpace(_owner.ProgrammedHistory))
                return _owner.ProgrammedHistory;
            return string.IsNullOrWhiteSpace(_seeded) ? null : _seeded;
        }

        public void SeedHistoryAnsi(string ansi)
        {
            if (!string.IsNullOrEmpty(ansi))
                _seeded = ansi;
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GraphPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new GraphPaneRuntime(options.Id);
    }

    private sealed class GraphPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class GhosttyEmitPaneFactory : IPaneRuntimeFactory
    {
        private readonly string? _libraryPath;

        public GhosttyEmitPaneFactory(string? libraryPath) => _libraryPath = libraryPath;

        public PaneRuntime? Last { get; private set; }

        public InjectedPtyProcess? Pty { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var cols = Math.Max(20, options.Cols);
            var rows = Math.Max(6, options.Rows);
            var pty = new InjectedPtyProcess();
            IVtEngine vt = new GhosttyVtEngine(
                cols, rows, maxScrollback: 80, libraryPathOverride: _libraryPath);
            var runtime = new PaneRuntime(
                options with { Cols = cols, Rows = rows },
                (_, _) => pty,
                vt);
            Pty = pty;
            Last = runtime;
            return runtime;
        }
    }

    internal sealed class InjectedPtyProcess : IPtyProcess
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private readonly ChannelStream _output;

        public InjectedPtyProcess() => _output = new ChannelStream(_channel.Reader);

        public int Pid => 4242;
        public bool IsRunning => true;
        public int? ExitCode => null;
        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput => _output;

        public void Emit(byte[] chunk) => _channel.Writer.TryWrite(chunk);

        public void Resize(int cols, int rows)
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private byte[]? _current;
        private int _offset;

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

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
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
    }
}
