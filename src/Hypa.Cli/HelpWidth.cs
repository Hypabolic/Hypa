using System.CommandLine;
using System.CommandLine.Help;
using System.Runtime.InteropServices;

namespace Hypa.Cli;

/// <summary>
/// Gives the help of a root command a width from the terminal, so the help
/// does not read <see cref="Console.WindowWidth"/>.
/// </summary>
/// <remarks>
/// System.CommandLine reads <see cref="Console.WindowWidth"/> for the help
/// width when stdout is a terminal. On Unix that read starts the .NET
/// terminal setup, which writes <c>keypad_xmit</c> (<c>CSI ? 1 h ESC =</c>)
/// and never resets it. A parse error also writes the help.
/// </remarks>
internal static class HelpWidth
{
    private const int FallbackColumns = 80;

    public static RootCommand Apply(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (OperatingSystem.IsWindows() || Console.IsOutputRedirected)
            return root;

        var columns = TerminalColumns.TryRead(StandardOutputFd) ?? FallbackColumns;
        foreach (var option in root.Options)
        {
            if (option is HelpOption { Action: HelpAction help })
                help.MaxWidth = columns;
        }

        return root;
    }

    private const int StandardOutputFd = 1;
}

/// <summary>The column count of a terminal descriptor, from <c>TIOCGWINSZ</c>.</summary>
internal static class TerminalColumns
{
    private const uint LinuxTiocgwinsz = 0x5413;
    private const uint MacTiocgwinsz = 0x40087468;

    public static int? TryRead(int fd)
    {
        if (OperatingSystem.IsWindows())
            return null;
        var ws = default(Winsize);
        var request = OperatingSystem.IsMacOS() ? (nuint)MacTiocgwinsz : (nuint)LinuxTiocgwinsz;
        var rc = DarwinStackVariadic
            ? ioctl_darwin_variadic(fd, request, 0, 0, 0, 0, 0, 0, ref ws)
            : ioctl(fd, request, ref ws);
        return rc == 0 && ws.Col > 0 ? ws.Col : null;
    }

    private static bool DarwinStackVariadic =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref Winsize ws);

    // Darwin arm64: ioctl is variadic, and Apple passes variadic arguments on
    // the stack. Six pads fill x2-x7, so the winsize pointer is at [sp+0].
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_darwin_variadic(
        int fd,
        nuint request,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        ref Winsize ws);
}
