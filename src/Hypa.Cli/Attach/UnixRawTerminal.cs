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
    private readonly bool _nativeSeam;
    private readonly HostOutputWriter _output;
    private readonly object _writeGate = new();
    private int _pinnedCols;
    private int _pinnedRows;
    private bool _raw;
    private bool _disposed;
    private bool _altEntered;
    private bool _clientOverlay;
    private bool _windowTitleApplied;
    private string? _lastWindowTitle;
    private string? _lastHostStatus;
    private int _synchronizedPaintDepth;

    // HostBytesWritten after the last accepted host-encoded paint. A no-op
    // paint skips its write only when no other write was accepted since.
    private long _hostEncodedMark = -1;
    private bool _wrapDisabled;
    private bool _hostOutputClosed;

    // One kitty keyboard entry that this attach pushed on the host stack, and
    // the guard that resets the host modes (and pops that entry) after a
    // crash. The write gate guards both.
    private bool _kittyKeyboardPushed;
    private IHostModeGuard? _modeGuard;
    private Func<IHostModeGuard?>? _modeGuardFactory;

    // Set under the write gate when a restore starts. No push can follow the
    // restore sequence of a signal handler.
    private bool _keyboardModeClosed;
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

    /// <summary>
    /// Host TTY bytes that the host writer accepted in this instance. The
    /// host writer writes them in this order. Bytes in its queue count.
    /// </summary>
    internal long HostBytesWritten => _output.Accepted;

    /// <summary>
    /// Faults when a write to the host terminal fails on the writer thread.
    /// It never completes without a failure. The attach ends on it.
    /// </summary>
    internal Task HostWriteFaulted => _output.Faulted;

    /// <summary>Accepted host bytes that the host terminal did not take yet.</summary>
    internal long HostBytesPending => _output.Pending;

    /// <summary>
    /// Completes when the host terminal took every host byte accepted before
    /// this call. Later writes do not delay it. Faults when a write failed.
    /// Do not await it under a paint lock.
    /// </summary>
    internal Task WhenHostOutputWrittenAsync(CancellationToken ct) => _output.WhenWrittenAsync(ct);

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
        var outFd = _fd == 0 ? 1 : _fd;
        _output = HostOutputWriter.Threaded(bytes => NativeWrite(outFd, bytes));
        // The device path, not /dev/tty: when a session leader attach dies,
        // the guard has no controlling terminal, but the device stays open.
        var ttyPath = TtyPathOf(outFd) ?? TtyPathOf(_fd) ?? "/dev/tty";
        _modeGuardFactory = () => new ShellHostModeGuard(ttyPath);
    }

    /// <summary>
    /// Makes the guard that <see cref="EnterRaw"/> arms. Only the terminal
    /// constructor sets a real guard. The test constructors have none, so a
    /// test never starts a guard that writes to the terminal of the developer.
    /// </summary>
    internal Func<IHostModeGuard?>? ModeGuardFactory
    {
        get => _modeGuardFactory;
        set => _modeGuardFactory = value;
    }

    /// <summary>True while a host mode guard is armed for this attach.</summary>
    internal bool ModeGuardArmed
    {
        get { lock (_writeGate) return _modeGuard is not null; }
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
        _output = HostOutputWriter.Inline(bytes =>
        {
            capture.Write(bytes);
            return bytes.Length;
        });
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
        : this(WriterSeamOf(writer))
    {
    }

    /// <summary>
    /// Native writer seam for tests. It gets the bytes of each write and
    /// returns the native write result and errno. It runs on the host writer
    /// thread, as the native write to fd 1 does.
    /// </summary>
    internal UnixRawTerminal(HostWriteSeam writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _fd = -1;
        _original = [];
        _owned = true;
        _hasTermios = false;
        _nativeSeam = true;
        _output = HostOutputWriter.Threaded(bytes =>
        {
            var (n, errno) = writer(bytes);
            return CheckedWriteCount(n, errno);
        });
    }

    /// <summary>One native write: returns the count and errno.</summary>
    internal delegate (nint n, int errno) HostWriteSeam(ReadOnlySpan<byte> bytes);

    private static HostWriteSeam WriterSeamOf(Func<(nint n, int errno)> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return _ => writer();
    }

    private const int Eintr = 4;

    private static int NativeWrite(int fd, ReadOnlySpan<byte> bytes)
    {
        while (true)
        {
            var n = write(fd, ref MemoryMarshal.GetReference(bytes), (nuint)bytes.Length);
            var errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
            // A signal stopped the write before it took a byte. The host
            // writer keeps a failure, so do not report this one: try again.
            if (n < 0 && errno == Eintr)
                continue;
            return CheckedWriteCount(n, errno);
        }
    }

    private static int CheckedWriteCount(nint n, int errno)
    {
        if (n <= 0)
            throw TtyWriteFailure.Create(n, errno);
        return (int)n;
    }

    public bool IsRaw => _raw;

    public void EnterRaw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_raw)
            return;

        // Arm the guard before raw mode: a kill right after the change must
        // still reset the host. With no guard the attach still runs.
        ArmModeGuard();
        if (_hasTermios)
        {
            var raw = (byte[])_original.Clone();
            cfmakeraw(raw);
            if (tcsetattr(_fd, Tcsanow, raw) != 0)
            {
                DisarmModeGuard();
                throw new InvalidOperationException("tcsetattr raw failed.");
            }
        }

        _raw = true;
        lock (_writeGate)
            _keyboardModeClosed = false;
        AttachPathTrace.SetHostTtyOwned(true);
        if (!_nativeSeam)
        {
            using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                WriteBytes(SnapshotPainter.DisableLineWrap);
            _wrapDisabled = true;
        }
    }

    /// <summary>
    /// Stops every later host write except <see cref="Restore"/>. It takes
    /// the write lock, so a write call in progress ends first. The bytes that
    /// the host writer accepted before still go to the host, in order and
    /// before the restore bytes. The attach calls it when the user leaves
    /// while work that can still paint runs.
    /// </summary>
    internal void CloseHostOutput()
    {
        lock (_writeGate)
            _hostOutputClosed = true;
    }

    internal bool HostOutputClosed
    {
        get { lock (_writeGate) return _hostOutputClosed; }
    }

    public void Restore()
    {
        if (!_owned || _disposed)
            return;

        var wasHostTtyOwner = _raw;
        var wrapEnabled = false;
        var restoreWritten = false;
        try
        {
            lock (_writeGate)
            {
                _keyboardModeClosed = true;
                // The paint bytes that the host writer holds go out first.
                // A failed paint does not stop the restore: the restore
                // bytes try the host again.
                try { _output.Drain(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                var writeFailed = _output.ClearFault();
                // A failed composed paint must not leave the host in
                // synchronized mode. After a failed write the host writer
                // dropped the queued bytes, which can hold the end of a
                // synchronized paint.
                var hadOpenSynchronizedPaint = _synchronizedPaintDepth > 0 || writeFailed;
                _synchronizedPaintDepth = 0;
                using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                {
                    if (hadOpenSynchronizedPaint)
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EndSynchronizedOutput));
                    // Pop before 1049l: the host keeps one kitty stack for
                    // each screen, and the push went to the alternate screen.
                    if (_kittyKeyboardPushed)
                    {
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(HostKeyboardMode.PopSequence));
                        _kittyKeyboardPushed = false;
                        // A failed write after this point keeps the guard
                        // armed. It must then not pop a second time.
                        _modeGuard?.NotePopped();
                    }

                    if (ClientOverlay.ShouldWriteRestoreSequence(_clientOverlay))
                    {
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.RestoreSequence));
                        wrapEnabled = _wrapDisabled;
                    }
                    else if (_wrapDisabled)
                    {
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EnableLineWrap));
                        wrapEnabled = true;
                    }
                    if (wasHostTtyOwner)
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.NormalCursorAndKeypad));
                    if (_windowTitleApplied)
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(WindowTitleApplier.ClearSequence));
                    // The report must not outlive the attach.
                    if (_lastHostStatus is not null)
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(HostStatusReport.Clear));
                }

                restoreWritten = true;
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
                        _ = _output.ClearFault();
                        WriteBytesNow(System.Text.Encoding.UTF8.GetBytes(SnapshotPainter.EnableLineWrap));
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

        var cookedRestored = true;
        if (_hasTermios && _raw)
            cookedRestored = tcsetattr(_fd, Tcsanow, _original) == 0;

        // O_NONBLOCK is per open-file-description and is inherited by the shell.
        // InterruptStdin used to set it and never clear it; the next attach then
        // saw EAGAIN as hangup and printed Detached immediately.
        if (_fd >= 0)
            SetNonBlocking(_fd, enabled: false);

        // Disarm only after cooked mode is back. A kill before this line
        // writes the mode resets a second time, which does no harm. A failed
        // restore write or a failed termios restore keeps the guard armed:
        // it writes the resets and runs stty sane when this process ends.
        if (wasHostTtyOwner && restoreWritten && cookedRestored)
            DisarmModeGuard();

        _raw = false;
        if (wasHostTtyOwner)
            AttachPathTrace.SetHostTtyOwned(false);
        _altEntered = false;
        _clientOverlay = false;
        _windowTitleApplied = false;
        _lastWindowTitle = null;
        _lastHostStatus = null;
        if (wrapEnabled)
            _wrapDisabled = false;
        _overlayCarry.Clear();
    }

    public bool AltScreenEntered => _altEntered;

    /// <summary>This attach has one kitty keyboard entry on the host stack.</summary>
    internal bool KittyKeyboardPushed
    {
        get { lock (_writeGate) return _kittyKeyboardPushed; }
    }

    /// <summary>
    /// Push kitty flag 1 on the host. Call it once, after the host answered
    /// <c>CSI ? u</c>. A second call does nothing. <see cref="Restore"/>
    /// pops the entry. The host mode guard that <see cref="EnterRaw"/> armed
    /// pops it if the process dies before that. With no armed guard there is
    /// no push: a crash must not leave the host in kitty mode.
    /// </summary>
    /// <returns>True when this call wrote the push.</returns>
    internal bool PushKittyKeyboard()
    {
        lock (_writeGate)
        {
            if (!CanPushKeyboardModeUnlocked() || _modeGuard is not { } guard)
                return false;
            // Tell the guard first: a crash right after the push must pop.
            if (!guard.NotePushing())
                return false;
            try
            {
                // Wait for the host: the push counts only when the host took it.
                using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                    WriteBytesNow(Encoding.UTF8.GetBytes(HostKeyboardMode.PushSequence));
                _kittyKeyboardPushed = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The host did not get the push. The guard stays armed for
                // the other modes, but it must not pop.
                guard.NotePopped();
                return false;
            }
        }
    }

    private bool CanPushKeyboardModeUnlocked() =>
        !_kittyKeyboardPushed && !_hostOutputClosed && !_keyboardModeClosed && _raw;

    /// <summary>
    /// Set the kitty flags again after the host can have reset its modes
    /// (focus-in, a resize, a reconnect). A pop and a push replace the entry
    /// of this attach, so the host stack does not grow. On a host that reset
    /// its stack, the pop has no effect and the push restores the flags.
    /// </summary>
    internal void RefreshKittyKeyboard()
    {
        lock (_writeGate)
        {
            if (!_kittyKeyboardPushed || _hostOutputClosed || _keyboardModeClosed)
                return;
            // A signal handler calls this. A failed write must not end the
            // process there: the next host write reports a dead terminal.
            try
            {
                using (AttachPathTrace.PushOutput(AttachPathTrace.RouteHostMode))
                {
                    WriteBytesCore(Encoding.UTF8.GetBytes(
                        HostKeyboardMode.PopSequence + HostKeyboardMode.PushSequence));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        }
    }

    private void ArmModeGuard()
    {
        lock (_writeGate)
        {
            // A guard that a failed restore kept armed serves again.
            if (_modeGuard is not null)
                return;
        }

        // The guard starts a process, so not under the write lock.
        if (_modeGuardFactory?.Invoke() is not { } guard)
            return;
        bool armed;
        try { armed = guard.Arm(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            armed = false;
        }

        if (!armed)
        {
            guard.Dispose();
            return;
        }

        lock (_writeGate)
            _modeGuard = guard;
    }

    private void DisarmModeGuard()
    {
        IHostModeGuard? guard;
        lock (_writeGate)
        {
            guard = _modeGuard;
            _modeGuard = null;
        }

        if (guard is null)
            return;
        try { guard.Disarm(); }
        catch { /* the restore must finish */ }
    }

    public bool ClientOverlayActive => _clientOverlay;

    internal ClientOverlayCarry OverlayCarry => _overlayCarry;

    public void EnableFocusReport()
    {
        WriteBytes(SnapshotPainter.EnableFocusReport);
    }

    /// <summary>
    /// Ask the host to frame pastes with <c>CSI 200~</c>/<c>CSI 201~</c> so
    /// pasted text bypasses keybindings and reaches the pane as one paste.
    /// </summary>
    public void EnableBracketedPaste()
    {
        WriteBytes(SnapshotPainter.EnableBracketedPaste);
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
            if (_hostOutputClosed)
                return;
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

    /// <summary>
    /// Writes a program status report (OSC 7501) once per changed report.
    /// A clear goes out only after a report. Restore sends the final clear.
    /// This is the one host writer for the report. It joins the host frame when one is open.
    /// </summary>
    internal void WriteHostStatus(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
            return;
        var compose = CurrentCompose();
        if (compose is not null)
        {
            if (!TryNoteHostStatus(sequence))
                return;
            compose.Append(sequence);
            if (AttachPathTrace.IsEnabled)
                AttachPathTrace.NoteComposeAppend(compose.Slices, Encoding.UTF8.GetByteCount(sequence));
            return;
        }

        lock (_writeGate)
        {
            if (_hostOutputClosed)
                return;
            if (!TryNoteHostStatus(sequence))
                return;
            WriteBytesCore(System.Text.Encoding.UTF8.GetBytes(sequence));
        }
    }

    // True when the report is new. A repeat, or a clear with no report before it, is not.
    private bool TryNoteHostStatus(string sequence)
    {
        var isClear = string.Equals(sequence, HostStatusReport.Clear, StringComparison.Ordinal);
        if (isClear)
        {
            if (_lastHostStatus is null)
                return false;
            _lastHostStatus = null;
            return true;
        }

        if (string.Equals(_lastHostStatus, sequence, StringComparison.Ordinal))
            return false;
        _lastHostStatus = sequence;
        return true;
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
            if (_hostOutputClosed)
                return false;
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
    /// <paramref name="accept"/> and <paramref name="onAccepted"/> run under
    /// the write lock. <paramref name="onAccepted"/> runs after the host
    /// writer accepted the bytes into its queue, before the host terminal
    /// takes them. The call does not wait for the host terminal.
    /// A <paramref name="noOp"/> paint changes nothing on the host. It skips
    /// the terminal write and still runs <paramref name="accept"/> and
    /// <paramref name="onAccepted"/> under the write lock. A retry from
    /// <paramref name="recover"/> can be a no-op too.
    /// </summary>
    internal bool WriteHostEncoded(
        ReadOnlySpan<byte> payload,
        Func<bool>? accept = null,
        Action? onAccepted = null,
        Func<HostWriteRetry?>? recover = null,
        string? route = null,
        bool noOp = false)
    {
        BeforeComposedWrite?.Invoke();
        BeforeWriteLock?.Invoke();
        lock (_writeGate)
        {
            DuringComposedWrite?.Invoke();
            if (_hostOutputClosed)
                return false;
            ReadOnlySpan<byte> current = payload;
            if (accept is not null && !accept())
            {
                if (recover?.Invoke() is not { } retry || retry.Bytes.IsEmpty)
                    return false;

                RecoveredComposeCount++;
                current = retry.Bytes.Span;
                noOp = retry.NoOp;
            }

            // Another writer can move the cursor or change the screen. Then
            // the no-op paint writes its cursor bytes.
            if (!noOp || HostBytesWritten != _hostEncodedMark)
            {
                if (current.Length == 0)
                    return false;
                using (AttachPathTrace.PushOutput(route ?? AttachPathTrace.RouteChrome))
                    WriteBytesCore(current);
            }

            onAccepted?.Invoke();
            _hostEncodedMark = HostBytesWritten;
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
            if (_hostOutputClosed)
                return false;
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
                // The host writer records only the prefix that the host
                // took. The unsent suffix never appears in route evidence.
                WriteBytesCore(write, traceHost: slices.Count == 0, slices: slices.Count > 0 ? slices : null);
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

    /// <summary>
    /// Hands <paramref name="bytes"/> to the one host writer, after all
    /// bytes handed to it before. A host terminal writes them on the writer
    /// thread, so this call does not wait for the host terminal.
    /// </summary>
    private void WriteBytesCore(
        ReadOnlySpan<byte> bytes,
        bool traceHost = true,
        IReadOnlyList<AttachPathTrace.ComposeSlice>? slices = null)
    {
        if (bytes.IsEmpty)
            return;
        _ = _output.Submit(bytes, traceHost, slices);
    }

    /// <summary>
    /// Like <see cref="WriteBytesCore"/>, then waits until the host terminal
    /// took the bytes. Throws when a write failed. For the writes whose
    /// result the caller needs: the restore and the kitty keyboard push.
    /// </summary>
    private void WriteBytesNow(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        _output.SubmitAndWait(bytes);
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
        _output.Dispose();
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
    private static extern int ttyname_r(int fd, byte[] buf, nuint len);

    /// <summary>The device path of the terminal on <paramref name="fd"/>, or null.</summary>
    internal static string? TtyPathOf(int fd)
    {
        if (fd < 0)
            return null;
        var buf = new byte[1024];
        if (ttyname_r(fd, buf, (nuint)buf.Length) != 0)
            return null;
        var end = Array.IndexOf(buf, (byte)0);
        if (end <= 0)
            return null;
        return Encoding.UTF8.GetString(buf, 0, end);
    }

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

/// <summary>A host paint that the writer encoded again after a failed accept.</summary>
internal readonly record struct HostWriteRetry(ReadOnlyMemory<byte> Bytes, bool NoOp);
