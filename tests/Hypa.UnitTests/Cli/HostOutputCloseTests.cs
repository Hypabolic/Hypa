using System.Text;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// After <see cref="UnixRawTerminal.CloseHostOutput"/>, no write reaches the
/// host. A write that holds the write lock ends before the close returns.
/// </summary>
public sealed class HostOutputCloseTests
{
    [Fact]
    public void Closed_host_output_takes_no_write_on_any_path()
    {
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        tty.CloseHostOutput();

        tty.WriteBytes("bytes"u8);
        tty.WriteBytes("text");
        Assert.False(tty.WriteHostEncoded("frame"u8));
        Assert.False(tty.WriteComposedBytes("composed"u8));
        tty.WriteComposedPaint(() => tty.WriteBytes("paint"));
        tty.WriteWindowTitle("\u001b]2;title\u0007");
        tty.WriteHostStatus("\u001b]7501;status\u0007");

        Assert.Empty(capture.ToArray());
    }

    [Fact]
    public async Task Close_waits_for_a_write_in_progress_and_stops_the_next_one()
    {
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        var inWrite = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        tty.DuringComposedWrite = () =>
        {
            inWrite.Set();
            release.Wait(TestWait.HangGuard);
        };

        var first = Task.Run(() => tty.WriteHostEncoded("first"u8));
        Assert.True(inWrite.Wait(TestWait.HangGuard));
        Thread? closer = null;
        var close = Task.Run(() =>
        {
            Volatile.Write(ref closer, Thread.CurrentThread);
            tty.CloseHostOutput();
        });
        // The close needs the write lock, which the first write holds.
        TestWait.UntilBlockedOrDone(() => Volatile.Read(ref closer), close);
        Assert.False(close.IsCompleted);

        release.Set();
        Assert.True(await first.WaitAsync(TestWait.HangGuard));
        await close.WaitAsync(TestWait.HangGuard);
        tty.DuringComposedWrite = null;

        Assert.False(tty.WriteHostEncoded("second"u8));
        Assert.Equal("first", Encoding.UTF8.GetString(capture.ToArray()));
    }
}
