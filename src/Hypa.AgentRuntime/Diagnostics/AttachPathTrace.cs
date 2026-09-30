using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Opt-in attach path trace. Two monotonic domains: input and output.
/// Join only by timestamp and recorded byte bounds. Disabled unless a capture
/// session is active or <c>HYPA_ATTACH_ROUTE_TRACE</c> is set.
/// </summary>
public static class AttachPathTrace
{
    public const string EnvVar = "HYPA_ATTACH_ROUTE_TRACE";

    public const string DomainInput = "input";
    public const string DomainOutput = "output";

    public const string RouteSnapshotFull = "snapshot-full";
    public const string RouteSnapshotPatch = "snapshot-patch";
    public const string RouteChrome = "chrome";
    public const string RouteHistoryOverlay = "history-overlay";
    public const string RouteLiveRemap = "live-remap";
    public const string RouteTailReplay = "tail-replay";
    public const string RouteByteFallback = "byte-fallback";
    public const string RouteHostMode = "host-mode";

    public const string StageDecode = "decode";
    public const string StageMouseDecode = "mouse-decode";
    public const string StageDispatch = "dispatch";
    public const string StageDetachRequest = "detach-request";
    public const string StageAdmit = "admit";
    public const string StagePtyWriteAttempt = "pty-write-attempt";
    public const string StagePtyWriteCompletion = "pty-write-completion";
    public const string StagePtyRead = "pty-read";
    public const string StageVtFeed = "vt-feed";
    public const string StageVtIoGate = "vt-io-gate";
    public const string StageSnapshot = "snapshot";
    public const string StageSkipped = "skipped";
    public const string StageCapture = "capture";
    public const string StageIdentity = "identity";
    public const string StageEncode = "encode";
    public const string StageWriterAdmit = "writer-admit";
    public const string StageReplacement = "replacement";
    public const string StageDisconnect = "disconnect";
    public const string StageHostWrite = "host-write";

    public const string BoundStart = "start";
    public const string BoundEnd = "end";

    private static readonly AsyncLocal<AttachPathSession?> Capture = new();
    private static readonly AsyncLocal<OutputScope?> CurrentOutput = new();
    private static readonly AsyncLocal<long> CurrentCorr = new();
    private static readonly TraceSinkConfiguration Sink = TraceSinkConfiguration.Load();
    private static readonly BufferedTraceSink? FileSink = Sink.FilePath is { Length: > 0 }
        ? new BufferedTraceSink(Sink.FilePath)
        : null;
    private static int HostTtyOwned;
    private static readonly int ProcessId = Environment.ProcessId;

    static AttachPathTrace()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public static bool IsEnabled =>
        Capture.Value is not null
        || (Sink.Enabled && (!Sink.Stderr || Volatile.Read(ref HostTtyOwned) == 0));

    /// <summary>Flushes the opt-in file sink. Called by deterministic shutdown paths and tests.</summary>
    public static void Flush() => FileSink?.Flush();

    /// <summary>Flushes and closes the process-wide diagnostic sink.</summary>
    public static void Dispose() => FileSink?.Dispose();

    /// <summary>Marks the interval in which stderr is owned by the host TTY.</summary>
    public static void SetHostTtyOwned(bool owned)
    {
        if (owned)
        {
            Interlocked.Increment(ref HostTtyOwned);
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref HostTtyOwned);
            if (current == 0 || Interlocked.CompareExchange(ref HostTtyOwned, current - 1, current) == current)
                return;
        }
    }

    public static AttachPathCapture BeginCapture()
    {
        var session = new AttachPathSession();
        Capture.Value = session;
        return new AttachPathCapture(session);
    }

    public static IDisposable PushOutput(
        string route,
        string? paneId = null,
        long feedGeneration = 0,
        long snapshotGeneration = 0,
        int occupantGeneration = 0,
        int cursorColBefore = -1,
        int cursorRowBefore = -1,
        int cursorColAfter = -1,
        int cursorRowAfter = -1)
    {
        if (!IsEnabled)
            return Nop.Instance;

        var prior = CurrentOutput.Value;
        CurrentOutput.Value = new OutputScope(
            route,
            paneId,
            feedGeneration,
            snapshotGeneration,
            occupantGeneration,
            cursorColBefore,
            cursorRowBefore,
            cursorColAfter,
            cursorRowAfter,
            prior);
        return new OutputPop(prior);
    }

    public static void RecordPhase(
        string domain,
        string stage,
        string? paneId = null,
        int byteCount = 0,
        long feedGeneration = 0,
        long snapshotGeneration = 0,
        int occupantGeneration = 0,
        string? bound = null)
    {
        if (!IsEnabled)
            return;

        var corr = CurrentCorr.Value;
        if (domain == DomainInput)
        {
            var seq = NextInputSeq();
            if (corr == 0)
                corr = seq;
            WriteRecord(new AttachPathRecord(
                DomainInput,
                seq,
                DateTimeOffset.UtcNow.UtcTicks,
                stage,
                Route: null,
                paneId,
                feedGeneration,
                snapshotGeneration,
                occupantGeneration,
                CursorColBefore: -1,
                CursorRowBefore: -1,
                CursorColAfter: -1,
                CursorRowAfter: -1,
                byteCount,
                ByteDigest: null,
                HostOffset: -1,
                HostCount: 0,
                corr,
                bound));
            return;
        }

        WriteRecord(new AttachPathRecord(
            DomainOutput,
            NextOutputSeq(),
            DateTimeOffset.UtcNow.UtcTicks,
            stage,
            Route: null,
            paneId,
            feedGeneration,
            snapshotGeneration,
            occupantGeneration,
            CursorColBefore: -1,
            CursorRowBefore: -1,
            CursorColAfter: -1,
            CursorRowAfter: -1,
            byteCount,
            ByteDigest: null,
            HostOffset: -1,
            HostCount: 0,
            corr,
            bound));
    }

    public static void RecordInput(
        string stage,
        string? paneId,
        ReadOnlySpan<byte> bytes,
        long feedGeneration = 0)
    {
        if (!IsEnabled || bytes.IsEmpty)
            return;

        var seq = NextInputSeq();
        var corr = CurrentCorr.Value;
        if (stage == StageDecode)
        {
            corr = seq;
            CurrentCorr.Value = seq;
        }
        else if (corr == 0)
            corr = seq;

        WriteRecord(new AttachPathRecord(
            DomainInput,
            seq,
            DateTimeOffset.UtcNow.UtcTicks,
            stage,
            Route: null,
            paneId,
            feedGeneration,
            SnapshotGeneration: 0,
            OccupantGeneration: 0,
            CursorColBefore: -1,
            CursorRowBefore: -1,
            CursorColAfter: -1,
            CursorRowAfter: -1,
            bytes.Length,
            ByteDigest: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            HostOffset: -1,
            HostCount: 0,
            corr));
    }

    public static void RecordOutput(
        string stage,
        string? route,
        string? paneId,
        int byteCount,
        long hostOffset = -1,
        int hostCount = 0,
        long feedGeneration = 0,
        long snapshotGeneration = 0,
        int occupantGeneration = 0,
        int cursorColBefore = -1,
        int cursorRowBefore = -1,
        int cursorColAfter = -1,
        int cursorRowAfter = -1)
    {
        if (!IsEnabled || byteCount <= 0)
            return;

        var scope = CurrentOutput.Value;
        WriteRecord(new AttachPathRecord(
            DomainOutput,
            NextOutputSeq(),
            DateTimeOffset.UtcNow.UtcTicks,
            stage,
            route ?? scope?.Route,
            paneId ?? scope?.PaneId,
            feedGeneration != 0 ? feedGeneration : scope?.FeedGeneration ?? 0,
            snapshotGeneration != 0 ? snapshotGeneration : scope?.SnapshotGeneration ?? 0,
            occupantGeneration != 0 ? occupantGeneration : scope?.OccupantGeneration ?? 0,
            cursorColBefore >= 0 ? cursorColBefore : scope?.CursorColBefore ?? -1,
            cursorRowBefore >= 0 ? cursorRowBefore : scope?.CursorRowBefore ?? -1,
            cursorColAfter >= 0 ? cursorColAfter : scope?.CursorColAfter ?? -1,
            cursorRowAfter >= 0 ? cursorRowAfter : scope?.CursorRowAfter ?? -1,
            byteCount,
            ByteDigest: null,
            hostOffset,
            hostCount > 0 ? hostCount : byteCount,
            CurrentCorr.Value));
    }

    /// <summary>Adds attribution to the compose context that owns the buffer.</summary>
    public static void NoteComposeAppend(ICollection<ComposeSlice> slices, int utf8Count)
    {
        if (!IsEnabled || utf8Count <= 0)
            return;

        var scope = CurrentOutput.Value;
        slices.Add(new ComposeSlice(
            scope?.Route ?? RouteHostMode,
            scope?.PaneId,
            scope?.FeedGeneration ?? 0,
            scope?.SnapshotGeneration ?? 0,
            scope?.OccupantGeneration ?? 0,
            scope?.CursorColBefore ?? -1,
            scope?.CursorRowBefore ?? -1,
            scope?.CursorColAfter ?? -1,
            scope?.CursorRowAfter ?? -1,
            utf8Count));
    }

    public static bool FlushComposeSlices(
        IReadOnlyList<ComposeSlice> slices,
        long payloadHostOffset,
        int payloadLength)
    {
        if (slices.Count == 0)
            return false;

        var offset = payloadHostOffset;
        var remaining = payloadLength;
        foreach (var slice in slices)
        {
            if (remaining <= 0)
                break;
            var count = Math.Min(slice.Utf8Count, remaining);
            RecordOutput(
                StageHostWrite,
                slice.Route,
                slice.PaneId,
                count,
                offset,
                count,
                slice.FeedGeneration,
                slice.SnapshotGeneration,
                slice.OccupantGeneration,
                slice.CursorColBefore,
                slice.CursorRowBefore,
                slice.CursorColAfter,
                slice.CursorRowAfter);
            offset += count;
            remaining -= count;
        }

        if (remaining > 0)
        {
            RecordOutput(
                StageHostWrite,
                CurrentOutput.Value?.Route ?? RouteHostMode,
                CurrentOutput.Value?.PaneId,
                remaining,
                offset,
                remaining);
        }

        return true;
    }

    public static void NoteHostWrite(long hostOffset, int byteCount)
    {
        if (!IsEnabled || byteCount <= 0)
            return;

        var scope = CurrentOutput.Value;
        RecordOutput(
            StageHostWrite,
            scope?.Route ?? RouteHostMode,
            scope?.PaneId,
            byteCount,
            hostOffset,
            byteCount);
    }

    private static long NextInputSeq()
    {
        var session = Capture.Value;
        if (session is not null)
            return session.NextInput();
        return Interlocked.Increment(ref ProcessInputSeq);
    }

    private static long NextOutputSeq()
    {
        var session = Capture.Value;
        if (session is not null)
            return session.NextOutput();
        return Interlocked.Increment(ref ProcessOutputSeq);
    }

    private static long ProcessInputSeq;
    private static long ProcessOutputSeq;

    private static void WriteRecord(AttachPathRecord record)
    {
        Capture.Value?.Add(record);
        if (!Sink.Enabled || (Sink.Stderr && Volatile.Read(ref HostTtyOwned) != 0))
            return;

        var line = Format(record);
        if (Sink.Stderr)
        {
            Console.Error.WriteLine(line);
            return;
        }

        FileSink?.Write(line);
        // The PTY benchmark uses this source-tagged record as its fail-closed
        // readiness boundary. It is outside the measured input interval, so a
        // one-time diagnostic flush cannot contaminate keystroke latency.
        if (record.Stage == StageHostWrite && record.Route == RouteSnapshotFull)
            FileSink?.Flush();
    }

    internal static string Format(AttachPathRecord record)
    {
        var sb = new StringBuilder(160);
        sb.Append(record.Domain == DomainInput ? 'I' : 'O');
        sb.Append(' ');
        sb.Append(record.Seq.ToString("D6", CultureInfo.InvariantCulture));
        sb.Append(" pid=");
        sb.Append(ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.Append(" ts=");
        sb.Append(record.TimestampTicks.ToString(CultureInfo.InvariantCulture));
        sb.Append(" stage=");
        sb.Append(record.Stage);
        if (!string.IsNullOrEmpty(record.Route))
        {
            sb.Append(" route=");
            sb.Append(record.Route);
        }

        if (!string.IsNullOrEmpty(record.PaneId))
        {
            sb.Append(" pane=");
            sb.Append(record.PaneId);
        }

        if (record.FeedGeneration != 0)
        {
            sb.Append(" feed=");
            sb.Append(record.FeedGeneration.ToString(CultureInfo.InvariantCulture));
        }

        if (record.SnapshotGeneration != 0)
        {
            sb.Append(" snap=");
            sb.Append(record.SnapshotGeneration.ToString(CultureInfo.InvariantCulture));
        }

        if (record.OccupantGeneration != 0)
        {
            sb.Append(" occ=");
            sb.Append(record.OccupantGeneration.ToString(CultureInfo.InvariantCulture));
        }

        if (record.Correlation != 0)
        {
            sb.Append(" corr=");
            sb.Append(record.Correlation.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(record.Bound))
        {
            sb.Append(" bound=");
            sb.Append(record.Bound);
        }

        if (record.CursorColBefore >= 0)
        {
            sb.Append(" cursor=");
            sb.Append(record.CursorColBefore.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(record.CursorRowBefore.ToString(CultureInfo.InvariantCulture));
            sb.Append("->");
            sb.Append(record.CursorColAfter.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(record.CursorRowAfter.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(" bytes=");
        sb.Append(record.ByteCount.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(record.ByteDigest))
        {
            sb.Append(" sha256=");
            sb.Append(record.ByteDigest);
        }
        if (record.HostOffset >= 0)
        {
            sb.Append(" host=[");
            sb.Append(record.HostOffset.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append((record.HostOffset + record.HostCount).ToString(CultureInfo.InvariantCulture));
            sb.Append(')');
        }

        return sb.ToString();
    }

    public sealed class AttachPathCapture : IDisposable
    {
        private readonly AttachPathSession _session;

        internal AttachPathCapture(AttachPathSession session) => _session = session;

        public IReadOnlyList<AttachPathRecord> Records => _session.Snapshot();

        public IReadOnlyList<AttachPathRecord> Input =>
            Records.Where(r => r.Domain == DomainInput).ToArray();

        public IReadOnlyList<AttachPathRecord> Output =>
            Records.Where(r => r.Domain == DomainOutput).ToArray();

        public void Dispose()
        {
            if (ReferenceEquals(Capture.Value, _session))
                Capture.Value = null;
        }
    }

    internal sealed class AttachPathSession
    {
        private readonly object _gate = new();
        private readonly List<AttachPathRecord> _records = [];
        private long _inputSeq;
        private long _outputSeq;

        public long NextInput()
        {
            lock (_gate)
                return ++_inputSeq;
        }

        public long NextOutput()
        {
            lock (_gate)
                return ++_outputSeq;
        }

        public void Add(AttachPathRecord record)
        {
            lock (_gate)
                _records.Add(record);
        }

        public AttachPathRecord[] Snapshot()
        {
            lock (_gate)
                return _records.ToArray();
        }
    }

    public readonly record struct ComposeSlice(
        string Route,
        string? PaneId,
        long FeedGeneration,
        long SnapshotGeneration,
        int OccupantGeneration,
        int CursorColBefore,
        int CursorRowBefore,
        int CursorColAfter,
        int CursorRowAfter,
        int Utf8Count);

    private sealed class OutputPop : IDisposable
    {
        private readonly OutputScope? _prior;
        private bool _disposed;

        public OutputPop(OutputScope? prior) => _prior = prior;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentOutput.Value = _prior;
        }
    }

    private sealed record OutputScope(
        string Route,
        string? PaneId,
        long FeedGeneration,
        long SnapshotGeneration,
        int OccupantGeneration,
        int CursorColBefore,
        int CursorRowBefore,
        int CursorColAfter,
        int CursorRowAfter,
        OutputScope? Prior);

    private sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed record TraceSinkConfiguration(bool Enabled, bool Stderr, string? FilePath)
    {
        public static TraceSinkConfiguration Load()
        {
            var value = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(value) || value == "0")
                return new(false, false, null);
            if (value is "1" or "stderr" or "true")
                return new(true, true, null);
            // Attach and mux are separate processes. Never let inherited
            // configuration place two buffered writers on the same file.
            // The caller may choose the PID position explicitly; otherwise
            // preserve the supplied path as a prefix.
            var filePath = ResolveFilePath(value, Environment.ProcessId);
            return new(true, false, filePath);
        }
    }

    internal static string ResolveFilePath(string configuredPath, int processId)
    {
        var pid = processId.ToString(CultureInfo.InvariantCulture);
        return configuredPath.Contains("{pid}", StringComparison.Ordinal)
            ? configuredPath.Replace("{pid}", pid, StringComparison.Ordinal)
            : $"{configuredPath}.pid-{pid}";
    }

    private sealed class BufferedTraceSink : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;
        private readonly Timer _flushTimer;
        private bool _disposed;

        public BufferedTraceSink(string path)
        {
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096);
            _flushTimer = new Timer(
                static state => ((BufferedTraceSink)state!).Flush(),
                this,
                dueTime: TimeSpan.FromMilliseconds(100),
                period: TimeSpan.FromMilliseconds(100));
        }

        public void Write(string line)
        {
            lock (_gate)
            {
                if (!_disposed)
                    _writer.WriteLine(line);
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (!_disposed)
                    _writer.Flush();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _flushTimer.Dispose();
                _writer.Dispose();
            }
        }
    }
}

public readonly record struct AttachPathRecord(
    string Domain,
    long Seq,
    long TimestampTicks,
    string Stage,
    string? Route,
    string? PaneId,
    long FeedGeneration,
    long SnapshotGeneration,
    int OccupantGeneration,
    int CursorColBefore,
    int CursorRowBefore,
    int CursorColAfter,
    int CursorRowAfter,
    int ByteCount,
    string? ByteDigest,
    long HostOffset,
    int HostCount,
    long Correlation = 0,
    string? Bound = null);
