using System.Runtime.InteropServices;
using System.Text;
using Hypa.AgentRuntime.Infrastructure.StandardStreams;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The raw descriptor writer that replaces the .NET console writers on Unix.
/// Each test uses a real pipe.
/// </summary>
public sealed class UnixDescriptorWriteStreamTests
{
    [SkippableFact]
    public async Task A_large_write_reaches_the_reader_complete()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix descriptors only.");
        var (readFd, writeFd) = Pipe();
        var payload = new byte[1024 * 1024 + 7];
        new Random(402).NextBytes(payload);

        using var reader = new FileStream(new SafeFileHandle(readFd, ownsHandle: true), FileAccess.Read, 1);
        var readAll = Task.Run(async () =>
        {
            using var copy = new MemoryStream();
            await reader.CopyToAsync(copy);
            return copy.ToArray();
        });

        var write = Task.Run(() =>
        {
            using (var stream = new UnixDescriptorWriteStream(writeFd))
                stream.Write(payload);
            Assert.Equal(0, close(writeFd));
        });

        await write.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(payload, await readAll.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [SkippableFact]
    public void A_write_after_the_reader_closes_drops_the_bytes_and_does_not_throw()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix descriptors only.");
        var (readFd, writeFd) = Pipe();
        Assert.Equal(0, close(readFd));
        try
        {
            using var stream = new UnixDescriptorWriteStream(writeFd);
            stream.Write("after the reader closed\n"u8);
            stream.Flush();
        }
        finally
        {
            _ = close(writeFd);
        }
    }

    [SkippableFact]
    public void The_text_writer_writes_no_byte_order_mark()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix descriptors only.");
        var (readFd, writeFd) = Pipe();
        using (var writer = StandardStreamWriters.Create(writeFd, Encoding.UTF8))
            writer.Write("héllo");
        Assert.Equal(0, close(writeFd));

        using var reader = new FileStream(new SafeFileHandle(readFd, ownsHandle: true), FileAccess.Read, 1);
        using var copy = new MemoryStream();
        reader.CopyTo(copy);
        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), copy.ToArray());
    }

    private static (int Read, int Write) Pipe()
    {
        var fds = new int[2];
        Assert.Equal(0, pipe(fds));
        return (fds[0], fds[1]);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe([Out] int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
