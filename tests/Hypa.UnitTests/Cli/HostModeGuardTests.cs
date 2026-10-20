using System.Runtime.InteropServices;
using System.Text;
using Hypa.Cli.Attach;
using Xunit;
using RecordingGuard = Hypa.UnitTests.Cli.HostKittyKeyboardTests.RecordingGuard;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The host mode guard of an attach. <see cref="UnixRawTerminal.EnterRaw"/>
/// arms one guard. A clean <see cref="UnixRawTerminal.Restore"/> disarms it.
/// When the attach process dies first, the guard child writes the kitty pop
/// (after a push) and the mode resets of the restore to the host terminal.
/// </summary>
public sealed class HostModeGuardTests
{
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Enter_raw_arms_one_guard_before_raw_mode()
    {
        var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var rawAtArm = new List<bool>();
        var guard = new RecordingGuard { OnArm = () => rawAtArm.Add(tty.IsRaw) };
        var made = 0;
        tty.ModeGuardFactory = () =>
        {
            made++;
            return guard;
        };

        tty.EnterRaw();
        tty.EnterRaw();

        Assert.True(tty.IsRaw);
        Assert.True(tty.ModeGuardArmed);
        Assert.Equal(1, made);
        Assert.Equal(1, guard.Armed);
        Assert.Equal([false], rawAtArm);
    }

    [Fact]
    public void Attach_takes_the_terminal_when_the_guard_cannot_start()
    {
        var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        tty.ModeGuardFactory = () => new RecordingGuard { CanArm = false };

        tty.EnterRaw();

        Assert.True(tty.IsRaw);
        Assert.False(tty.ModeGuardArmed);
    }

    [Fact]
    public void Clean_restore_disarms_the_guard_once()
    {
        var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();
        tty.EnterClientOverlay();
        tty.EnableFocusReport();

        tty.Restore();
        tty.Restore();
        tty.Dispose();

        Assert.Equal(1, guard.Disarmed);
        Assert.False(tty.ModeGuardArmed);
    }

    [Fact]
    public void Failed_restore_write_keeps_the_guard_armed()
    {
        var calls = 0;
        var tty = new UnixRawTerminal(() =>
        {
            calls++;
            // The overlay bytes go out. The restore fails: the host TTY is gone (EIO).
            return calls == 1 ? (SnapshotPainter.EnterAltScreen.Length + SnapshotPainter.DisableLineWrap.Length, 0) : (-1, 5);
        });
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();
        tty.EnterClientOverlay();

        tty.Restore();
        tty.Dispose();

        Assert.Equal(0, guard.Disarmed);
    }

    [Fact]
    public void Guard_writes_the_same_mode_resets_as_the_restore()
    {
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        tty.EnterRaw();
        tty.EnterClientOverlay();
        var before = capture.Length;

        tty.Restore();

        var restore = Encoding.ASCII.GetString(capture.ToArray(), (int)before, (int)(capture.Length - before));
        Assert.Equal(SnapshotPainter.CancelSequence + restore, ShellHostModeGuard.CrashBytes(pushed: false));
        Assert.Equal(
            SnapshotPainter.CancelSequence + HostKeyboardMode.PopSequence + restore,
            ShellHostModeGuard.CrashBytes(pushed: true));
    }

    [SkippableFact]
    public void Guard_writes_the_mode_resets_when_the_attach_dies()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var guard = new ShellHostModeGuard(target);
            Assert.True(guard.Arm());
            Assert.NotEqual(0, guard.ChildProcessId);

            guard.SimulateParentDeathForTests();

            Assert.Equal(Ascii(ShellHostModeGuard.CrashBytes(pushed: false)), File.ReadAllBytes(target));
        });
    }

    [SkippableFact]
    public void Guard_pops_the_kitty_flags_first_after_a_push()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var guard = new ShellHostModeGuard(target);
            Assert.True(guard.Arm());
            Assert.True(guard.NotePushing());

            guard.SimulateParentDeathForTests();

            Assert.Equal(Ascii(ShellHostModeGuard.CrashBytes(pushed: true)), File.ReadAllBytes(target));
        });
    }

    [SkippableFact]
    public void Guard_does_not_pop_after_the_attach_popped()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var guard = new ShellHostModeGuard(target);
            Assert.True(guard.Arm());
            Assert.True(guard.NotePushing());
            guard.NotePopped();

            guard.SimulateParentDeathForTests();

            Assert.Equal(Ascii(ShellHostModeGuard.CrashBytes(pushed: false)), File.ReadAllBytes(target));
        });
    }

    [SkippableFact]
    public void Guard_survives_the_hangup_of_a_session_leader_attach()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var guard = new ShellHostModeGuard(target);
            Assert.True(guard.Arm());
            var pid = guard.ChildProcessId;
            Assert.NotEqual(0, pid);
            // Arm returns after the child set its traps.
            Assert.Equal(0, kill(pid, Sighup));
            Assert.NotEqual(0, guard.ChildProcessId);

            // The signal is pending when kill returns, and the child takes it
            // before it runs again. The child next runs to read the closed
            // pipe below. Only a child that lived through the hangup can then
            // write the restore bytes.
            guard.SimulateParentDeathForTests();

            Assert.Equal(Ascii(ShellHostModeGuard.CrashBytes(pushed: false)), File.ReadAllBytes(target));
        });
    }

    [SkippableFact]
    public void Disarmed_guard_writes_nothing()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var guard = new ShellHostModeGuard(target);
            Assert.True(guard.Arm());
            Assert.True(guard.NotePushing());

            guard.Disarm();

            Assert.Empty(File.ReadAllBytes(target));
        });
    }

    [SkippableFact]
    public void Clean_restore_of_a_real_guard_leaves_only_the_restore_bytes()
    {
        SkipUnlessUnix();
        WithTarget(target =>
        {
            var capture = new MemoryStream();
            var tty = new UnixRawTerminal(capture, 80, 24);
            var guard = new ShellHostModeGuard(target);
            tty.ModeGuardFactory = () => guard;
            tty.EnterRaw();
            Assert.NotEqual(0, guard.ChildProcessId);
            tty.EnterClientOverlay();
            tty.EnableFocusReport();
            Assert.True(tty.PushKittyKeyboard());

            tty.Restore();
            tty.Dispose();

            Assert.Equal(0, guard.ChildProcessId);
            Assert.Empty(File.ReadAllBytes(target));
            var text = Encoding.ASCII.GetString(capture.ToArray());
            Assert.Equal(1, Count(text, SnapshotPainter.DisableFocusReport));
            Assert.Equal(1, Count(text, HostKeyboardMode.PopSequence));
        });
    }

    private static void SkipUnlessUnix() =>
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "The guard is a /bin/sh child.");

    private static void WithTarget(Action<string> body)
    {
        var target = Path.Combine(Path.GetTempPath(), $"hypa-mode-guard-{Guid.NewGuid():N}");
        File.WriteAllBytes(target, []);
        try
        {
            body(target);
        }
        finally
        {
            File.Delete(target);
        }
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }

    private const int Sighup = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
