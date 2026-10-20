using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.History;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Hypa.Terminal.Vt.Osc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.Terminal;

/// <summary>
/// Pane process + VT.
/// Unix default I/O: <see cref="Pty.PtyHostProcess"/> (hypa-pty-host; no managed fork).
/// Process-io (<see cref="Pty.ProcessPtyFallback"/>) is opt-in for tests and batch.
/// Porta.Pty was evaluated and not adopted.
/// </summary>
public sealed class PaneRuntime : IPaneRuntime, IPaneHandoffControl, IPaneVtSnapshot
{
    private readonly PaneSpawnOptions _options;
    private readonly IVtEngine _vt;
    private readonly IIntelligencePipeline? _intelligence;
    private readonly ILogger _logger;
    private readonly Func<string, IReadOnlyList<string>, IPtyProcess> _spawnProcess;
    private IPtyProcess? _pty;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private Task? _waitLoop;
    private int? _exitCode;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _handoffGate = new();
    private readonly object _vtIoGate = new();
    private long _feedGeneration;
    private PaneFeedPaintDecision _lastFeedPaintDecision = new(0, true);
    private int _scrollOffset;
    private HandoffExportLease? _handoffExport;
    // Retained live grid. Only TryCaptureGhosttyLiveFrameLocked writes the
    // grid, the tables and the fields below, under _liveCaptureGate. Lock
    // order: _liveCaptureGate, then _vtIoGate.
    private readonly object _liveCaptureGate = new();
    private VtFrameCell[] _liveCells = [];
    private VtStampTables _liveTables = new();
    private VtFrameCell[] _verifyCells = [];
    private VtStampTables _verifyTables = new();
    private VtFrameTables _lastTables = VtFrameTables.Empty;
    private readonly LiveStampOutcome _liveOutcome = new();
    private LiveStampBase? _liveRetained;
    private LiveStampFallbackReason _liveInvalidReason = LiveStampFallbackReason.NoRetainedGrid;
    private VtFrameGrid? _lastLiveGrid;
    private int _dirtyStampsSinceVerify;
    // Read and written under _vtIoGate: the identity of the last live grid,
    // and of the grid that the last owned commit posted.
    private object? _lastLiveToken;
    private object? _committedLiveToken;
    private readonly ILiveStampMeter _liveStampMeter = new LiveStampCounters();
    // Read and written under _vtIoGate: the attach captures that failed
    // closed in sequence since the last good attach capture.
    private int _attachCaptureFailures;
    // Written under _vtIoGate: the last attach capture that posted was the
    // lenient snapshot at the failure limit.
    private volatile bool _attachFrameDegraded;
    private int _liveCols;
    private int _liveRowCount;
    private readonly IPaneHistoryStore? _historyStore;
    private readonly long _nativeWindowBytes;
    private readonly long _scrollbackLimitBytes;
    private readonly VtStampTables _historyTables = new();
    private VtFrameTables? _historyFrozen;
    // Row buffers for the history drain. The store copies the rows, so the
    // next drain writes into the same arrays.
    private readonly List<VtFrameCell[]> _historyRows = [];
    internal const int MaxKeptHistoryRowBuffers = 512;
    private readonly List<VtFrameCell[]> _historyOpenRows = [];
    private int _historyOpenCols;
    private int _historyGapCount;
    private bool _deepScrollActive;
    private int _deepScrollOffset;
    private int _committedStoreRows;
    private bool _historyStoreReleased;
    private readonly object _handlerGate = new();
    private Action<IPaneRuntime, ReadOnlyMemory<byte>>[] _outputHandlers = [];
    private Action<IPaneRuntime, int>[] _bellHandlers = [];
    private HostTerminalTheme _hostTheme;
    private long _appliedHostThemeVersion;
    private bool _childFgOwned;
    private bool _childBgOwned;
    private int? _transientOwnerTpgid;
    private readonly PaneOscPipeline _osc = new();
    private readonly List<ProgramStatusSignal> _programStatusScratch = [];
    // The termios mode of the PTY at the output that the VT has fed. Read and
    // written under _vtIoGate. Null while unknown: process-io, or a helper
    // below protocol minor 1.
    private PtyTtyMode? _ttyMode;
    private Action<IPaneRuntime, IReadOnlyList<ProgramStatusSignal>>[] _programStatusHandlers = [];

    public PaneRuntime(
        PaneSpawnOptions options,
        IVtEngine vt,
        IIntelligencePipeline? intelligence = null,
        ILogger? logger = null,
        IPtyProcessFactory? ptyFactory = null,
        IPaneHistoryStore? historyStore = null,
        long nativeWindowBytes = 0,
        long scrollbackLimitBytes = 0,
        IPaneProcessInfoProbe? processInfoProbe = null,
        ILiveStampMeter? liveStampMeter = null)
    {
        ArgumentNullException.ThrowIfNull(vt);
        if (liveStampMeter is not null)
            _liveStampMeter = liveStampMeter;
        _options = options;
        Id = options.Id;
        _vt = vt;
        _intelligence = intelligence;
        _logger = logger ?? NullLogger.Instance;
        var factory = ptyFactory ?? new PtyProcessFactory(logger: _logger);
        var env = options.StripPaneIdEnv
            ? PaneIdEnvironment.Strip(options.Env)
            : options.Env;
        _spawnProcess = (file, args) => factory.Spawn(
            file, args, _options.Cwd, _options.Cols, _options.Rows, env);
        _historyStore = historyStore;
        _nativeWindowBytes = nativeWindowBytes;
        _scrollbackLimitBytes = scrollbackLimitBytes > 0
            ? scrollbackLimitBytes
            : options.ScrollbackLimitBytes;
        ProcessInfoProbe = processInfoProbe;
    }

    /// <summary>
    /// Test seam for exercising lifecycle races without starting an OS process.
    /// Production callers use the public constructor and the normal spawn path.
    /// </summary>
    internal PaneRuntime(
        PaneSpawnOptions options,
        Func<string, IReadOnlyList<string>, IPtyProcess> spawnProcess,
        IVtEngine vt,
        IIntelligencePipeline? intelligence = null,
        ILogger? logger = null,
        IPaneHistoryStore? historyStore = null,
        long nativeWindowBytes = 0,
        long scrollbackLimitBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(vt);
        _options = options;
        Id = options.Id;
        _vt = vt;
        _intelligence = intelligence;
        _logger = logger ?? NullLogger.Instance;
        _spawnProcess = spawnProcess ?? throw new ArgumentNullException(nameof(spawnProcess));
        _historyStore = historyStore;
        _nativeWindowBytes = nativeWindowBytes;
        _scrollbackLimitBytes = scrollbackLimitBytes > 0
            ? scrollbackLimitBytes
            : options.ScrollbackLimitBytes;
    }

    internal IVtEngine VtEngine => _vt;

    internal IPaneHistoryStore? HistoryStore => _historyStore;

    internal int HistoryRowBufferCount
    {
        get
        {
            lock (_vtIoGate)
                return _historyRows.Count;
        }
    }

    internal int HistoryGapCount
    {
        get
        {
            lock (_vtIoGate)
                return _historyGapCount;
        }
    }

    public PaneId Id { get; }
    public bool IsAlive => _pty?.IsRunning == true;
    public int? ExitCode => _exitCode ?? _pty?.ExitCode;
    public int? Pid => _pty is { IsRunning: true } p ? p.Pid : _pty?.Pid;

    public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_handlerGate)
            {
                var current = _outputHandlers;
                var next = new Action<IPaneRuntime, ReadOnlyMemory<byte>>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = value;
                Volatile.Write(ref _outputHandlers, next);
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_handlerGate)
            {
                var current = _outputHandlers;
                var index = Array.IndexOf(current, value);
                if (index < 0)
                    return;
                if (current.Length == 1)
                {
                    Volatile.Write(ref _outputHandlers, []);
                    return;
                }

                var next = new Action<IPaneRuntime, ReadOnlyMemory<byte>>[current.Length - 1];
                if (index > 0)
                    Array.Copy(current, 0, next, 0, index);
                if (index < current.Length - 1)
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                Volatile.Write(ref _outputHandlers, next);
            }
        }
    }

    public event Action<IPaneRuntime, int>? BellReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_handlerGate)
            {
                var current = _bellHandlers;
                var next = new Action<IPaneRuntime, int>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = value;
                Volatile.Write(ref _bellHandlers, next);
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_handlerGate)
            {
                var current = _bellHandlers;
                var index = Array.IndexOf(current, value);
                if (index < 0)
                    return;
                if (current.Length == 1)
                {
                    Volatile.Write(ref _bellHandlers, []);
                    return;
                }

                var next = new Action<IPaneRuntime, int>[current.Length - 1];
                if (index > 0)
                    Array.Copy(current, 0, next, 0, index);
                if (index < current.Length - 1)
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                Volatile.Write(ref _bellHandlers, next);
            }
        }
    }
    /// <inheritdoc />
    public event Action<IPaneRuntime>? TtyModeChanged;

    /// <summary>
    /// The termios mode of the PTY at the output that the VT has fed, or null
    /// while unknown. Each captured frame carries its echo flag.
    /// </summary>
    public PtyTtyMode? TtyMode
    {
        get
        {
            lock (_vtIoGate)
                return _ttyMode;
        }
    }

    public event Action<IPaneRuntime, IReadOnlyList<ProgramStatusSignal>>? ProgramStatusSignaled
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_handlerGate)
            {
                var current = _programStatusHandlers;
                var next = new Action<IPaneRuntime, IReadOnlyList<ProgramStatusSignal>>[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = value;
                Volatile.Write(ref _programStatusHandlers, next);
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_handlerGate)
            {
                var current = _programStatusHandlers;
                var index = Array.IndexOf(current, value);
                if (index < 0)
                    return;
                var next = new Action<IPaneRuntime, IReadOnlyList<ProgramStatusSignal>>[current.Length - 1];
                if (index > 0)
                    Array.Copy(current, 0, next, 0, index);
                if (index < current.Length - 1)
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                Volatile.Write(ref _programStatusHandlers, next);
            }
        }
    }

    public event Action<IPaneRuntime, int>? Exited;

    /// <summary>Test seam: tpgid for restore. Production uses the process probe.</summary>
    internal int? TestForegroundGroup { get; set; }

    /// <summary>Test seam: shell pid for restore. Production uses <see cref="Pid"/>.</summary>
    internal int? TestShellPid { get; set; }

    internal IPaneProcessInfoProbe? ProcessInfoProbe { get; set; }

    internal HostTerminalTheme HostThemeSnapshot
    {
        get
        {
            lock (_vtIoGate)
                return _hostTheme;
        }
    }

    internal long AppliedHostThemeVersion
    {
        get
        {
            lock (_vtIoGate)
                return _appliedHostThemeVersion;
        }
    }

    internal bool ChildForegroundOwned
    {
        get
        {
            lock (_vtIoGate)
                return _childFgOwned;
        }
    }

    internal bool ChildBackgroundOwned
    {
        get
        {
            lock (_vtIoGate)
                return _childBgOwned;
        }
    }

    /// <summary>
    // Skip a channel the child
    /// owns. Write OSC 10/11 through Ghostty Feed. Do not run the child OSC
    /// tracker on host-originated feeds.
    /// </summary>
    public void ApplyHostTerminalTheme(HostTerminalTheme theme) =>
        ApplyHostTerminalTheme(theme, version: 0);

    public void ApplyHostTerminalTheme(HostTerminalTheme theme, long version)
    {
        lock (_vtIoGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (version > 0 && version < _appliedHostThemeVersion)
                return;
            if (version > _appliedHostThemeVersion)
                _appliedHostThemeVersion = version;
            ApplyHostTerminalThemeUnlocked(theme);
        }
    }

    internal List<byte[]> HandleChildBytesForTests(ReadOnlySpan<byte> bytes)
    {
        var replies = new List<byte[]>();
        lock (_vtIoGate)
            ApplyChildBytesUnlocked(bytes, replies);
        return replies;
    }

    internal List<byte[]> HandleChildBytesForTests(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<ProgramStatusSignal> programStatusSignals)
    {
        var replies = new List<byte[]>();
        lock (_vtIoGate)
        {
            ApplyChildBytesUnlocked(bytes, replies);
            programStatusSignals = TakeProgramStatusSignalsUnlocked();
        }

        return replies;
    }

    internal void RestoreHostThemeForTests(int? tpgid)
    {
        lock (_vtIoGate)
            MaybeRestoreHostThemeUnlocked(tpgid);
    }

    public bool SupportsHandoff => PtyHandoffCapability.SupportsH2(_pty);

    public async Task<RuntimeResult<PaneHandoffExport>> ExportHandoffAsync(
        string runtimeSessionId,
        int generation,
        CancellationToken ct)
    {
        var exporter = PtyHandoffCapability.TryGetExporter(_pty);
        if (exporter is null || !PtyHandoffCapability.SupportsH2(_pty))
        {
            return RuntimeResult<PaneHandoffExport>.Fail(
                RuntimePersistenceError.Access("PTY provider does not support H2 handoff."));
        }

        var nonce = RandomNumberGenerator.GetBytes(16);
        var lease = HandoffExportLease.Start(
            exporter,
            new PtyHandoffExportOptions
            {
                RuntimeSessionId = runtimeSessionId,
                PaneId = Id.Value,
                Generation = generation,
                Nonce = nonce,
            },
            ct,
            _logger);
        await ReplaceHandoffExportAsync(lease).ConfigureAwait(false);

        return RuntimeResult<PaneHandoffExport>.Ok(new PaneHandoffExport
        {
            HandoffPath = lease.SocketPath,
            NonceHex = Convert.ToHexString(nonce).ToLowerInvariant(),
            Generation = generation,
        });
    }

    public async Task<RuntimeResult<PaneHandoffAdopt>> AdoptHandoffAsync(
        string handoffPath,
        string nonceHex,
        int generation,
        CancellationToken ct)
    {
        if (_pty is not null && !PtyHandoffCapability.SupportsH2(_pty))
        {
            return RuntimeResult<PaneHandoffAdopt>.Fail(
                RuntimePersistenceError.Access("PTY provider does not support H2 handoff."));
        }

        await ReplaceHandoffExportAsync(null).ConfigureAwait(false);

        try
        {
            var nonce = Convert.FromHexString(nonceHex);
            await using var port = await UnixPtyHandoffPort.ConnectAsync(handoffPath, ct)
                .ConfigureAwait(false);
            var (process, _) = await PtyHostProcess.ImportHandoffAsync(
                port,
                new PtyHandoffAdoptOptions
                {
                    Nonce = nonce,
                    Generation = generation,
                    ExpectedGeneration = generation,
                    ChildPid = 1,
                    TargetRuntimeId = "hypa",
                },
                _logger,
                ct).ConfigureAwait(false);

            var old = _pty;
            DetachTtyModeSource(old);
            _pty = process;
            AttachTtyModeSource(process);
            if (old is not null)
                await old.DisposeAsync().ConfigureAwait(false);

            return RuntimeResult<PaneHandoffAdopt>.Ok(new PaneHandoffAdopt
            {
                Adopted = true,
                ChildPid = process.Pid,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Handoff adopt failed");
            return RuntimeResult<PaneHandoffAdopt>.Fail(RuntimePersistenceError.Io("handoff adopt failed"));
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pty is not null)
                return;

            if (!string.IsNullOrEmpty(_options.InitialHistoryAnsi))
            {
                lock (_vtIoGate)
                    SeedHistoryAnsiUnlocked(_options.InitialHistoryAnsi);
            }

            var (file, args) = ResolveCommand(_options.Command, _options.Args, _options.Terminal);
            IPtyProcess? spawned = null;
            try
            {
                // Startup and disposal share the lifecycle gate. Disposal cannot
                // publish a terminal as already disposed while this is spawning.
                spawned = _spawnProcess(file, args);
                _pty = spawned;
                AttachTtyModeSource(spawned);
                _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);
                _waitLoop = Task.Run(() => WaitLoopAsync(_readCts.Token), CancellationToken.None);
            }
            catch
            {
                if (_readCts is not null)
                {
                    await _readCts.CancelAsync().ConfigureAwait(false);
                    _readCts.Dispose();
                    _readCts = null;
                }

                DetachTtyModeSource(spawned);
                _pty = null;
                if (spawned is not null)
                {
                    try { await spawned.DisposeAsync().ConfigureAwait(false); }
                    catch { /* preserve the startup exception */ }
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_pty is null)
            throw new InvalidOperationException("Pane not started.");

        var input = _pty.StandardInput;
        var offset = 0;
        try
        {
            while (offset < data.Length)
            {
                var remaining = data[offset..];
                AttachPathTrace.RecordInput(
                    AttachPathTrace.StagePtyWriteAttempt,
                    Id.Value,
                    remaining.Span);
                var accepted = input is IPtyInputProgress progressive
                    ? await progressive.WriteAcceptedAsync(remaining, ct).ConfigureAwait(false)
                    : await WriteOrdinaryStreamAsync(input, remaining, ct).ConfigureAwait(false);
                if (accepted <= 0 || accepted > remaining.Length)
                {
                    throw new IOException(
                        $"PTY input made invalid progress ({accepted} of {remaining.Length} bytes).");
                }

                offset += accepted;
            }

            // The completion boundary is the flush. Recording it inside the
            // loop claimed delivery for bytes whose flush later failed.
            await input.FlushAsync(ct).ConfigureAwait(false);
            if (offset > 0)
            {
                AttachPathTrace.RecordInput(
                    AttachPathTrace.StagePtyWriteCompletion,
                    Id.Value,
                    data.Span[..offset]);
            }
        }
        catch
        {
            // The attempt remains useful evidence. A failed or cancelled write
            // has no completion record: nothing reached the PTY boundary.
            throw;
        }
    }

    private static async ValueTask<int> WriteOrdinaryStreamAsync(
        Stream input,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        await input.WriteAsync(data, ct).ConfigureAwait(false);
        return data.Length;
    }

    public ValueTask WriteTextAsync(string text, CancellationToken ct)
    {
        _intelligence?.OnPaneInput(Id, text);
        var bytes = Encoding.UTF8.GetBytes(text);
        return WriteAsync(bytes, ct);
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        // The control plane can run a resize outside its binding gate, at
        // the same time as a close. DisposeAsync sets _disposed under this
        // lock, so a resize never reaches a disposed VT. The PTY ignores a
        // resize after its own dispose.
        IPtyProcess? pty;
        lock (_vtIoGate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            FlushHistoryOpenUnlocked();
            _vt.Resize(cols, rows);
            if (_historyStore is not null)
                _historyStore.SealOpenBlock();
            _historyOpenCols = cols;
            pty = _pty;
        }

        pty?.Resize(cols, rows);
        return ValueTask.CompletedTask;
    }

    public string ReadVisibleText()
    {
        lock (_vtIoGate)
        {
            // Ghostty viewport is already at origin after TrySetScrollOrigin.
            if (_deepScrollActive && _historyStore is not null)
                return FormatDeepScrollTextUnlocked(_vt.Rows);
            if (_vt is GhosttyVtEngine)
                return _vt.GetVisibleText();
            var maxOffset = MeasureMaxScrollOffsetUnlocked();
            var offset = Math.Clamp(_scrollOffset, 0, maxOffset);
            if (offset == 0)
                return _vt.GetVisibleText();
            return VisibleWindowFromRecentUnlocked(offset, _vt.Rows);
        }
    }

    public string ReadRecentText(int maxLines)
    {
        lock (_vtIoGate)
            return ReadRecentTextUnlocked(maxLines);
    }

    public string ReadRecentUnwrappedText(int maxLines) => _vt.GetRecentUnwrappedText(maxLines);

    public string? SnapshotHistory()
    {
        lock (_vtIoGate)
        {
            var ansi = _vt.GetRecentUnwrappedAnsi(int.MaxValue);
            return string.IsNullOrWhiteSpace(ansi) ? null : ansi;
        }
    }

    public void SeedHistoryAnsi(string ansi)
    {
        if (string.IsNullOrEmpty(ansi))
            return;
        lock (_vtIoGate)
            SeedHistoryAnsiUnlocked(ansi);
    }

    private void SeedHistoryAnsiUnlocked(string ansi)
    {
        _vt.Feed(Encoding.UTF8.GetBytes(ansi));
    }

    public string ReadDetectionText() => _vt.GetRecentText(80);

    public string? ReadWorkingDirectory()
    {
        string? reported;
        lock (_vtIoGate)
            reported = _osc.Agent.WorkingDirectory;
        return PaneWorkingDirectory.Validate(reported, Directory.Exists)
            ?? (IsAlive && ReadShellPid() is { } pid ? ProcessInfoProbe?.TryGetWorkingDirectory(pid) : null);
    }

    public string ReadDetectionOscTitle()
    {
        lock (_vtIoGate)
            return _osc.Agent.LatestTitle;
    }

    public string ReadDetectionOscProgress()
    {
        lock (_vtIoGate)
            return _osc.Agent.LatestProgress;
    }

    public void ClearAgentOscState()
    {
        lock (_vtIoGate)
            _osc.Agent.ClearRetained();
    }

    /// <summary>
    /// Structured VT grid snapshot via the pane's engine.
    /// Not on <see cref="IPaneRuntime"/> — keeps Terminal types off the Application port.
    /// </summary>
    public VtStructuredSnapshot CaptureVtSnapshot()
    {
        lock (_vtIoGate)
            return SnapshotHonouringOriginUnlocked();
    }

    /// <inheritdoc />
    public long FeedGeneration
    {
        get
        {
            lock (_vtIoGate)
                return _feedGeneration;
        }
    }

    /// <inheritdoc />
    public PaneFeedPaintDecision LastFeedPaintDecision
    {
        get
        {
            lock (_vtIoGate)
                return _lastFeedPaintDecision;
        }
    }

    /// <inheritdoc />
    public bool IsSynchronizedOutputActive
    {
        get
        {
            if (_disposed)
                return false;
            try
            {
                lock (_vtIoGate)
                    return _vt.IsSynchronizedOutputActive;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool TryEndSynchronizedOutput(long expectedFeedGeneration, out long feedGeneration)
    {
        feedGeneration = 0;
        if (_disposed)
            return false;
        try
        {
            lock (_vtIoGate)
            {
                feedGeneration = _feedGeneration;
                if (_feedGeneration != expectedFeedGeneration || !_vt.TryEndSynchronizedOutput())
                    return false;

                // The VT state changed with no feed, as for a host theme
                // change. A new generation lets the next paint post it.
                _feedGeneration++;
                feedGeneration = _feedGeneration;
                _lastFeedPaintDecision = new PaneFeedPaintDecision(
                    _feedGeneration,
                    RequestPaint: !_vt.IsSynchronizedOutputActive);
                return true;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
    {
        json = "";
        feedGeneration = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                if (!TryPaintSnapshotHonouringOriginUnlocked(attachRoute: false, out var snap))
                    return false;
                json = VtSnapshotNormalizer.ToCompactJson(snap);
                feedGeneration = _feedGeneration;
            }

            return !string.IsNullOrEmpty(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT snapshot capture failed for {PaneId}", Id);
            json = "";
            feedGeneration = 0;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryCaptureLivePaintJson(
        out string json,
        out long feedGeneration,
        out PaneVtPaintKind kind,
        out IReadOnlyList<int>? dirtyRows)
    {
        json = "";
        feedGeneration = 0;
        kind = PaneVtPaintKind.Full;
        dirtyRows = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            VtStructuredSnapshot? snap = null;
            lock (_vtIoGate)
            {
                feedGeneration = _feedGeneration;
                if (_vt is GhosttyVtEngine ghostty)
                {
                    TrySyncGhosttyScrollUnlocked(ghostty, out _, out _);
                    if (_deepScrollActive)
                    {
                        _liveStampMeter.RecordFallback(LiveStampFallbackReason.DeepScroll);
                        kind = PaneVtPaintKind.Full;
                        dirtyRows = null;
                        return false;
                    }

                    if (_scrollOffset != 0)
                    {
                        _liveStampMeter.RecordFallback(LiveStampFallbackReason.ScrollOffset);
                        kind = PaneVtPaintKind.Full;
                        dirtyRows = null;
                        if (!TryCaptureGhosttyPaintUnlocked(ghostty, out snap))
                            return false;
                    }
                    else if (!ghostty.TryCaptureLivePaint(out snap, out var dirty, out dirtyRows, out var renderStateUpdateOk))
                    {
                        // A failed render-state update or cell lookup posts nothing.
                        if (!renderStateUpdateOk)
                            _liveStampMeter.RecordFallback(LiveStampFallbackReason.RenderStateUpdate);
                        else if (ghostty.LastLiveCaptureLookupFailed)
                            _liveStampMeter.RecordFallback(LiveStampFallbackReason.RowGridRef);
                        return false;
                    }
                    else if (dirty == GhosttyNative.RenderStateDirtyClean)
                    {
                        // No cell changed, but the cursor can have moved. The
                        // Clean snapshot has an empty grid, so capture the
                        // viewport for the caller to compare with the posted frame.
                        kind = PaneVtPaintKind.Clean;
                        dirtyRows = null;
                        if (!TryCaptureGhosttyPaintUnlocked(ghostty, out snap))
                            return false;
                    }
                    else
                    {
                        // Partial cannot prove unmarked rows are stable (p10k
                        // shrink / LF). A bottom suffix is not a row shift.
                        // viewport. Post Full.
                        kind = PaneVtPaintKind.Full;
                        dirtyRows = null;
                        if (dirty != GhosttyNative.RenderStateDirtyFull
                            && !TryCaptureGhosttyPaintUnlocked(ghostty, out snap))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // viewport after the scroll move. Basic has no native
                    // viewport; the origin window is that capture. Never Clean
                    // for offset != 0.
                    kind = PaneVtPaintKind.Full;
                    snap = SnapshotHonouringOriginUnlocked(resolveColors: false);
                }
            }

            if (snap is null)
                return false;
            json = VtSnapshotNormalizer.ToCompactJson(snap);
            return !string.IsNullOrEmpty(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT live paint capture failed for {PaneId}", Id);
            json = "";
            feedGeneration = 0;
            kind = PaneVtPaintKind.Full;
            dirtyRows = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryCaptureLivePaintFrame(
        out VtFrame? frame,
        out long feedGeneration,
        out PaneVtPaintKind kind)
    {
        return TryCaptureLivePaintFrame(out frame, out feedGeneration, out kind, out _);
    }

    /// <inheritdoc />
    public bool TryCaptureLivePaintFrame(
        out VtFrame? frame,
        out long feedGeneration,
        out PaneVtPaintKind kind,
        out VtDirtyRowPatch? patch)
    {
        frame = null;
        feedGeneration = 0;
        kind = PaneVtPaintKind.Full;
        // The live frame is always complete. A dirty-row hint rides on its
        // grid (VtFrameGrid.Delta), so no separate patch is returned.
        patch = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_vt is GhosttyVtEngine ghostty)
            {
                lock (_liveCaptureGate)
                    return TryCaptureGhosttyLiveFrameLocked(ghostty, out frame, out feedGeneration, out kind);
            }

            AttachPathTrace.RecordPhase(
                AttachPathTrace.DomainOutput,
                AttachPathTrace.StageVtIoGate,
                Id.Value,
                bound: AttachPathTrace.BoundStart);
            lock (_vtIoGate)
            {
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtIoGate,
                    Id.Value,
                    bound: AttachPathTrace.BoundEnd);
                feedGeneration = _feedGeneration;
                // Test doubles and Basic: the origin window after a scroll
                // move. Never Clean for offset != 0.
                var snap = SnapshotHonouringOriginUnlocked(resolveColors: false);
                frame = WithTtyEchoUnlocked(StampFromSnapshotUnlocked(
                    Id.Value, snap, occupantGeneration: 0, generation: 0, _scrollOffset));
                return frame is not null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT live frame capture failed for {PaneId}", Id);
            frame = null;
            feedGeneration = 0;
            kind = PaneVtPaintKind.Full;
            patch = null;
            return false;
        }
    }

    /// <summary>
    /// Ghostty live capture. The caller holds <c>_liveCaptureGate</c>, so
    /// no other capture writes the retained grid while this one copies it.
    /// <c>_vtIoGate</c> is held only for the native reads: the render-state
    /// update, the cursor and mode read, and the stamp of the dirty rows. The
    /// grid copy, the table freeze and the check-frame compare run after the
    /// PTY reader can feed again.
    /// </summary>
    private bool TryCaptureGhosttyLiveFrameLocked(
        GhosttyVtEngine ghostty,
        out VtFrame? frame,
        out long feedGeneration,
        out PaneVtPaintKind kind)
    {
        frame = null;
        feedGeneration = 0;
        kind = PaneVtPaintKind.Full;
        var outcome = _liveOutcome;
        VtFrameCursor cursor;
        VtFrameModes modes;
        string activeScreen;
        int origin;
        int cols;
        int rows;
        var liveGrid = false;
        var reuseLastGrid = false;
        var verified = false;
        object? token = null;
        VtFrameGridDelta? delta = null;
        try
        {
            AttachPathTrace.RecordPhase(
                AttachPathTrace.DomainOutput,
                AttachPathTrace.StageVtIoGate,
                Id.Value,
                bound: AttachPathTrace.BoundStart);
            lock (_vtIoGate)
            {
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtIoGate,
                    Id.Value,
                    bound: AttachPathTrace.BoundEnd);
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtIoGateHold,
                    Id.Value,
                    bound: AttachPathTrace.BoundStart);
                var visited = 0;
                try
                {
                    feedGeneration = _feedGeneration;
                    TrySyncGhosttyScrollUnlocked(ghostty, out _, out _);
                    cols = ghostty.Cols;
                    rows = ghostty.Rows;
                    EnsureLiveGridUnlocked(cols, rows);
                    if (_deepScrollActive)
                    {
                        _liveStampMeter.RecordFallback(LiveStampFallbackReason.DeepScroll);
                        InvalidateLiveGridUnlocked(LiveStampFallbackReason.ScrollOffsetChange);
                        if (!TryStampDeepScrollUnlocked(ghostty, out cursor, out modes, out activeScreen))
                            return false;
                        origin = _deepScrollOffset;
                        visited = cols * rows;
                    }
                    else if (_scrollOffset != 0)
                    {
                        _liveStampMeter.RecordFallback(LiveStampFallbackReason.ScrollOffset);
                        InvalidateLiveGridUnlocked(LiveStampFallbackReason.ScrollOffsetChange);
                        if (!ghostty.TryStampPaintSnapshot(
                                _liveCells,
                                _liveTables,
                                out cursor,
                                out modes,
                                out activeScreen))
                        {
                            return false;
                        }

                        origin = _scrollOffset;
                        visited = cols * rows;
                    }
                    else
                    {
                        var paneReason = LiveStampFallbackReason.None;
                        if (_liveRetained is null)
                            paneReason = _liveInvalidReason;
                        else if (_liveTables.Styles.Count > MaxRetainedStyles
                                 || _liveTables.Glyphs.ExtraCount > MaxRetainedGlyphExtras)
                            paneReason = LiveStampFallbackReason.TableLimit;
                        var verify = paneReason == LiveStampFallbackReason.None
                            && _dirtyStampsSinceVerify + 1 >= LiveStampVerifyInterval;
                        if (!ghostty.TryStampLivePaintRows(
                                _liveCells,
                                _liveTables,
                                _liveRetained,
                                paneReason,
                                verify ? _verifyCells : null,
                                verify ? _verifyTables : null,
                                outcome))
                        {
                            // A failed render-state update posts nothing: its
                            // styles would come from an old state.
                            if (outcome.Reason != LiveStampFallbackReason.None)
                                _liveStampMeter.RecordFallback(outcome.Reason);
                            InvalidateLiveGridUnlocked(LiveStampFallbackReason.NoRetainedGrid);
                            return false;
                        }

                        cursor = outcome.Cursor;
                        modes = outcome.Modes;
                        activeScreen = outcome.ActiveScreen;
                        origin = _scrollOffset;
                        visited = outcome.CellsVisited;
                        if (!outcome.Stamped)
                        {
                            // Ghostty mouse DECSET does not dirty cells. Keep a
                            // modes-only frame so a token change can pack.
                            kind = PaneVtPaintKind.Clean;
                            reuseLastGrid = _lastLiveGrid is { } last && last.Cols == cols && last.Rows == rows;
                        }
                        else
                        {
                            liveGrid = true;
                            _liveRetained = outcome.Base;
                            _liveInvalidReason = LiveStampFallbackReason.None;
                            token = new object();
                            if (outcome.StampedFull)
                            {
                                _liveStampMeter.RecordFallback(outcome.Reason);
                                _dirtyStampsSinceVerify = 0;
                                // A full stamp can change rows that no dirty
                                // flag marks (a colour change). No later hint
                                // may name a grid from before it.
                                _committedLiveToken = null;
                            }
                            else if (outcome.Verified)
                            {
                                verified = true;
                                _dirtyStampsSinceVerify = 0;
                                _committedLiveToken = null;
                            }
                            else
                            {
                                _liveStampMeter.RecordDirtyRowStamp(outcome.DirtyRows.Count, outcome.CellsVisited);
                                _dirtyStampsSinceVerify++;
                                // The unmarked rows equal the grid that the last
                                // owned commit posted. The packer may then diff
                                // only the dirty rows against that grid.
                                if (_committedLiveToken is { } committed)
                                    delta = new VtFrameGridDelta(committed, [.. outcome.DirtyRows]);
                            }
                        }
                    }

                    // The echo flag is read with the cells, under the same lock.
                    modes = modes with { Echo = _ttyMode?.Echo, Canonical = _ttyMode?.Canonical };
                    if (!reuseLastGrid)
                        _lastLiveToken = token;
                    LastLiveStampCellsVisited = visited;
                }
                finally
                {
                    AttachPathTrace.RecordPhase(
                        AttachPathTrace.DomainOutput,
                        AttachPathTrace.StageVtIoGateHold,
                        Id.Value,
                        visited,
                        bound: AttachPathTrace.BoundEnd);
                }
            }

            if (verified)
                ApplyVerifyFrameLocked(cols, rows);

            VtFrameGrid grid;
            if (reuseLastGrid)
                grid = _lastLiveGrid!;
            else
            {
                var copy = new VtFrameCell[cols * rows];
                Array.Copy(_liveCells, copy, copy.Length);
                _lastTables = _liveTables.Freeze(_lastTables);
                grid = new VtFrameGrid(copy, _lastTables, cols, rows)
                {
                    Identity = token,
                    Delta = delta,
                };
                _lastLiveGrid = liveGrid ? grid : null;
            }

            frame = new VtFrame(
                Id.Value,
                cols,
                rows,
                grid,
                cursor,
                modes,
                origin,
                OccupantGeneration: 0,
                Generation: 0,
                GhosttyVtEngine.Provider,
                activeScreen);
            return true;
        }
        catch
        {
            // The tokens are read by the commit under _vtIoGate.
            lock (_vtIoGate)
                InvalidateLiveGridUnlocked(LiveStampFallbackReason.NoRetainedGrid);
            _lastLiveGrid = null;
            throw;
        }
    }

    /// <summary>
    /// Check frame. Compare the dirty-row result with the full stamp of the
    /// same capture, log each row that differs, then keep the full stamp.
    /// The caller holds <c>_liveCaptureGate</c>.
    /// </summary>
    private void ApplyVerifyFrameLocked(int cols, int rows)
    {
        var candidate = _liveTables.Freeze(null);
        var full = _verifyTables.Freeze(null);
        List<int>? mismatched = null;
        for (var r = 0; r < rows; r++)
        {
            var start = r * cols;
            for (var c = 0; c < cols; c++)
            {
                if (candidate.Resolve(in _liveCells[start + c]).VisuallyEquals(full.Resolve(in _verifyCells[start + c])))
                    continue;
                (mismatched ??= []).Add(r);
                break;
            }
        }

        if (mismatched is not null)
        {
            _liveStampMeter.RecordVerifyMismatch(mismatched.Count);
            _logger.LogWarning(
                "Live stamp check frame found {Count} stale rows for {PaneId}: {Rows}",
                mismatched.Count,
                Id,
                string.Join(",", mismatched));
        }

        _liveStampMeter.RecordFallback(LiveStampFallbackReason.VerifyFrame);
        (_liveCells, _verifyCells) = (_verifyCells, _liveCells);
        (_liveTables, _verifyTables) = (_verifyTables, _liveTables);
    }

    /// <summary>
    /// Mark the retained live grid as not usable. The next live capture
    /// stamps the full viewport and counts <paramref name="reason"/>.
    /// </summary>
    private void InvalidateLiveGridUnlocked(LiveStampFallbackReason reason)
    {
        _liveRetained = null;
        _liveInvalidReason = reason;
        _lastLiveToken = null;
        _committedLiveToken = null;
    }

    /// <inheritdoc />
    public bool TryCaptureAttachFrame(out VtFrame? frame, out long feedGeneration) =>
        TryCaptureAttachFrame(out frame, out feedGeneration, out _);

    /// <inheritdoc />
    public bool TryCaptureAttachFrame(out VtFrame? frame, out long feedGeneration, out bool captureFault)
    {
        captureFault = false;
        frame = null;
        feedGeneration = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                frame = CaptureAttachSnapshotUnlocked(out _, out feedGeneration, out captureFault);
                return frame is not null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT attach frame capture failed for {PaneId}", Id);
            frame = null;
            feedGeneration = 0;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryCaptureAttachSnapshot(out VtAttachSnapshot? snapshot, out long feedGeneration)
    {
        snapshot = null;
        feedGeneration = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                var frame = CaptureAttachSnapshotUnlocked(out var scrollRegion, out feedGeneration, out _);
                if (frame is null)
                    return false;
                snapshot = new VtAttachSnapshot(
                    frame,
                    new Hypa.AgentRuntime.Application.VtScrollRegion(scrollRegion.Top, scrollRegion.Bottom),
                    VtSnapshotNormalizer.SchemaVersion);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT attach snapshot capture failed for {PaneId}", Id);
            snapshot = null;
            feedGeneration = 0;
            return false;
        }
    }

    /// <summary>
    // / Shared attach capture.
    /// <c>src/server/render_stream.rs:138-143</c> moves the typed frame
    /// into one message. Syncs the Ghostty scroll origin, reads the
    /// viewport snapshot, applies the scalar normalize rules without a
    /// second grid copy, and stamps one frame. Caller holds
    /// <c>_vtIoGate</c>. Null with <paramref name="captureFault"/> set when
    /// the capture failed closed: the caller retries after a render tick.
    /// </summary>
    private VtFrame? CaptureAttachSnapshotUnlocked(
        out VtScrollRegionSnapshot scrollRegion,
        out long feedGeneration,
        out bool captureFault)
    {
        scrollRegion = default!;
        feedGeneration = _feedGeneration;
        captureFault = false;
        if (!TryPaintSnapshotHonouringOriginUnlocked(attachRoute: true, out var snap))
        {
            captureFault = true;
            return null;
        }

        // Scalar rules from VtSnapshotNormalizer.cs:35-46 without the
        // second jagged grid at :53-66.
        var cols = Math.Max(1, snap.Cols);
        var rows = Math.Max(1, snap.Rows);
        if (snap.Cells is { Length: > 0 })
        {
            rows = Math.Max(rows, snap.Cells.Length);
            for (var r = 0; r < snap.Cells.Length; r++)
            {
                var row = snap.Cells[r];
                if (row is not null)
                    cols = Math.Max(cols, row.Length);
            }
        }

        BasicVtFloor.EnsureValidDimensions(cols, rows);

        VtCursorSnapshot? cursor = null;
        if (snap.Cursor is { } srcCursor)
        {
            cursor = new VtCursorSnapshot
            {
                Col = Math.Clamp(srcCursor.Col, 0, cols - 1),
                Row = Math.Clamp(srcCursor.Row, 0, rows - 1),
                Visible = srcCursor.Visible,
                Shape = Math.Clamp(srcCursor.Shape, 0, 6),
            };
        }

        // VtSnapshotNormalizer.cs:82-85.
        var top = Math.Clamp(snap.ScrollRegion?.Top ?? 0, 0, rows - 1);
        var bottom = Math.Clamp(snap.ScrollRegion?.Bottom ?? rows - 1, top, rows - 1);
        scrollRegion = new VtScrollRegionSnapshot { Top = top, Bottom = bottom };

        var modes = snap.Modes ?? VtModesSnapshot.BasicDefaults;
        var mouse = string.IsNullOrEmpty(modes.Mouse) ? "none" : modes.Mouse;

        var activeScreen = snap.ActiveScreen;
        if (activeScreen is not ("main" or "alt"))
            activeScreen = "main";

        var provider = string.IsNullOrEmpty(snap.Provider)
            ? GhosttyVtEngine.Provider
            : snap.Provider;

        var normalized = snap with
        {
            Cols = cols,
            Rows = rows,
            Cursor = cursor,
            ActiveScreen = activeScreen,
            ScrollRegion = scrollRegion,
            Provider = provider,
            Modes = new VtModesSnapshot
            {
                Origin = modes.Origin,
                AutoWrap = modes.AutoWrap,
                Insert = modes.Insert,
                BracketedPaste = modes.BracketedPaste,
                Mouse = mouse,
                MouseEncoding = modes.MouseEncoding,
                FocusReporting = modes.FocusReporting,
                Sync = modes.Sync,
                ApplicationCursor = modes.ApplicationCursor,
            },
        };
        return WithTtyEchoUnlocked(StampFromSnapshotUnlocked(
            Id.Value, normalized, occupantGeneration: 0, generation: 0, _scrollOffset));
    }

    /// <summary>Puts the termios mode of the fed output on a captured frame. The caller holds <c>_vtIoGate</c>.</summary>
    private VtFrame WithTtyEchoUnlocked(VtFrame frame) =>
        _ttyMode is { } mode
            ? frame with { Modes = frame.Modes with { Echo = mode.Echo, Canonical = mode.Canonical } }
            : frame;

    /// <summary>The live stamp counters of this pane.</summary>
    public ILiveStampMeter LiveStampMeter => _liveStampMeter;

    /// <summary>A check frame runs after this many dirty-row stamps.</summary>
    internal int LiveStampVerifyInterval { get; set; } = DefaultLiveStampVerifyInterval;

    /// <summary>The retained style table limit. A larger table forces a full stamp.</summary>
    internal int MaxRetainedStyles { get; set; } = 512;

    /// <summary>The retained glyph table limit. A larger table forces a full stamp.</summary>
    internal int MaxRetainedGlyphExtras { get; set; } = 2048;

    /// <summary>Test seam: the cells that the last live capture read.</summary>
    internal int LastLiveStampCellsVisited { get; private set; }

    internal const int DefaultLiveStampVerifyInterval = 32;

    internal static VtFrame ToVtFrame(
        string paneId,
        VtStructuredSnapshot snap,
        int occupantGeneration,
        long generation,
        int viewportOrigin)
    {
        var tables = new VtStampTables();
        var cols = Math.Max(1, snap.Cols);
        var rows = Math.Max(1, snap.Rows);
        var cells = new VtFrameCell[cols * rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var row = r < snap.Cells.Length ? snap.Cells[r] : null;
                var src = row is not null && c < row.Length ? row[c] : VtCellSnapshot.Empty;
                cells[(r * cols) + c] = ToVtFrameCell(src, tables);
            }
        }

        return new VtFrame(
            paneId,
            snap.Cols,
            snap.Rows,
            new VtFrameGrid(cells, tables.Freeze(null), cols, rows),
            CursorOf(snap),
            ModesOf(snap),
            viewportOrigin,
            occupantGeneration,
            generation,
            snap.Provider,
            snap.ActiveScreen);
    }

    private void EnsureLiveGridUnlocked(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (_liveCols == cols && _liveRowCount == rows && _liveCells.Length == cols * rows)
            return;

        var resized = _liveCols != 0;
        _liveCols = cols;
        _liveRowCount = rows;
        _liveCells = new VtFrameCell[cols * rows];
        _verifyCells = new VtFrameCell[cols * rows];
        _liveRetained = null;
        _liveInvalidReason = resized
            ? LiveStampFallbackReason.Resize
            : LiveStampFallbackReason.NoRetainedGrid;
        _lastLiveGrid = null;
    }

    /// <summary>
    /// Stamp a structured snapshot into a new frame. Uses its own buffer and
    /// tables, so it never changes the retained live grid.
    /// </summary>
    private static VtFrame StampFromSnapshotUnlocked(
        string paneId,
        VtStructuredSnapshot snap,
        int occupantGeneration,
        long generation,
        int viewportOrigin) =>
        ToVtFrame(paneId, snap, occupantGeneration, generation, viewportOrigin);

    private static VtFrameCell ToVtFrameCell(VtCellSnapshot src, VtStampTables? tables)
    {
        var st = src.Style;
        var packed = new VtPackedStyle(
            VtColorPack.Parse(st.Fg),
            VtColorPack.Parse(st.Bg),
            VtColorPack.Parse(st.UnderlineColor),
            VtStyleBits.Pack(
                st.Bold,
                st.Dim,
                st.Italic,
                st.Underline,
                st.Inverse,
                st.Invisible,
                st.Strikethrough,
                st.Blink,
                st.Overline),
            (byte)Math.Clamp(st.UnderlineStyle, 0, 255));
        var text = VtGlyphIntern.Intern(string.IsNullOrEmpty(src.Text) ? " " : src.Text);
        var width = src.Width < 1 ? 1 : src.Width;
        if (tables is null)
            return VtFrameCell.Pack(VtGlyphIntern.AsciiIndex(text) < 0 ? 32 : VtGlyphIntern.AsciiIndex(text), 0, -1, width, src.IsContinuation);
        return tables.PackCell(text, width, src.IsContinuation, in packed, hyperlink: null);
    }

    private static VtFrameCursor CursorOf(VtStructuredSnapshot snap) =>
        snap.Cursor is { } cursor
            ? new VtFrameCursor(cursor.Col, cursor.Row, cursor.Visible, cursor.Shape, HasCursor: true)
            : VtFrameCursor.None;

    private static VtFrameModes ModesOf(VtStructuredSnapshot snap) =>
        new(
            snap.Modes.Origin,
            snap.Modes.AutoWrap,
            snap.Modes.Insert,
            snap.Modes.BracketedPaste,
            snap.Modes.Mouse,
            snap.Modes.FocusReporting,
            snap.Modes.Sync,
            snap.Modes.MouseEncoding,
            snap.Modes.ApplicationCursor);

    /// <inheritdoc />
    public void CommitPostedPaint()
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                if (_vt is GhosttyVtEngine ghostty)
                {
                    // An owned commit clears only flags that the last live
                    // grid holds. That grid is then the base of the next
                    // dirty-row hint.
                    var owned = ghostty.CommitPostedLivePaint();
                    _committedLiveToken = owned ? _lastLiveToken : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT paint commit failed for {PaneId}", Id);
        }
    }

    /// <inheritdoc />
    public bool TryGetScrollMetrics(out int offset, out int maxOffset)
    {
        offset = 0;
        maxOffset = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                if (_vt is GhosttyVtEngine ghostty)
                {
                    if (!TrySyncGhosttyScrollUnlocked(ghostty, out offset, out maxOffset))
                        return false;
                    maxOffset += Math.Max(0, _committedStoreRows);
                    if (_deepScrollActive)
                        offset = _deepScrollOffset;
                    return true;
                }

                maxOffset = MeasureMaxScrollOffsetUnlocked();
                offset = Math.Clamp(_scrollOffset, 0, maxOffset);
                return true;
            }
        }
        catch
        {
            offset = 0;
            maxOffset = 0;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// stored-origin contract. PTY origin-only must not poll
    // / Ghostty scrollbar id 9.
    /// once per frame, not per write.
    /// </remarks>
    public bool TryGetScrollOrigin(out int offset)
    {
        offset = 0;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                offset = _deepScrollActive ? _deepScrollOffset : _scrollOffset;
                return true;
            }
        }
        catch
        {
            offset = 0;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TrySetScrollOrigin(int offset)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_vtIoGate)
            {
                if (_vt is GhosttyVtEngine ghostty)
                {
                    var hasMetrics = TrySyncGhosttyScrollUnlocked(ghostty, out _, out var ghosttyMax);
                    if (!hasMetrics)
                        ghosttyMax = 0;
                    if (_committedStoreRows > 0)
                    {
                        var totalMax = ghosttyMax + _committedStoreRows;
                        var clamped = Math.Clamp(offset, 0, totalMax);
                        if (clamped > ghosttyMax)
                        {
                            // Store answers only below the Ghostty range.
                            // Do not move the live Ghostty viewport.
                            _deepScrollActive = true;
                            _deepScrollOffset = clamped;
                            _scrollOffset = clamped;
                            return true;
                        }

                        offset = clamped;
                    }

                    _deepScrollActive = false;
                    _deepScrollOffset = 0;
                    ghostty.SetScrollOffsetFromBottom(offset);
                    // poll scrollbar after the viewport move. Do not keep
                    // the clamped request as origin truth.
                    if (!TrySyncGhosttyScrollUnlocked(ghostty, out _, out _))
                        _scrollOffset = Math.Max(0, offset);
                    return true;
                }

                var maxOffset = MeasureMaxScrollOffsetUnlocked();
                _scrollOffset = Math.Clamp(offset, 0, maxOffset);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>bindings.rs:2523-2526</c>: no scroll change notification; poll
    /// scrollbar once per frame. Metrics, set, and capture poll. Origin-only
    /// does not.
    /// </summary>
    private bool TrySyncGhosttyScrollUnlocked(
        GhosttyVtEngine ghostty,
        out int offset,
        out int maxOffset)
    {
        if (ghostty.TryGetScrollMetrics(out offset, out maxOffset))
        {
            _scrollOffset = offset;
            return true;
        }

        offset = _scrollOffset;
        maxOffset = 0;
        return false;
    }

    private int MeasureMaxScrollOffsetUnlocked()
    {
        if (_vt is GhosttyVtEngine ghostty)
        {
            if (TrySyncGhosttyScrollUnlocked(ghostty, out _, out var maxOffset))
                return maxOffset;
            return 0;
        }

        var recent = _vt.GetRecentText(10_000);
        var lines = CountTextLines(recent);
        return Math.Max(0, lines - _vt.Rows);
    }

    private string VisibleWindowFromRecentUnlocked(int offset, int rows)
    {
        var recent = _vt.GetRecentText(10_000);
        if (string.IsNullOrEmpty(recent))
            return _vt.GetVisibleText();
        var lines = recent.Split('\n');
        var end = Math.Max(0, lines.Length - offset);
        var start = Math.Max(0, end - Math.Max(1, rows));
        var count = Math.Max(0, end - start);
        return count == 0 ? "" : string.Join('\n', lines.AsSpan(start, count).ToArray());
    }

    private VtStructuredSnapshot SnapshotHonouringOriginUnlocked() =>
        SnapshotHonouringOriginUnlocked(resolveColors: false);

    /// <summary>
    /// Predicate only. A complete bottom suffix is the common LF / prompt-shrink
    /// pattern, not proof that unmarked rows are stable. Live capture must not
    /// Patch on this result.
    /// </summary>
    internal static bool DirtyRowsCoverCompleteSuffix(IReadOnlyList<int> dirtyRows, int rows)
    {
        if (dirtyRows.Count == 0 || rows <= 0)
            return false;
        var set = new HashSet<int>();
        var min = int.MaxValue;
        foreach (var r in dirtyRows)
        {
            if ((uint)r >= (uint)rows)
                return false;
            if (!set.Add(r))
                continue;
            if (r < min)
                min = r;
        }

        if (min == int.MaxValue || set.Count != rows - min)
            return false;
        for (var r = min; r < rows; r++)
        {
            if (!set.Contains(r))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Test seam: feed the pane VT and drain history without a PTY.
    /// Capture does not depend on a live observer.
    /// </summary>
    internal void FeedVtForTests(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_vtIoGate)
        {
            _vt.Feed(data);
            _ = _osc.Observe(data);
            _ = _vt.TakePendingBellCount();
            _feedGeneration++;
            var drainStart = Stopwatch.GetTimestamp();
            DrainHistoryUnlocked();
            HistoryDrainTimeForTests += Stopwatch.GetElapsedTime(drainStart);
            _lastFeedPaintDecision = new PaneFeedPaintDecision(
                _feedGeneration,
                RequestPaint: !_vt.IsSynchronizedOutputActive);
        }
    }

    /// <summary>Test seam: the total time that <see cref="FeedVtForTests(ReadOnlySpan{byte})"/> spent in the history copy.</summary>
    internal TimeSpan HistoryDrainTimeForTests { get; private set; }

    internal void FeedVtForTests(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        FeedVtForTests(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Feed the pane VT, then raise <see cref="OutputReceived"/> so a test
    /// runs the live paint path with the feed-bound paint decision.
    /// </summary>
    internal void PublishFedOutputForTests(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PublishFedOutputForTests(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Byte form of <see cref="PublishFedOutputForTests(string)"/>. A recorded
    /// read can end inside a UTF-8 sequence or an escape sequence.
    /// </summary>
    internal void PublishFedOutputForTests(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        FeedVtForTests(data);
        var handlers = Volatile.Read(ref _outputHandlers);
        if (handlers.Length > 0)
            InvokeOutputReceived(data, handlers);
    }

    private string ReadRecentTextUnlocked(int maxLines)
    {
        var ghostty = _vt.GetRecentText(maxLines);
        if (_historyStore is null || _committedStoreRows <= 0 || maxLines < 1)
            return ghostty;

        var ghosttyLines = CountTextLines(ghostty);
        if (ghosttyLines >= maxLines)
            return ghostty;

        var needed = Math.Min(maxLines - ghosttyLines, _committedStoreRows);
        if (needed <= 0)
            return ghostty;
        var start = Math.Max(_historyStore.FirstRowIndex, _historyStore.FirstRowIndex + _committedStoreRows - needed);
        var read = _historyStore.TryReadRows(start, needed);
        if (!read.IsOk || read.Value.Rows.Count == 0)
            return ghostty;

        var storeText = FormatHistoryRead(read.Value);
        if (string.IsNullOrEmpty(storeText))
            return ghostty;
        if (string.IsNullOrEmpty(ghostty))
            return storeText;
        return storeText + "\n" + ghostty;
    }

    /// <summary>
    // / Copy rows that are about to leave Ghostty.
    /// scrollbar after write
    /// (<c>src/pane/terminal/windows_recent_fallback.rs:24-30</c>)
    /// and pins with <c>track_row</c>
    /// (<c>src/ghostty/mod.rs:1088-1101</c>). Drain only on the
    /// primary screen.
    /// </summary>
    private void DrainHistoryUnlocked()
    {
        if (_historyStore is null || _vt is not GhosttyVtEngine ghostty)
            return;
        if (_nativeWindowBytes <= 0 || _nativeWindowBytes >= _scrollbackLimitBytes)
            return;
        if (!ghostty.IsPrimaryScreenActive)
            return;
        if (!ghostty.TryGetScrollbackExtent(out var totalRows, out var viewRows) || totalRows <= viewRows)
            return;

        // Copy every off-screen row.
        // side cache (windows_recent_fallback.rs:35-41). Copying as rows
        // leave the viewport means one later prune cannot drop an
        // uncopied row.
        var copyEnd = totalRows - viewRows;
        if (copyEnd <= 0)
        {
            RefreshCommittedStoreRowsUnlocked(ghostty);
            return;
        }

        var start = 0;
        if (ghostty.TryGetPinnedScreenRow(out var pinned))
            start = pinned + 1;
        else if (_historyStore.RowCount > 0)
        {
            _historyGapCount++;
            start = 0;
        }

        if (start < 0)
            start = 0;
        if (start >= copyEnd)
        {
            RefreshCommittedStoreRowsUnlocked(ghostty);
            return;
        }

        if (!ghostty.TryStampScreenRows(start, copyEnd - start, _historyTables, _historyRows, out var stamped) || stamped == 0)
        {
            // A failed batch keeps no style or glyph entry for the retry.
            TrimHistoryRowBuffers();
            _historyTables.BeginFrame();
            RefreshCommittedStoreRowsUnlocked(ghostty);
            return;
        }

        if (_historyOpenCols != 0 && _historyOpenCols != ghostty.Cols)
            _historyStore.SealOpenBlock();
        _historyOpenCols = ghostty.Cols;
        // Equal tables give the same frozen object, so the rows of this drain
        // join the open block of the store. A new object sealed a block on
        // each drain.
        var frozen = _historyTables.Freeze(_historyFrozen);
        _historyFrozen = frozen;
        _historyStore.AppendRows(CollectionsMarshal.AsSpan(_historyRows)[..stamped], ghostty.Cols, frozen);
        TrimHistoryRowBuffers();
        _historyTables.BeginFrame();

        var lastCopied = copyEnd - 1;
        if (!ghostty.TryPinScreenRow((uint)lastCopied))
            ghostty.ClearPinnedRow();
        RefreshCommittedStoreRowsUnlocked(ghostty);
    }

    // A recovery drain can copy the whole off-screen extent at once. Keep
    // only the buffers of a normal drain, also after a failed copy.
    private void TrimHistoryRowBuffers()
    {
        if (_historyRows.Count > MaxKeptHistoryRowBuffers)
            _historyRows.RemoveRange(MaxKeptHistoryRowBuffers, _historyRows.Count - MaxKeptHistoryRowBuffers);
    }

    private void RefreshCommittedStoreRowsUnlocked(GhosttyVtEngine ghostty)
    {
        if (_historyStore is null)
        {
            _committedStoreRows = 0;
            return;
        }

        var stored = _historyStore.RowCount;
        if (stored <= 0)
        {
            _committedStoreRows = 0;
            return;
        }

        if (ghostty.TryGetScrollbackRowCount(out var scrollback) && scrollback > 0)
        {
            _committedStoreRows = Math.Max(0, stored - scrollback);
            return;
        }

        if (ghostty.TryGetPinnedScreenRow(out var pinned) && pinned >= 0)
        {
            _committedStoreRows = Math.Max(0, stored - (pinned + 1));
            return;
        }

        _committedStoreRows = stored;
    }

    private void FlushHistoryOpenUnlocked()
    {
        _historyOpenRows.Clear();
        _historyTables.BeginFrame();
        _historyStore?.SealOpenBlock();
    }

    private void ReleaseHistoryStore()
    {
        if (_historyStoreReleased)
            return;
        _historyStoreReleased = true;
        try
        {
            lock (_vtIoGate)
                FlushHistoryOpenUnlocked();
        }
        catch
        {
            // best-effort; pane is already closed
        }

        try { _historyStore?.Dispose(); }
        catch { /* best-effort; pane is already closed */ }
    }

    private bool TryStampDeepScrollUnlocked(
        GhosttyVtEngine ghostty,
        out VtFrameCursor cursor,
        out VtFrameModes modes,
        out string activeScreen)
    {
        cursor = default;
        modes = default;
        activeScreen = "main";
        if (_historyStore is null || _committedStoreRows <= 0)
            return false;
        if (!ghostty.TryReadPaintChrome(out cursor, out modes, out activeScreen))
            return false;

        var cols = ghostty.Cols;
        var rows = ghostty.Rows;
        EnsureLiveGridUnlocked(cols, rows);
        _liveTables.BeginFrame();
        var lines = CollectDeepScrollRowsUnlocked(rows);
        for (var r = 0; r < rows; r++)
        {
            var src = r < lines.Count ? lines[r] : null;
            for (var c = 0; c < cols; c++)
            {
                if (src is null || c >= src.Cells.Length)
                {
                    _liveCells[(r * cols) + c] = VtFrameCell.Blank;
                    continue;
                }

                var view = src.Tables.Resolve(in src.Cells[c]);
                _liveCells[(r * cols) + c] = view.Pack(_liveTables);
            }
        }

        return true;
    }

    private string FormatDeepScrollTextUnlocked(int rows)
    {
        var lines = CollectDeepScrollRowsUnlocked(rows);
        var parts = new List<string>(lines.Count);
        foreach (var row in lines)
            parts.Add(FormatHistoryRow(row));
        return string.Join('\n', parts);
    }

    private List<PaneHistoryRow> CollectDeepScrollRowsUnlocked(int rows)
    {
        var result = new List<PaneHistoryRow>(Math.Max(1, rows));
        if (_historyStore is null || rows < 1 || _committedStoreRows <= 0)
            return result;

        var into = Math.Max(1, _deepScrollOffset - GhosttyMaxOffsetUnlocked());
        into = Math.Min(into, _committedStoreRows);
        var bottom = _historyStore.FirstRowIndex + _committedStoreRows - into;
        var top = bottom - rows + 1;
        if (top < _historyStore.FirstRowIndex)
            top = _historyStore.FirstRowIndex;
        if (bottom < top)
            return result;
        var count = (int)(bottom - top + 1);
        var read = _historyStore.TryReadRows(top, count);
        if (!read.IsOk)
            return result;
        result.AddRange(read.Value.Rows);
        while (result.Count < rows)
            result.Insert(0, new PaneHistoryRow(-1, _vt.Cols, new VtFrameCell[_vt.Cols], VtFrameTables.Empty));
        if (result.Count > rows)
            result.RemoveRange(0, result.Count - rows);
        return result;
    }

    private int GhosttyMaxOffsetUnlocked()
    {
        if (_vt is GhosttyVtEngine ghostty && ghostty.TryGetScrollMetrics(out _, out var maxOffset))
            return maxOffset;
        return 0;
    }

    private static string FormatHistoryRead(PaneHistoryRead read)
    {
        var parts = new string[read.Rows.Count];
        for (var i = 0; i < read.Rows.Count; i++)
            parts[i] = FormatHistoryRow(read.Rows[i]);
        return string.Join('\n', parts);
    }

    private static string FormatHistoryRow(PaneHistoryRow row)
    {
        var sb = new StringBuilder(row.Cols);
        for (var c = 0; c < row.Cells.Length; c++)
        {
            var view = row.Tables.Resolve(in row.Cells[c]);
            if (view.IsContinuation)
                continue;
            sb.Append(string.IsNullOrEmpty(view.Text) ? " " : view.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The number of attach captures in sequence that fail closed and post
    /// nothing. The capture at this count posts the lenient snapshot and
    /// counts <see cref="LiveStampFallbackReason.AttachCaptureLimit"/>, so a
    /// new attach is not left with no first frame. The mux retries a failed
    /// attach after one render tick.
    /// </summary>
    internal const int AttachCaptureFailureLimit = 4;

    /// <inheritdoc />
    public bool AttachFrameNeedsRepaint => _attachFrameDegraded;

    /// <summary>
    /// Paint snapshot at the scroll origin for the attach and JSON routes.
    /// Ghostty fails closed: a failed render-state update or cell lookup
    /// returns false and counts its live-stamp fallback reason. On the attach
    /// route, the failure at <see cref="AttachCaptureFailureLimit"/> in
    /// sequence returns the lenient snapshot. Caller holds <c>_vtIoGate</c>.
    /// </summary>
    private bool TryPaintSnapshotHonouringOriginUnlocked(
        bool attachRoute,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VtStructuredSnapshot? snapshot)
    {
        if (_vt is not GhosttyVtEngine ghostty)
        {
            snapshot = SnapshotHonouringOriginUnlocked(resolveColors: true);
            return true;
        }

        TrySyncGhosttyScrollUnlocked(ghostty, out _, out _);
        if (TryCaptureGhosttyPaintUnlocked(ghostty, out snapshot))
        {
            if (attachRoute)
            {
                _attachCaptureFailures = 0;
                _attachFrameDegraded = false;
            }

            return true;
        }

        if (!attachRoute || ++_attachCaptureFailures < AttachCaptureFailureLimit)
            return false;

        // The count starts again, so each later attach also gets a frame
        // while the fault lasts. The caller reads AttachFrameNeedsRepaint
        // and retries until a good capture replaces this frame.
        _attachCaptureFailures = 0;
        _attachFrameDegraded = true;
        _liveStampMeter.RecordFallback(LiveStampFallbackReason.AttachCaptureLimit);
        snapshot = ghostty.CapturePaintSnapshot();
        return true;
    }

    /// <summary>
    /// Ghostty paint snapshot that fails closed. A failure counts its
    /// live-stamp fallback reason. Caller holds <c>_vtIoGate</c>.
    /// </summary>
    private bool TryCaptureGhosttyPaintUnlocked(
        GhosttyVtEngine ghostty,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VtStructuredSnapshot? snapshot)
    {
        if (ghostty.TryCapturePaintSnapshot(out snapshot, out var failure))
            return true;
        _liveStampMeter.RecordFallback(failure);
        return false;
    }

    private VtStructuredSnapshot SnapshotHonouringOriginUnlocked(bool resolveColors)
    {
        if (_vt is GhosttyVtEngine ghostty)
        {
            TrySyncGhosttyScrollUnlocked(ghostty, out _, out _);
            return resolveColors ? ghostty.CapturePaintSnapshot() : ghostty.CaptureSnapshot();
        }

        var maxOffset = MeasureMaxScrollOffsetUnlocked();
        var offset = Math.Clamp(_scrollOffset, 0, maxOffset);
        var snap = _vt.CaptureSnapshot();
        if (offset == 0)
            return snap;

        return WindowRecentTextOntoSnapshot(_vt, snap, offset);
    }

    /// <summary>
    /// Stub origin window from recent text. Ghostty moves the native
    /// </summary>
    internal static VtStructuredSnapshot WindowRecentTextOntoSnapshot(
        IVtEngine vt,
        VtStructuredSnapshot snap,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(vt);
        ArgumentNullException.ThrowIfNull(snap);
        if (offset <= 0)
            return snap;

        var recent = vt.GetRecentText(10_000);
        if (string.IsNullOrEmpty(recent))
            return snap;
        var lines = recent.Split('\n');
        var end = Math.Max(0, lines.Length - offset);
        var start = Math.Max(0, end - Math.Max(1, snap.Rows));
        var count = Math.Max(0, end - start);
        var window = count == 0 ? Array.Empty<string>() : lines.AsSpan(start, count).ToArray();
        var cells = new VtCellSnapshot[snap.Rows][];
        for (var r = 0; r < snap.Rows; r++)
        {
            cells[r] = new VtCellSnapshot[snap.Cols];
            var line = r < window.Length ? window[r] : "";
            for (var c = 0; c < snap.Cols; c++)
            {
                var ch = c < line.Length ? line[c] : ' ';
                cells[r][c] = new VtCellSnapshot
                {
                    Text = ch is '\0' or ' ' ? " " : ch.ToString(),
                    Width = 1,
                    IsContinuation = false,
                    Style = VtCellStyleSnapshot.Default,
                };
            }
        }

        return snap with { Cells = cells };
    }

    private static int CountTextLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            lock (_vtIoGate)
                _disposed = true;

            await ReplaceHandoffExportAsync(null).ConfigureAwait(false);

            if (_readCts is not null)
                await _readCts.CancelAsync().ConfigureAwait(false);

            DetachTtyModeSource(_pty);
            if (_pty is not null)
                await _pty.DisposeAsync().ConfigureAwait(false);

            if (_readLoop is not null)
            {
                try { await _readLoop.ConfigureAwait(false); }
                catch { /* cancelled */ }
            }

            if (_waitLoop is not null)
            {
                try { await _waitLoop.ConfigureAwait(false); }
                catch { /* cancelled */ }
            }

            _readCts?.Dispose();
            _readCts = null;

            // Deterministic VT teardown (Ghostty native terminal / scrollback).
            // Must run after the read loop so no concurrent Feed races Dispose.
            // Engine first, then the history store.
            try { _vt.Dispose(); }
            catch { /* best-effort; pane is already closed */ }
            ReleaseHistoryStore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ApplyHostTerminalThemeUnlocked(HostTerminalTheme theme)
    {
        var foregroundUnowned = !_childFgOwned;
        var backgroundUnowned = !_childBgOwned;
        _hostTheme = theme;
        if (foregroundUnowned && backgroundUnowned)
            _transientOwnerTpgid = null;
        if (_vt is GhosttyVtEngine ghostty)
        {
            ghostty.SetHostDefaultColors(theme.Foreground, theme.Background);
            // ghostty_terminal_set option 14. Do not Feed OSC 4.
            ghostty.SetDefaultPalette(theme.Palette);
        }

        if (foregroundUnowned)
            FeedHostDefaultUnlocked(HostDefaultColorKind.Foreground, theme.Foreground);
        if (backgroundUnowned)
            FeedHostDefaultUnlocked(HostDefaultColorKind.Background, theme.Background);

        _feedGeneration++;
        _lastFeedPaintDecision = new PaneFeedPaintDecision(
            _feedGeneration,
            RequestPaint: !_vt.IsSynchronizedOutputActive);
    }

    private void FeedHostDefaultUnlocked(HostDefaultColorKind kind, HostRgb? color)
    {
        var sequence = color is { } rgb
            ? HostThemeParser.OscSetDefaultColorSequence(kind, rgb)
            : HostThemeParser.OscResetDefaultColorSequence(kind);
        _vt.Feed(System.Text.Encoding.ASCII.GetBytes(sequence));
    }

    private void ApplyChildBytesUnlocked(ReadOnlySpan<byte> bytes, List<byte[]> replies)
    {
        // One tokenizer reads the chunk. Feed child bytes through each default colour
        // OSC end offset first, then handle Reset so OSC 111 is followed by
        // host OSC 10/11 (terminal.rs:3071-3074). Effects come in stream order,
        // and each feed adds the VT replies (DSR, DA, DECRQM, ...) of its bytes,
        // so all replies are in stream order too.
        var effects = _osc.Observe(bytes);
        var sawSet = false;
        var written = 0;
        foreach (var effect in effects)
        {
            if (effect is ProgramStatusOscEffect status)
            {
                if (status.Signal.Kind is ProgramStatusSignalKind.Query)
                    FeedChildOutputThroughUnlocked(bytes, ref written, status.EndOffset, replies);
                ApplyProgramStatusSignalUnlocked(status.Signal, replies);
                continue;
            }

            if (effect is not DefaultColorOscEffect color)
                continue;

            var ev = color.Event;
            var firstVtReply = replies.Count;
            FeedChildOutputThroughUnlocked(bytes, ref written, ev.EndOffset, replies);

            switch (ev.Kind)
            {
                case DefaultColorOscKind.Query:
                    if (TryOscQueryReply(ev.Channel, out var reply))
                    {
                        // Ghostty also answers when it holds a default colour.
                        // The host theme reply takes its place (terminal.rs
                        // remove_last_matching_libghostty_color_reply).
                        PaneQueryReplies.RemoveLastDefaultColorReply(replies, firstVtReply, ev.Channel);
                        replies.Add(reply);
                    }

                    break;
                case DefaultColorOscKind.Set:
                    MarkChildOwnedUnlocked(ev.Channel, owned: true, ev.Color);
                    sawSet = true;
                    break;
                case DefaultColorOscKind.Reset:
                    MarkChildOwnedUnlocked(ev.Channel, owned: false, color: null);
                    FeedHostDefaultUnlocked(
                        ev.Channel,
                        ev.Channel is HostDefaultColorKind.Foreground
                            ? _hostTheme.Foreground
                            : _hostTheme.Background);
                    break;
            }
        }

        if (written < bytes.Length)
        {
            var firstVtReply = replies.Count;
            FeedChildOutputThroughUnlocked(bytes, ref written, bytes.Length, replies);
            // The chunk ends between the ESC and the backslash of an OSC 10/11
            // query. Ghostty answered at the ESC. Hypa answers when the
            // backslash arrives, so drop the Ghostty reply now.
            if (_osc.TryGetDefaultColorQueryAwaitingTerminator(out var pending)
                && TryOscQueryReply(pending, out _))
            {
                PaneQueryReplies.RemoveLastDefaultColorReply(replies, firstVtReply, pending);
            }
        }

        var tpgid = ReadForegroundGroup();
        if (sawSet
            && tpgid is { } owner
            && ReadShellPid() is { } shellPid
            && owner != shellPid)
        {
            _transientOwnerTpgid = owner;
        }

        MaybeRestoreHostThemeUnlocked(tpgid);
    }

    // Feed live child bytes up to endOffset. The VT replies of those bytes go
    // to the reply list, in stream order, after the reply filter.
    private void FeedChildOutputThroughUnlocked(
        ReadOnlySpan<byte> bytes,
        ref int written,
        int endOffset,
        List<byte[]> replies)
    {
        var end = Math.Min(Math.Max(endOffset, 0), bytes.Length);
        if (end <= written)
            return;
        var firstVtReply = replies.Count;
        _vt.FeedChildOutput(bytes.Slice(written, end - written), replies);
        written = end;
        PaneQueryReplies.RemoveUnsupported(replies, firstVtReply);
    }

    // OSC 7501. The reply to the query is fixed. Reports, resets, and prompt starts wait in the scratch list
    // until the read loop leaves the VT gate and tells the subscribers.
    private static readonly byte[] ProgramStatusQueryReply = "\u001b]7501;?\u001b\\"u8.ToArray();

    private void ApplyProgramStatusSignalUnlocked(ProgramStatusSignal signal, List<byte[]> replies)
    {
        if (signal.Kind is ProgramStatusSignalKind.Query)
            replies.Add(ProgramStatusQueryReply);
        else
            _programStatusScratch.Add(signal);
    }

    private IReadOnlyList<ProgramStatusSignal> TakeProgramStatusSignalsUnlocked()
    {
        if (_programStatusScratch.Count == 0)
            return [];
        var taken = _programStatusScratch.ToArray();
        _programStatusScratch.Clear();
        return taken;
    }

    private void InvokeProgramStatusSignaled(IReadOnlyList<ProgramStatusSignal> signals)
    {
        var handlers = Volatile.Read(ref _programStatusHandlers);
        if (handlers.Length == 0 || signals.Count == 0)
            return;

        foreach (var handler in handlers)
        {
            try
            {
                handler(this, signals);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pane {PaneId} ProgramStatusSignaled subscriber threw", Id);
            }
        }
    }

    private bool TryOscQueryReply(HostDefaultColorKind channel, out byte[] reply)
    {
        reply = [];
        // that channel. Owned returns None and lets Ghostty answer through
        // write_pty with the colour that the child set.
        var owned = channel is HostDefaultColorKind.Foreground
            ? _childFgOwned
            : _childBgOwned;
        if (owned)
            return false;

        var color = channel is HostDefaultColorKind.Foreground
            ? _hostTheme.Foreground
            : _hostTheme.Background;
        if (color is not { } rgb)
            return false;
        reply = HostThemeParser.OscRgbResponse(channel, rgb);
        return true;
    }

    private void MarkChildOwnedUnlocked(HostDefaultColorKind channel, bool owned, HostRgb? color)
    {
        _ = color;
        if (channel is HostDefaultColorKind.Foreground)
            _childFgOwned = owned;
        else
            _childBgOwned = owned;
    }

    private void MaybeRestoreHostThemeUnlocked(int? tpgid)
    {
        // no longer foreground. Skip alternate screen: osc.rs:761-767 / :780-788
        // and terminal.rs:3304-3314. tpgid == shell pid approximates
        // foreground_job_is_shell (osc.rs:689-691, :776 / :794-795).
        if (_transientOwnerTpgid is null || _hostTheme.IsEmpty)
            return;
        if (IsAlternateScreenUnlocked())
            return;
        if (ReadShellPid() is not { } shellPid)
            return;
        if (tpgid is not { } group || group != shellPid)
            return;

        _transientOwnerTpgid = null;
        _childFgOwned = false;
        _childBgOwned = false;
        FeedHostDefaultUnlocked(HostDefaultColorKind.Foreground, _hostTheme.Foreground);
        FeedHostDefaultUnlocked(HostDefaultColorKind.Background, _hostTheme.Background);
    }

    private int? ReadShellPid() => TestShellPid ?? Pid;

    private bool IsAlternateScreenUnlocked()
    {
        try
        {
            return _vt.IsAlternateScreen;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private int? ReadForegroundGroup()
    {
        if (TestForegroundGroup is { } test)
            return test;
        if (ReadShellPid() is not { } shellPid)
            return null;
        return ProcessInfoProbe?.TryGetForegroundGroup(shellPid);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _pty is not null)
            {
                int n;
                try
                {
                    n = await _pty.StandardOutput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (n <= 0)
                    break;

                var slice = buffer.AsSpan(0, n);
                AttachPathTrace.RecordOutput(
                    AttachPathTrace.StagePtyRead,
                    route: null,
                    Id.Value,
                    n);
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtFeed,
                    Id.Value,
                    n,
                    bound: AttachPathTrace.BoundStart);
                int bellCount;
                List<byte[]> oscReplies;
                IReadOnlyList<ProgramStatusSignal> programStatusSignals;
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtIoGate,
                    Id.Value,
                    n,
                    bound: AttachPathTrace.BoundStart);
                lock (_vtIoGate)
                {
                    AttachPathTrace.RecordPhase(
                        AttachPathTrace.DomainOutput,
                        AttachPathTrace.StageVtIoGate,
                        Id.Value,
                        n,
                        bound: AttachPathTrace.BoundEnd);
                    oscReplies = [];
                    ApplyChildBytesUnlocked(slice, oscReplies);
                    // The mode of the bytes just fed. The helper sent a change
                    // before the output that it covers, so the source has it now.
                    ApplyTtyModeUnlocked();
                    programStatusSignals = TakeProgramStatusSignalsUnlocked();
                    bellCount = _vt.TakePendingBellCount();
                    _feedGeneration++;
                    // Drain after Feed, still under _vtIoGate. may skip
                    // compose when no observer is live. History still drains,
                    // or unwatched panes lose rows.
                    DrainHistoryUnlocked();
                    // request_render = !mode_get(MODE_SYNCHRONIZED_OUTPUT)
                    // after the Ghostty write. Bind the decision to this feed.
                    _lastFeedPaintDecision = new PaneFeedPaintDecision(
                        _feedGeneration,
                        RequestPaint: !_vt.IsSynchronizedOutputActive);
                }

                AttachPathTrace.RecordOutput(
                    AttachPathTrace.StageVtFeed,
                    route: null,
                    Id.Value,
                    n,
                    feedGeneration: _feedGeneration);
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageVtFeed,
                    Id.Value,
                    n,
                    _feedGeneration,
                    bound: AttachPathTrace.BoundEnd);

                _intelligence?.OnPaneOutput(Id, slice);
                // A handler must not retain the memory after it returns.
                var outputHandlers = Volatile.Read(ref _outputHandlers);
                if (outputHandlers.Length > 0)
                    InvokeOutputReceived(buffer.AsMemory(0, n), outputHandlers);

                if (bellCount > 0)
                    InvokeBellReceived(bellCount);

                InvokeProgramStatusSignaled(programStatusSignals);

                foreach (var reply in oscReplies)
                {
                    try
                    {
                        await WriteAsync(reply, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pane {PaneId} read loop ended with error", Id);
        }
    }

    private void AttachTtyModeSource(IPtyProcess? pty)
    {
        if (pty is not IPtyTtyModeSource source)
            return;
        source.TtyModeChanged += OnPtyTtyModeChanged;
        // The helper sends the first mode right after Spawned or Adopted, so
        // it can arrive before this subscription. Take it now.
        OnPtyTtyModeChanged();
    }

    private void DetachTtyModeSource(IPtyProcess? pty)
    {
        if (pty is IPtyTtyModeSource source)
            source.TtyModeChanged -= OnPtyTtyModeChanged;
    }

    /// <summary>
    /// Takes the mode of the output that the reader has read. A known mode
    /// never goes back to unknown: a new owner after an Adopt has no mode
    /// until its first frame, and the child is the same. The caller holds
    /// <c>_vtIoGate</c>. Returns true when the mode changed.
    /// </summary>
    private bool ApplyTtyModeUnlocked()
    {
        if ((_pty as IPtyTtyModeSource)?.TtyMode is not { } next || next == _ttyMode)
            return false;
        _ttyMode = next;
        return true;
    }

    /// <summary>
    /// A mode change for output that the reader already read, for example a
    /// silent <c>stty -echo</c> that the helper found at an input write. No
    /// output follows, so the read loop does not see it. Take it now and give
    /// it a feed generation, so that the mux paints a body with the new flag.
    /// Runs on the helper frame pump.
    /// </summary>
    private void OnPtyTtyModeChanged()
    {
        bool changed;
        lock (_vtIoGate)
        {
            if (_disposed)
                return;
            changed = ApplyTtyModeUnlocked();
            if (changed)
            {
                _feedGeneration++;
                _lastFeedPaintDecision = new PaneFeedPaintDecision(
                    _feedGeneration,
                    RequestPaint: !_vt.IsSynchronizedOutputActive);
            }
        }

        if (!changed)
            return;
        try
        {
            TtyModeChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pane {PaneId} TtyModeChanged subscriber threw", Id);
        }
    }

    private void InvokeBellReceived(int count)
    {
        var handlers = Volatile.Read(ref _bellHandlers);
        if (handlers.Length == 0)
            return;

        foreach (var handler in handlers)
        {
            try
            {
                handler(this, count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pane {PaneId} BellReceived subscriber threw", Id);
            }
        }
    }

    internal async Task ReplaceHandoffExportAsync(HandoffExportLease? next)
    {
        HandoffExportLease? previous;
        lock (_handoffGate)
        {
            previous = _handoffExport;
            _handoffExport = next;
        }

        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    private void InvokeOutputReceived(
        ReadOnlyMemory<byte> chunk,
        Action<IPaneRuntime, ReadOnlyMemory<byte>>[] handlers)
    {
        foreach (var handler in handlers)
        {
            try
            {
                handler(this, chunk);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pane {PaneId} OutputReceived subscriber threw", Id);
            }
        }
    }

    private async Task WaitLoopAsync(CancellationToken ct)
    {
        if (_pty is null)
            return;

        try
        {
            await _pty.WaitForExitAsync(ct).ConfigureAwait(false);
            // Terminal ownership-end vs child-death:
            // H2 handoff / hard-abandon leave-alive complete WaitForExit with null ExitCode
            // so the OS child can live under a peer. Do not invent ExitCode=-1 or fire
            // Exited with a fiction — IsAlive becomes false via !IPtyProcess.IsRunning.
            if (_pty.ExitCode is int code)
            {
                _exitCode = code;
                Exited?.Invoke(this, code);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>
    /// Resolves pane argv. Empty command uses terminal.default_shell / $SHELL / /bin/sh.
    /// shell_mode=login adds -l; non_login does not; auto is login on macOS only.
    /// A space in <paramref name="command"/> does not insert a shell wrap.
    /// Explicit wrap is argv: <c>/bin/bash</c> plus <c>["-lc", cmd]</c>.
    /// </summary>
    internal static (string File, IReadOnlyList<string> Args) ResolveCommand(
        string command,
        IReadOnlyList<string> args,
        AttachTerminalConfig? terminal = null)
    {
        if (!string.IsNullOrWhiteSpace(command))
            return (command, args);

        return TerminalSpawnPolicy.ResolveEmptyCommand(
            terminal ?? AttachTerminalConfig.Default,
            Environment.GetEnvironmentVariable("SHELL"),
            OperatingSystem.IsMacOS());
    }
}

public sealed class PaneRuntimeFactory : IPaneRuntimeFactory
{
    private readonly IIntelligencePipeline? _intelligence;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IPtyProcessFactory _ptyFactory;
    private readonly Hypa.Terminal.Vt.Ghostty.IVtEngineFactory _vtEngineFactory;
    private readonly IPaneProcessInfoProbe _processInfoProbe;

    public PaneRuntimeFactory(
        IIntelligencePipeline? intelligence = null,
        ILoggerFactory? loggerFactory = null,
        IPtyProcessFactory? ptyFactory = null,
        Hypa.Terminal.Vt.Ghostty.IVtEngineFactory? vtEngineFactory = null,
        IPaneProcessInfoProbe? processInfoProbe = null)
    {
        _intelligence = intelligence;
        _loggerFactory = loggerFactory;
        _ptyFactory = ptyFactory
            ?? new PtyProcessFactory(
                logger: loggerFactory?.CreateLogger<PtyProcessFactory>());
        _vtEngineFactory = vtEngineFactory
            ?? Hypa.Terminal.Vt.Ghostty.VtEngineFactory.FromEnvironment();
        _processInfoProbe = processInfoProbe ?? NullPaneProcessInfoProbe.Instance;
    }

    /// <summary>Provider options used for pane spawns (runtime.health).</summary>
    public PtyProviderOptions PtyOptions => _ptyFactory.Options;

    /// <inheritdoc />
    public string PtyProvider => _ptyFactory.Options.ProviderWireName;

    /// <inheritdoc />
    public bool PtyInteractive => _ptyFactory.Options.Interactive;

    /// <summary>Process-level VT engine factory.</summary>
    public Hypa.Terminal.Vt.Ghostty.IVtEngineFactory VtEngineFactory => _vtEngineFactory;

    public IPaneRuntime Create(PaneSpawnOptions options)
    {
        var logger = _loggerFactory?.CreateLogger<PaneRuntime>();
        var totalBytes = TerminalSpawnPolicy.ResolveScrollbackBytes(options.ScrollbackLimitBytes);
        var nativeBytes = TerminalSpawnPolicy.ResolveNativeWindowBytes(totalBytes);
        var maxScrollback = (int)Math.Min(nativeBytes, int.MaxValue);
        var vt = _vtEngineFactory.Create(options.Cols, options.Rows, maxScrollback);
        var store = new InMemoryPaneHistoryStore(
            new BrotliPaneHistoryCodec(),
            TerminalSpawnPolicy.ResolveHistoryStoreRowBound(totalBytes),
            totalBytes,
            TerminalSpawnPolicy.HistoryBlockRows);
        return new PaneRuntime(
            options,
            vt: vt,
            intelligence: _intelligence,
            logger: logger,
            ptyFactory: _ptyFactory,
            historyStore: store,
            nativeWindowBytes: nativeBytes,
            scrollbackLimitBytes: totalBytes,
            processInfoProbe: _processInfoProbe);
    }
}
