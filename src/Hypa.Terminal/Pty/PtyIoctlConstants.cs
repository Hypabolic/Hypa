namespace Hypa.Terminal.Pty;

/// <summary>
/// Platform-selected PTY ioctl request codes for Linux vs macOS.
/// Values match system headers (termios / ttycom), not a single hard-coded pair.
/// </summary>
public static class PtyIoctlConstants
{
    // Linux: include/uapi/asm-generic/ioctls.h
    public const int LinuxTIOCSCTTY = 0x540E;
    public const int LinuxTIOCSWINSZ = 0x5414;

    // macOS: sys/ttycom.h — TIOCSCTTY=_IO('t',97), TIOCSWINSZ=_IOW('t',103,struct winsize)
    public const int MacTIOCSCTTY = unchecked((int)0x20007461);
    public const int MacTIOCSWINSZ = unchecked((int)0x80087467);

    public static int TIOCSCTTYFor(bool isMacOs) => isMacOs ? MacTIOCSCTTY : LinuxTIOCSCTTY;

    public static int TIOCSWINSZFor(bool isMacOs) => isMacOs ? MacTIOCSWINSZ : LinuxTIOCSWINSZ;

    /// <summary>Selects constants for the current OS. Throws on non-Unix hosts.</summary>
    public static (int TIOCSCTTY, int TIOCSWINSZ) ForCurrentOs()
    {
        if (OperatingSystem.IsMacOS())
            return (MacTIOCSCTTY, MacTIOCSWINSZ);
        if (OperatingSystem.IsLinux())
            return (LinuxTIOCSCTTY, LinuxTIOCSWINSZ);
        throw new PlatformNotSupportedException("PTY ioctl constants require Linux or macOS.");
    }
}
