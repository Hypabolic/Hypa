using System.Runtime.InteropServices;

namespace Hypa.AgentRuntime.Infrastructure.StandardStreams;

/// <summary>
/// A write-only stream on a Unix file descriptor that the stream does not
/// own. It calls <c>write</c> directly and does not start the .NET terminal
/// setup.
/// </summary>
/// <remarks>
/// The error handling is the same as the .NET console stream: a write that
/// a signal stops is tried again, a full non-blocking descriptor is polled
/// until it can take bytes, and a closed reader (<c>EPIPE</c>) or a closed
/// descriptor (<c>EBADF</c>) drops the bytes. Thus <c>hypa --help | head -1</c>
/// does not fail.
/// </remarks>
public sealed class UnixDescriptorWriteStream : Stream
{
    private const int Eintr = 4;
    private const int Ebadf = 9;
    private const int Epipe = 32;
    private const short PollOut = 0x0004;

    private readonly int _fd;

    public UnixDescriptorWriteStream(int fd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fd);
        _fd = fd;
    }

    private static int Eagain => OperatingSystem.IsLinux() ? 11 : 35;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var n = write(_fd, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);
            if (n > 0)
            {
                buffer = buffer[(int)n..];
                continue;
            }

            if (n == 0)
                return;

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Eintr)
                continue;
            if (errno == Epipe || errno == Ebadf)
                return;
            if (errno == Eagain)
            {
                WaitUntilWritable();
                continue;
            }

            throw new IOException($"write to descriptor {_fd} failed (errno {errno}).", errno);
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void WaitUntilWritable()
    {
        var fds = new PollFd { Fd = _fd, Events = PollOut };
        while (poll(ref fds, 1, -1) < 0 && Marshal.GetLastPInvokeError() == Eintr)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint nfds, int timeout);
}
