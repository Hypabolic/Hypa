using System.Diagnostics;
using System.Text;

namespace Hypa.Cli.Attach;

/// <summary>
/// Resets the host terminal modes when the attach process stops with no
/// chance to restore the terminal, for example after <c>kill -9</c>, a crash
/// or an out-of-memory kill. One guard serves one attach: it covers the
/// modes of the attach and the kitty keyboard flags.
/// </summary>
internal interface IHostModeGuard : IDisposable
{
    /// <summary>
    /// Start the guard. Returns false when the guard cannot run. The attach
    /// then does not push the kitty flags.
    /// </summary>
    bool Arm();

    /// <summary>
    /// The attach writes the kitty push next. From now on a crash also pops.
    /// Returns false when the guard is not running; the attach then does not push.
    /// </summary>
    bool NotePushing();

    /// <summary>
    /// The attach popped its kitty flags, or its push did not reach the host.
    /// A crash after this does not pop.
    /// </summary>
    void NotePopped();

    /// <summary>The attach restored the host terminal. The guard must write nothing.</summary>
    void Disarm();
}

/// <summary>
/// A <c>/bin/sh</c> child process that holds the read end of a pipe from the
/// attach. When the attach process dies, the kernel closes the write end. The
/// child then reads end of file and writes the restore bytes to the host
/// terminal: CAN, the kitty pop after a <c>pushed</c> line, and
/// <see cref="SnapshotPainter.HostModeResetSequence"/>. Then it runs
/// <c>stty sane</c> on the terminal. A clean restore sends <c>disarm</c>,
/// and the child exits with no write.
/// </summary>
/// <remarks>
/// <para>The child writes only after the attach process is dead. Thus it is
/// not a second host writer while the attach paints.</para>
/// <para>All three standard streams of the child are pipes. Thus .NET does not
/// change the terminal settings for a child that uses the terminal. The
/// child writes <c>ready</c> after it sets its traps, and <see cref="Arm"/>
/// waits for that line. Then the script sends its own output and errors to
/// <c>/dev/null</c>: after the attach dies, no process reads the pipes.</para>
/// <para>The child ignores SIGHUP, SIGINT and SIGQUIT. The death of a session
/// leader attach sends SIGHUP to the process group of the child. The child
/// also ignores SIGTTOU. After the attach dies, the shell takes the terminal
/// back, and the child is then in a background process group. Without this,
/// <c>stty sane</c> stops or fails.</para>
/// <para>The child is in the process group of the attach. A signal to the
/// full group (for example <c>kill -9 %1</c> from the shell) also stops the
/// child, and then nothing resets the host.</para>
/// <para>The guard does not clear the window title or the host status
/// report. <see cref="UnixRawTerminal.Restore"/> clears them only when the
/// attach set them, and the guard does not know that.</para>
/// <para>Two short gaps remain: a kill between the <c>pushed</c> line and the
/// push write, and a kill between the restore pop and the <c>popped</c> line.
/// Each can give one pop with no push. That pop goes to the alternate screen
/// of the attach, so it removes no flags of another program in the usual
/// case. A kill after the restore and before <c>disarm</c> writes the mode
/// resets a second time, with no pop.</para>
/// </remarks>
internal sealed class ShellHostModeGuard : IHostModeGuard
{
    internal const string DisarmLine = "disarm";

    internal const string PushedLine = "pushed";

    internal const string PoppedLine = "popped";

    internal const string ReadyLine = "ready";

    // The child has this long to set its traps and report ready.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    // $1: the bytes after a push. $2: the bytes with no push. $3: the terminal.
    // The child reports ready only after its traps are set.
    private const string Script =
        "trap '' HUP INT QUIT TTOU; echo " + ReadyLine + "; exec >/dev/null 2>&1; pushed=0; "
        + "while IFS= read -r line; do case \"$line\" in "
        + PushedLine + ") pushed=1 ;; " + PoppedLine + ") pushed=0 ;; "
        + DisarmLine + ") exit 0 ;; esac; done; "
        + "if [ \"$pushed\" = 1 ]; then printf '%b' \"$1\" > \"$3\"; "
        + "else printf '%b' \"$2\" > \"$3\"; fi; "
        + "PATH=\"$PATH:/bin:/usr/bin\" stty sane < \"$3\"";

    private readonly string _ttyPath;
    private readonly string _afterPush;
    private readonly string _withoutPush;
    private readonly object _gate = new();
    private Process? _child;

    /// <param name="ttyPath">The host terminal device. Tests pass a file.</param>
    internal ShellHostModeGuard(string ttyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ttyPath);
        _ttyPath = ttyPath;
        _afterPush = CrashBytes(pushed: true);
        _withoutPush = CrashBytes(pushed: false);
    }

    /// <summary>
    /// The bytes that the guard writes after a crash. The pop goes before
    /// 1049l: the host keeps one kitty stack for each screen, and the push
    /// went to the alternate screen.
    /// </summary>
    internal static string CrashBytes(bool pushed) =>
        SnapshotPainter.CancelSequence
        + (pushed ? HostKeyboardMode.PopSequence : "")
        + SnapshotPainter.HostModeResetSequence;

    /// <summary>The child process id, or 0 when the guard is not armed.</summary>
    internal int ChildProcessId
    {
        get
        {
            lock (_gate)
                return _child is { HasExited: false } child ? child.Id : 0;
        }
    }

    public bool Arm()
    {
        lock (_gate)
        {
            if (_child is not null)
                return true;
            if (!File.Exists("/bin/sh"))
                return false;

            var start = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(Script);
            start.ArgumentList.Add("hypa-host-mode-guard");
            start.ArgumentList.Add(OctalEscapes(_afterPush));
            start.ArgumentList.Add(OctalEscapes(_withoutPush));
            start.ArgumentList.Add(_ttyPath);
            Process? child;
            try
            {
                child = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                child = null;
            }
            catch (InvalidOperationException)
            {
                child = null;
            }

            if (child is null)
                return false;
            if (!WaitReady(child))
            {
                Stop(child);
                return false;
            }

            _child = child;
            return true;
        }
    }

    private static bool WaitReady(Process child)
    {
        try
        {
            var line = Task.Run(child.StandardOutput.ReadLine);
            return line.Wait(ReadyTimeout) && line.Result == ReadyLine;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    // Kill first: an end of file on its input makes the child write.
    private static void Stop(Process child)
    {
        try
        {
            if (!child.HasExited)
                child.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            child.Dispose();
        }
    }

    public bool NotePushing() => SendLine(PushedLine);

    public void NotePopped() => _ = SendLine(PoppedLine);

    private bool SendLine(string line)
    {
        lock (_gate)
        {
            if (_child is not { } child)
                return false;
            try
            {
                if (child.HasExited)
                    return false;
                child.StandardInput.WriteLine(line);
                child.StandardInput.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Disarm()
    {
        Process? child;
        lock (_gate)
        {
            child = _child;
            _child = null;
        }

        if (child is null)
            return;
        try
        {
            child.StandardInput.WriteLine(DisarmLine);
            child.StandardInput.Close();
            child.WaitForExit(500);
        }
        catch (IOException)
        {
            // The child is gone. It cannot write after this.
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>
    /// Test seam: close the pipe with no disarm line, as the kernel does when
    /// the attach process dies. The child then writes the restore bytes.
    /// </summary>
    internal void SimulateParentDeathForTests()
    {
        Process? child;
        lock (_gate)
        {
            child = _child;
            _child = null;
        }

        if (child is null)
            return;
        try
        {
            child.StandardInput.Close();
            child.WaitForExit(5000);
        }
        finally
        {
            child.Dispose();
        }
    }

    public void Dispose() => Disarm();

    private static string OctalEscapes(string sequence)
    {
        var builder = new StringBuilder(sequence.Length * 4);
        foreach (var ch in sequence)
        {
            if (ch is >= ' ' and <= '~' and not '\\' and not '%')
                builder.Append(ch);
            else
                builder.Append('\\').Append('0').Append(Convert.ToString(ch & 0xFF, 8).PadLeft(3, '0'));
        }

        return builder.ToString();
    }
}
