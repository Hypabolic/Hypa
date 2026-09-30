using System.Runtime.InteropServices;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Chrome;

namespace Hypa.Cli.Attach;

/// <summary>
/// Unix termios raw mode for the attach TTY. Restore cooked mode on every exit.
/// </summary>
public sealed class UnixRawTerminal : IDisposable
{
    private const int Tcsanow = 0;
    private const int TermiosBytes = 256;
    private const int LinuxTiocgwinsz = 0x5413;
    private const int MacTiocgwinsz = 0x40087468;

    private readonly int _fd;
    private readonly byte[] _original;
    private readonly bool _owned;
    private readonly bool _hasTermios;
    private readonly Stream? _capture;
    private readonly Func<(nint n, int errno)>? _writer;
    private readonly object _writeGate = new();
    private int _pinnedCols;
    private int _pinnedRows;
    private bool _raw;
    private bool _disposed;
    private bool _altEntered;
    private bool _clientOverlay;
    private bool _windowTitleApplied;
    private string? _lastWindowTitle;
    private int _synchronizedPaintDepth;
    private long _hostBytesWritten;
    private bool _wrapDisabled;
    private readonly ThreadLocal<ComposeContext?> _composeSlot = new();
    private readonly ClientOverlayCarry _overlayCarry = new();

    /// <summary>Test seam: runs off the write lock before composed TTY bytes.</summary>
    internal Action? BeforeComposedWrite { get; set; }

    /// <summary>Test seam: runs off the write lock immediately before taking it.</summary>
    internal Action? BeforeWriteLock { get; set; }

    /// <summary>Test seam: runs inside the write lock before composed TTY bytes.</summary>
    internal Action? DuringComposedWrite { get; set; }

    /// <summary>Test seam: runs after this thread owns a compose buffer, before paint.</summary>
    internal Action? DuringCompose { get; set; }

    /// <summary>Count of write-lock recover payloads that replaced a stale compose.</summary>
    internal int RecoveredComposeCount { get; private set; }

    /// <summary>Host TTY bytes accepted by <see cref="WriteBytesCore"/> in this instance.</summary>
    internal long HostBytesWritten => _hostBytesWritten;

    public UnixRawTerminal(int fd = 0)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Unix raw TTY attach requires Linux or macOS.");

        _fd = fd;
        _original = new byte[TermiosBytes];
        if (tcgetattr(_fd, _original) != 0)
            throw new InvalidOperationException("tcgetattr failed. stdin is not a TTY.");
        _owned = true;
        _hasTermios = true;
        SetNonBlocking(_fd, enabled: false);
    }

    /// <summary>
    /// Capture sink for tests. Skips termios. Writes go to <paramref name="capture"/>.
    /// </summary>
    internal UnixRawTerminal(Stream capture)
        : this(capture, cols: 0, rows: 0)
    {
    }

    /// <summary>
    /// Capture sink with a pinned ioctl size. goldens use 80×24 or 64×24
    /// so <see cref="TryGetSize"/> does not ioctl fd -1.
    /// </summary>
    internal UnixRawTerminal(Stream capture, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _fd = -1;
        _original = [];
        _owned = true;
        _hasTermios = false;
        _capture = capture;
        _pinnedCols = cols;
        _pinnedRows = rows;
    }

    /// <summary>Update the pinned ioctl size. replay uses this for 64×24 narrow.</summary>
    internal void PinSize(int cols, int rows)
    {
        _pinnedCols = cols;
        _pinnedRows = rows;
    }

    /// <summary>
    /// Writer sink for tests. Skips termios and returns the native write result
    /// and errno without opening a live TTY.
    /// </summary>
    internal UnixRawTerminal(Func<(nint n, int errno)> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _fd = -1;
        _original = [];
        _owned = true;
        _hasTermios = false;
        _writer = writer;
    }

    public bool IsRaw => _raw;

    public void EnterRaw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_raw)
            return;

        if (_hasTermios)
        {
            var raw = (byte[])_original.Clone();
            cfmakeraw(raw);
            if (tcsetattr(_fd, Tcsanow, raw) != 0)
                throw new InvalidOperationException("tcsetattr raw failed.");
        }

        _raw = true;
        AttachPathTrace.SetHostTtyOwned(true);
        if (_writer is null)
        {
            using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                WriteBytes(SnapshotPainter.DisableLineWrap);
            _wrapDisabled = true;
        }
    }

    public void Restore()
    {
        if (!_owned || _disposed)
            return;

        var wasHostTtyOwner = _raw;
        var wrapEnabled = false;
        try
        {
            lock (_writeGate)
            {
                // A failed composed paint must not leave the host in synchronized mode.
                var hadOpenSynchronizedPaint = _synchronizedPaintDepth > 0;
                _synchronizedPaintDepth = 0;
                using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                {
                    if (hadOpenSynchronizedPaint)
                        WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EndSynchronizedOutput));
                    if (ClientOverlay.ShouldWriteRestoreSequence(_clientOverlay))
                    {
                        WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.RestoreSequence));
                        wrapEnabled = _wrapDisabled;
                    }
                    else if (_wrapDisabled)
                    {
                        WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EnableLineWrap));
                        wrapEnabled = true;
                    }
                    if (_windowTitleApplied)
                        WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(WindowTitleApplier.ClearSequence));
                }
            }
        }
        catch
        {
            // restore termios even if the write fails
        }

        if (_wrapDisabled && !wrapEnabled)
        {
            try
            {
                lock (_writeGate)
                {
                    using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                    {
                        WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EnableLineWrap));
                        wrapEnabled = true;
                    }
                }
            }
            catch
            {
                // keep wrap-disabled until a later restore can send CSI ?7h
            }
        }

        // Host replies to OSC 10/11, CSI 996, and OSC 4 (0–255) sit in stdin.
        // Drain them while still raw so cooked mode does not echo leftover reports.
        if (_raw && _fd >= 0)
        {
            try { DrainPendingInput(_fd); }
            catch { /* restore termios even if drain fails */ }
        }

        if (_hasTermios && _raw)
            _ = tcsetattr(_fd, Tcsanow, _original);

        // O_NONBLOCK is per open-file-description and is inherited by the shell.
        // InterruptStdin used to set it and never clear it; the next attach then
        // saw EAGAIN as hangup and printed Detached immediately.
        if (_fd >= 0)
            SetNonBlocking(_fd, enabled: false);

        _raw = false;
        if (wasHostTtyOwner)
            AttachPathTrace.SetHostTtyOwned(false);
        _altEntered = false;
        _clientOverlay = false;
        _windowTitleApplied = false;
        _lastWindowTitle = null;
        if (wrapEnabled)
            _wrapDisabled = false;
        _overlayCarry.Clear();
    }

    public bool AltScreenEntered => _altEntered;

    public bool ClientOverlayActive => _clientOverlay;

    internal ClientOverlayCarry OverlayCarry => _overlayCarry;

    public void EnableFocusReport()
    {
        WriteBytes(SnapshotPainter.EnableFocusReport);
    }

    public void EnableMouseCapture(bool capture)
    {
        WriteBytes(capture
            ? Hypa.Cli.Attach.Mouse.MouseCapture.EnableSequence
            : Hypa.Cli.Attach.Mouse.MouseCapture.DisableSequence);
    }

    /// <summary>
    /// Client-side host overlay. Protects the user's main screen and scrollback.
    /// Not a pane snapshot 1049h.
    /// </summary>
    public void EnterClientOverlay()
    {
        _clientOverlay = true;
        using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
            WriteBytes(SnapshotPainter.EnterAltScreen + SnapshotPainter.DisableLineWrap);
        _wrapDisabled = true;
    }

    public void NoteAltScreen(bool entered) => _altEntered = entered;

    /// <summary>
    /// Explicit 1049h re-entry. Do not call after ordinary main-screen bytes.
    /// A second 1049h runs DECSC and can clear the picture. Live attach strips
    /// pane 1049l and keeps the overlay; Restore writes the only 1049l.
    /// </summary>
    public void EnsureClientOverlay()
    {
        if (!_clientOverlay)
            return;
        WriteBytes(SnapshotPainter.EnterAltScreen);
    }

    /// <summary>
    /// Cancellable stdin wait. A blocking Unix read does not wake on
    /// <see cref="CancellationToken"/>.
    /// </summary>
    public static bool PollReadable(int fd, int timeoutMs)
    {
        var pfd = new Pollfd
        {
            Fd = fd,
            Events = Pollin,
        };
        var n = poll(ref pfd, 1, timeoutMs);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err is 4 or 35)
                return false;
            throw new InvalidOperationException($"poll failed: {err}");
        }

        return n > 0 && (pfd.Revents & (Pollin | Pollhup | Pollerr)) != 0;
    }

    public static void SetNonBlocking(int fd, bool enabled)
    {
        var flags = Fcntl(fd, FGetfl, 0);
        if (flags < 0)
            return;
        var bit = OperatingSystem.IsMacOS() ? MacONonblock : LinuxONonblock;
        _ = Fcntl(fd, FSetfl, enabled ? flags | bit : flags & ~bit);
    }

    /// <summary>True when <c>O_NONBLOCK</c> is set. Used to detect a poisoned TTY fd.</summary>
    internal static bool IsNonBlocking(int fd)
    {
        var flags = Fcntl(fd, FGetfl, 0);
        if (flags < 0)
            return false;
        var bit = OperatingSystem.IsMacOS() ? MacONonblock : LinuxONonblock;
        return (flags & bit) != 0;
    }

    /// <summary>
    /// Discard bytes already waiting on <paramref name="fd"/>. Does not block.
    /// Used on restore so solicited host theme replies do not leak into cooked mode.
    /// </summary>
    internal static int DrainPendingInput(int fd, int maxBytes = 65536, int waitMs = 80)
    {
        if (fd < 0 || maxBytes < 1)
            return 0;

        var buf = new byte[256];
        var drained = 0;
        var wasNonBlocking = IsNonBlocking(fd);
        if (!wasNonBlocking)
            SetNonBlocking(fd, enabled: true);
        try
        {
            // One short wait for in-flight host replies after we disable 2031.
            if (waitMs > 0)
                _ = PollReadable(fd, waitMs);
            while (drained < maxBytes)
            {
                if (!PollReadable(fd, timeoutMs: 0))
                    break;
                int n;
                try
                {
                    n = TryRead(fd, buf);
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (n <= 0)
                    break;
                drained += n;
            }
        }
        finally
        {
            if (!wasNonBlocking)
                SetNonBlocking(fd, enabled: false);
        }

        return drained;
    }

    /// <summary>
    /// Non-blocking read. Returns 0 on EOF, -1 on EAGAIN.
    /// </summary>
    public static int TryRead(int fd, byte[] buf) =>
        TryRead(fd, buf, offset: 0);

    /// <summary>
    /// Non-blocking read into <paramref name="buf"/> at <paramref name="offset"/>.
    /// Returns 0 on EOF, -1 on EAGAIN.
    /// </summary>
    public static int TryRead(int fd, byte[] buf, int offset)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (offset < 0 || offset > buf.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var remaining = buf.Length - offset;
        if (remaining == 0)
            return 0;
        var n = read(fd, ref buf[offset], (nuint)remaining);
        if (n < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err is 11 or 35)
                return -1;
            throw new InvalidOperationException($"read failed: {err}");
        }

        return (int)n;
    }

    /// <summary>
    /// Track 1049h/l in live PTY bytes so later snapshots stay accurate.
    /// Restore always leaves alt-screen even if this misses a split sequence.
    /// Returns true when this chunk leaves pane alt.
    /// </summary>
    public bool NoteLiveBytes(ReadOnlySpan<byte> bytes)
    {
        var left = ClientOverlay.NoteTransition(bytes, _altEntered, out var nowAlt);
        _altEntered = nowAlt;
        return left;
    }

    public void WriteBytes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var compose = CurrentCompose();
        if (compose is not null)
        {
            compose.Append(text);
            if (AttachPathTrace.IsEnabled)
                AttachPathTrace.NoteComposeAppend(compose.Slices, Encoding.UTF8.GetByteCount(text));
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        WriteBytes(bytes);
    }

    /// <summary>Writes OSC 2 once per changed payload. Restore still clears the host title.</summary>
    internal void WriteWindowTitle(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
            return;
        var compose = CurrentCompose();
        if (compose is not null)
        {
            if (_windowTitleApplied
                && string.Equals(_lastWindowTitle, sequence, StringComparison.Ordinal))
            {
                return;
            }

            compose.Append(sequence);
            if (AttachPathTrace.IsEnabled)
                AttachPathTrace.NoteComposeAppend(compose.Slices, Encoding.UTF8.GetByteCount(sequence));
            _windowTitleApplied = true;
            _lastWindowTitle = sequence;
            return;
        }

        lock (_writeGate)
        {
            if (_windowTitleApplied
                && string.Equals(_lastWindowTitle, sequence, StringComparison.Ordinal))
            {
                return;
            }

            WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(sequence));
            _windowTitleApplied = true;
            _lastWindowTitle = sequence;
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _ = WriteBytesIf(bytes, accept: null);
    }

    /// <summary>
    /// Write live bytes. <paramref name="accept"/> runs inside the TTY write
    /// lock (or on the compose buffer) and skips the write when it returns false.
    /// </summary>
    internal bool WriteBytesIf(ReadOnlySpan<byte> bytes, Func<bool>? accept)
    {
        if (bytes.IsEmpty && accept is null)
            return true;

        var compose = CurrentCompose();
        if (compose is not null)
        {
            if (accept is not null && !accept())
                return false;
            if (bytes.IsEmpty)
                return true;
            compose.Append(Encoding.UTF8.GetString(bytes));
            if (AttachPathTrace.IsEnabled)
                AttachPathTrace.NoteComposeAppend(compose.Slices, bytes.Length);
            return true;
        }

        BeforeWriteLock?.Invoke();
        lock (_writeGate)
        {
            if (accept is not null && !accept())
                return false;
            if (!bytes.IsEmpty)
                WriteBytesCore(bytes);
            return true;
        }
    }

    /// <summary>
    /// Capture TTY writes into a local buffer. Nested capture on this thread
    /// appends to the outer buffer and returns empty so the owner writes once.
    /// </summary>
    internal string CaptureWrites(Action paint) =>
        Encoding.UTF8.GetString(CaptureWritesPayload(paint).Bytes);

    internal ComposedPayload CaptureWritesPayload(Action paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        if (CurrentCompose() is not null)
        {
            paint();
            return ComposedPayload.Empty;
        }

        var context = new ComposeContext();
        _composeSlot.Value = context;
        try
        {
            DuringCompose?.Invoke();
            paint();
        }
        catch
        {
            // A failed paint must never leak attribution into the next payload.
            context.Slices.Clear();
            throw;
        }
        finally
        {
            _composeSlot.Value = null;
        }

        return new ComposedPayload(
            Encoding.UTF8.GetBytes(context.Buffer.ToString()),
            context.Slices.ToArray());
    }

    /// <summary>
    // / Write host-encoder bytes as-is.
    /// already wraps <c>CSI ?2026h/l</c>. Do not wrap again.
    /// </summary>
    internal bool WriteHostEncoded(
        ReadOnlySpan<byte> payload,
        Func<bool>? accept = null,
        Action? onAccepted = null,
        Func<byte[]?>? recover = null,
        string? route = null)
    {
        BeforeComposedWrite?.Invoke();
        BeforeWriteLock?.Invoke();
        lock (_writeGate)
        {
            DuringComposedWrite?.Invoke();
            ReadOnlySpan<byte> current = payload;
            byte[]? recoveredOwned = null;
            if (accept is not null && !accept())
            {
                recoveredOwned = recover?.Invoke();
                if (recoveredOwned is null || recoveredOwned.Length == 0)
                    return false;
                RecoveredComposeCount++;
                current = recoveredOwned;
            }

            if (current.Length == 0)
                return false;
            using (AttachPathTrace.PushOutput(route ?? AttachPathTrace.RouteChrome))
                WriteBytesCore(current);
            onAccepted?.Invoke();
            return true;
        }
    }

    /// <summary>
    /// Write already-composed paint bytes. Holds the TTY lock only for the
    /// 2026 wrapper and payload. <paramref name="accept"/> runs inside that lock
    /// before the write. <paramref name="recover"/> rebuilds bytes inside that
    /// lock when accept fails. <paramref name="onAccepted"/> runs after a
    /// successful write, still inside the lock.
    /// </summary>
    internal bool WriteComposedBytes(
        ReadOnlySpan<byte> payload,
        Func<bool>? accept = null,
        Action? onAccepted = null,
        Func<byte[]?>? recover = null)
    {
        return WriteComposedPayload(
            new ComposedPayload(payload.ToArray(), []),
            accept,
            onAccepted,
            recover is null ? null : () =>
            {
                var bytes = recover();
                return bytes is null ? null : new ComposedPayload(bytes, []);
            });
    }

    internal bool WriteComposedPayload(
        ComposedPayload payload,
        Func<bool>? accept = null,
        Action? onAccepted = null,
        Func<ComposedPayload?>? recover = null)
    {
        BeforeComposedWrite?.Invoke();
        BeforeWriteLock?.Invoke();
        lock (_writeGate)
        {
            DuringComposedWrite?.Invoke();
            ComposedPayload? recovered = null;
            var currentPayload = payload;
            if (accept is not null && !accept())
            {
                recovered = recover?.Invoke();
                if (recovered is null || recovered.Value.IsEmpty)
                    return false;
                RecoveredComposeCount++;
                currentPayload = recovered.Value;
            }

            if (currentPayload.IsEmpty)
                return false;
            var write = currentPayload.Bytes.AsSpan();
            var slices = currentPayload.Slices;
            if (_synchronizedPaintDepth == 0)
            {
                using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                    WriteBytesCore(Encoding.UTF8.GetBytes(SnapshotPainter.BeginSynchronizedOutput));
            }
            _synchronizedPaintDepth++;
            try
            {
                var payloadOffset = _hostBytesWritten;
                try
                {
                    WriteBytesCore(write, traceHost: slices.Count == 0);
                }
                finally
                {
                    // A native host write may accept a prefix before failing.
                    // Attribute only that accepted prefix; the unsent suffix
                    // must never appear in route evidence.
                    var accepted = checked((int)(_hostBytesWritten - payloadOffset));
                    if (slices.Count > 0 && accepted > 0)
                        AttachPathTrace.FlushComposeSlices(slices, payloadOffset, Math.Min(accepted, write.Length));
                }
            }
            finally
            {
                if (_synchronizedPaintDepth > 0)
                    _synchronizedPaintDepth--;
                if (_synchronizedPaintDepth == 0)
                {
                    using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                        WriteBytesCore(Encoding.UTF8.GetBytes(SnapshotPainter.EndSynchronizedOutput));
                }
            }

            onAccepted?.Invoke();
            return true;
        }
    }

    internal void WriteComposedPaint(Action paint, Func<bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(paint);
        if (CurrentCompose() is not null)
        {
            paint();
            return;
        }

        var payload = CaptureWritesPayload(paint);
        if (payload.IsEmpty)
            return;
        WriteComposedPayload(payload, accept);
    }

    internal bool IsComposing => CurrentCompose() is not null;

    private ComposeContext? CurrentCompose() =>
        _composeSlot.IsValueCreated ? _composeSlot.Value : null;

    private sealed class ComposeContext
    {
        public StringBuilder Buffer { get; } = new();
        public List<AttachPathTrace.ComposeSlice> Slices { get; } = [];

        public void Append(string value) => Buffer.Append(value);
    }

    internal readonly record struct ComposedPayload(
        byte[] Bytes,
        IReadOnlyList<AttachPathTrace.ComposeSlice> Slices)
    {
        public static ComposedPayload Empty => new([], []);
        public bool IsEmpty => Bytes.Length == 0;
    }

    private void WriteBytesCore(ReadOnlySpan<byte> bytes, bool traceHost = true)
    {
        if (bytes.IsEmpty)
            return;

        var offset = _hostBytesWritten;
        var accepted = 0;
        if (_capture is not null)
        {
            _capture.Write(bytes);
            accepted = bytes.Length;
            _hostBytesWritten += accepted;
            if (traceHost)
                AttachPathTrace.NoteHostWrite(offset, accepted);
            return;
        }

        var fd = _fd == 0 ? 1 : _fd;
        var remaining = bytes;
        try
        {
            while (!remaining.IsEmpty)
            {
                nint n;
                var errno = 0;
                if (_writer is { } writer)
                {
                    (n, errno) = writer();
                }
                else
                {
                    n = write(fd, ref MemoryMarshal.GetReference(remaining), (nuint)remaining.Length);
                    if (n < 0)
                        errno = Marshal.GetLastPInvokeError();
                }

                if (n < 0)
                    throw TtyWriteFailure.Create(n, errno);
                if (n == 0)
                    throw TtyWriteFailure.Create(n, errno);
                remaining = remaining.Slice((int)n);
                accepted += (int)n;
            }
        }
        finally
        {
            if (accepted > 0)
            {
                _hostBytesWritten += accepted;
                if (traceHost)
                    AttachPathTrace.NoteHostWrite(offset, accepted);
            }
        }
    }

    public bool TryGetSize(out int cols, out int rows)
    {
        if (_pinnedCols > 0 && _pinnedRows > 0)
        {
            cols = _pinnedCols;
            rows = _pinnedRows;
            return true;
        }

        cols = 80;
        rows = 24;
        var ws = new Winsize();
        var req = OperatingSystem.IsMacOS() ? (nuint)MacTiocgwinsz : (nuint)LinuxTiocgwinsz;
        var fd = _fd == 0 ? 1 : _fd;
        if (fd < 0)
            return false;
        if (Ioctl(fd, req, ref ws) != 0 || ws.Col == 0 || ws.Row == 0)
            return false;
        cols = ws.Col;
        rows = ws.Row;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Restore();
        _composeSlot.Dispose();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort Row;
        public ushort Col;
        public ushort Xpixel;
        public ushort Ypixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, ref byte buf, nuint count);

    private static bool DarwinStackVariadic =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    private static int Ioctl(int fd, nuint request, ref Winsize ws) =>
        DarwinStackVariadic
            ? ioctl_darwin_variadic(fd, request, 0, 0, 0, 0, 0, 0, ref ws)
            : ioctl(fd, request, ref ws);

    private static int Fcntl(int fd, int cmd, int arg) =>
        DarwinStackVariadic
            ? fcntl_darwin_variadic(fd, cmd, 0, 0, 0, 0, 0, 0, arg)
            : fcntl(fd, cmd, arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref Winsize ws);

    // Darwin arm64: ioctl is variadic; Apple passes variadic args on the stack.
    // Six pads exhaust x2-x7 so the winsize pointer lands at [sp+0].
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_darwin_variadic(
        int fd,
        nuint request,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        ref Winsize ws);

    private const short Pollin = 0x0001;
    private const short Pollerr = 0x0008;
    private const short Pollhup = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Pollfd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref Pollfd fds, uint nfds, int timeout);

    private const int FGetfl = 3;
    private const int FSetfl = 4;
    private const int LinuxONonblock = 0x0800;
    private const int MacONonblock = 0x0004;

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_darwin_variadic(
        int fd,
        int cmd,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        nint arg);
}
