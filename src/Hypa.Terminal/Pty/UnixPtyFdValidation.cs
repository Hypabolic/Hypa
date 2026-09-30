using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hypa.Terminal.Pty;

/// <summary>
/// AOT-safe helpers for SCM_RIGHTS hygiene: PTY-master validation and FD_CLOEXEC.
/// Applied on ownership transfer boundaries (receive / adopt), not as a Stream fiction.
/// </summary>
internal static class UnixPtyFdValidation
{
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    // Linux: include/uapi/asm-generic/ioctls.h — TIOCGPTN = _IOR('T', 30, unsigned int)
    private const int LinuxTIOCGPTN = unchecked((int)0x80045430);

    /// <summary>
    /// True when <paramref name="fd"/> is a PTY master (not a pipe, file, or slave-only tty).
    /// Linux uses TIOCGPTN; macOS uses ptsname_r on the master.
    /// </summary>
    public static bool IsPtyMaster(int fd)
    {
        if (fd < 0)
            return false;

        if (OperatingSystem.IsLinux())
        {
            uint ptyNo = 0;
            return ioctl_uint(fd, LinuxTIOCGPTN, ref ptyNo) == 0;
        }

        if (OperatingSystem.IsMacOS())
        {
            // ptsname_r succeeds only for the master side of a PTY pair.
            var buf = new byte[128];
            return ptsname_r(fd, buf, (UIntPtr)buf.Length) == 0;
        }

        return false;
    }

    /// <summary>
    /// Close <paramref name="fd"/> and throw when it is not a PTY master.
    /// Call only when the caller does not already own a SafeFileHandle for the FD.
    /// </summary>
    public static void EnsurePtyMasterOrClose(int fd)
    {
        if (IsPtyMaster(fd))
            return;

        try { _ = close(fd); }
        catch { /* preserve primary error */ }

        throw new IOException(
            $"SCM_RIGHTS descriptor {fd} is not a PTY master (rejected non-PTY adoption).");
    }

    /// <summary>Set FD_CLOEXEC on a live descriptor. Throws on fcntl failure.</summary>
    public static void SetCloexec(int fd)
    {
        if (fd < 0)
            throw new ArgumentOutOfRangeException(nameof(fd));

        var flags = Fcntl(fd, F_GETFD, 0);
        if (flags < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"fcntl(F_GETFD) failed errno={err}");
        }

        if ((flags & FD_CLOEXEC) != 0)
            return;

        if (Fcntl(fd, F_SETFD, flags | FD_CLOEXEC) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"fcntl(F_SETFD, FD_CLOEXEC) failed errno={err}");
        }
    }

    /// <summary>True when FD_CLOEXEC is set (test / diagnostic helper).</summary>
    public static bool HasCloexec(int fd)
    {
        if (fd < 0)
            return false;
        var flags = Fcntl(fd, F_GETFD, 0);
        return flags >= 0 && (flags & FD_CLOEXEC) != 0;
    }

    // Darwin arm64: fcntl is variadic; Apple passes variadic args on the stack.
    // Six pads exhaust x2-x7 so the flags arg lands at [sp+0].
    // Same sequence as UnixRawTerminal.Fcntl.
    private static bool DarwinStackVariadic =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    private static int Fcntl(int fd, int cmd, int arg) =>
        DarwinStackVariadic
            ? fcntl_darwin_variadic(fd, cmd, 0, 0, 0, 0, 0, 0, arg)
            : fcntl(fd, cmd, arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_darwin_variadic(
        int fd,
        int cmd,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        nint arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_uint(int fd, int request, ref uint arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, byte[] buf, UIntPtr buflen);
}
