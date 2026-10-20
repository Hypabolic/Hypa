using System.Text;

namespace Hypa.AgentRuntime.Infrastructure.StandardStreams;

/// <summary>
/// Sets <see cref="Console.Out"/> and <see cref="Console.Error"/> to writers
/// on the raw standard descriptors.
/// </summary>
/// <remarks>
/// On Unix, the first write through a .NET console stream starts the .NET
/// terminal setup. That setup writes the terminfo <c>keypad_xmit</c> string
/// (<c>CSI ? 1 h ESC =</c>) to the terminal, and .NET never writes
/// <c>keypad_local</c>. After a short command, the arrow keys of the shell
/// then send <c>ESC O A</c> in place of <c>ESC [ A</c>. A writer on the raw
/// descriptor does not start that setup, so the command writes only its own
/// bytes. Call <see cref="Install"/> as the first statement of each process
/// entry point. On Windows it does nothing.
/// </remarks>
public static class StandardStreamWriters
{
    public const int StandardOutput = 1;
    public const int StandardError = 2;

    public static void Install()
    {
        if (OperatingSystem.IsWindows())
            return;

        var encoding = WithoutPreamble(Console.OutputEncoding);
        Console.SetOut(Create(StandardOutput, encoding));
        Console.SetError(Create(StandardError, encoding));
    }

    /// <summary>
    /// A synchronized, auto-flushed writer on <paramref name="fd"/>. The
    /// writer does not close the descriptor.
    /// </summary>
    public static TextWriter Create(int fd, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var writer = new StreamWriter(new UnixDescriptorWriteStream(fd), WithoutPreamble(encoding), bufferSize: 256, leaveOpen: false)
        {
            AutoFlush = true,
        };
        return TextWriter.Synchronized(writer);
    }

    // The .NET console writer does not write a byte order mark. A writer on
    // a stream that cannot seek writes the preamble of its encoding first.
    // A console encoding with a preamble (UTF-8 with a BOM, UTF-16, UTF-32)
    // becomes UTF-8 without a BOM.
    private static Encoding WithoutPreamble(Encoding encoding) =>
        encoding.Preamble.Length == 0
            ? encoding
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
