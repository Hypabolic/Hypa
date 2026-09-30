using System.Runtime.InteropServices;
using System.Text;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
/// Plugin-owned raw TTY. Does not import <c>Hypa.Cli</c> terminal types.
/// </summary>
internal sealed class PluginRawTerminal : IDisposable
{
    private const int Tcsanow = 0;
    private const int TermiosBytes = 256;
    private const int LinuxTiocgwinsz = 0x5413;
    private const int MacTiocgwinsz = unchecked((int)0x40087468);

    private readonly int _fd;
    private readonly byte[] _original;
    private bool _raw;
    private bool _disposed;

    private PluginRawTerminal(int fd, byte[] original)
    {
        _fd = fd;
        _original = original;
    }

    public static PluginRawTerminal? TryOpen()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return null;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return null;

        var original = new byte[TermiosBytes];
        if (tcgetattr(0, original) != 0)
            return null;
        return new PluginRawTerminal(0, original);
    }

    public void EnterRaw()
    {
        if (_raw)
            return;
        var raw = (byte[])_original.Clone();
        cfmakeraw(raw);
        if (tcsetattr(_fd, Tcsanow, raw) != 0)
            throw new InvalidOperationException("tcsetattr raw failed.");
        _raw = true;
    }

    public void Restore()
    {
        if (!_raw)
            return;
        _ = tcsetattr(_fd, Tcsanow, _original);
        _raw = false;
    }

    public (int Cols, int Rows) Size(int fallbackCols, int fallbackRows)
    {
        var buf = new byte[8];
        var request = OperatingSystem.IsMacOS() ? MacTiocgwinsz : LinuxTiocgwinsz;
        if (ioctl(_fd, request, buf) != 0)
            return (fallbackCols, fallbackRows);
        var rows = BitConverter.ToUInt16(buf, 0);
        var cols = BitConverter.ToUInt16(buf, 2);
        if (cols == 0 || rows == 0)
            return (fallbackCols, fallbackRows);
        return (cols, rows);
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _ = write(_fd == 0 ? 1 : _fd, bytes, (nuint)bytes.Length);
    }

    public AnnotateKey? ReadKey()
    {
        var first = ReadByte();
        if (first < 0)
            return null;
        return AnsiKeyReader.Decode(first, ReadByte);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Restore();
        _disposed = true;
    }

    private int ReadByte()
    {
        var buf = new byte[1];
        var n = read(_fd, buf, 1);
        return n <= 0 ? -1 : buf[0];
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, int request, byte[] buf);
}
