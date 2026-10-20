using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Placement;

/// <summary>
/// The local side of an SSH cube. These tests use a Unix socket pair for the
/// attach client and a stand-in child process for ssh.
/// </summary>
[Collection(SshStdioBridgeTimingCollection.Name)]
public sealed class SshStdioBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine("/tmp", "hypa-sshb-" + Guid.NewGuid().ToString("N")[..8]);

    public SshStdioBridgeTests()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(_dir);
#pragma warning disable CA1416
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public async Task Upload_copy_forwards_a_single_keystroke_without_poll_delay()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (writer, reader) = await SocketPairAsync("pair.sock");
        var (controlWriter, controlReader) = await SocketPairAsync("control.sock");
        using (writer)
        using (reader)
        using (controlWriter)
        using (controlReader)
        {
            var sink = new TimedSink();
            var controlSink = new TimedSink();
            using var cts = new CancellationTokenSource();
            var copy = SshStdioBridge.CopyUploadAsync(reader, sink, cts.Token);
            var control = Task.Factory.StartNew(
                () => PlainBlockingReceive(controlReader, controlSink),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var rng = new Random(42);
            var delays = new List<double>();
            var controlDelays = new List<double>();
            for (var i = 0; i < 200; i++)
            {
                await InputPauseAsync(TimeSpan.FromMilliseconds(rng.Next(3, 17)));
                delays.Add(TimeOneByte(writer, sink));
                controlDelays.Add(TimeOneByte(controlWriter, controlSink));
            }

            await cts.CancelAsync();
            await copy.WaitAsync(TimeSpan.FromSeconds(2));
            controlWriter.Shutdown(SocketShutdown.Send);
            await control.WaitAsync(TimeSpan.FromSeconds(2));

            var p90 = Percentile(delays, 90);
            var p99 = Percentile(delays, 99);
            var controlP90 = Percentile(controlDelays, 90);
            var controlP99 = Percentile(controlDelays, 99);
            var report = $"upload copy p90 {p90:F2} ms p99 {p99:F2} ms; plain blocking receive p90 {controlP90:F2} ms p99 {controlP99:F2} ms";
            // A poll of 10 ms gives a p90 near 10 ms on any host, so the p90
            // check finds a poll. The copy wakes on the thread pool and the
            // plain receive on its own thread, so a shared host can delay a
            // few copy samples by some ms. The p99 limit stops only a stall.
            Assert.True(p90 < Math.Max(1.0, (controlP90 * 2) + 0.5), report);
            Assert.True(p99 < (controlP99 * 2) + 5.0, report);
        }
    }

    /// <summary>
    /// A pause that is an input of the test: the gap of a typist between two
    /// keys, or the stall of a slow reader. It is not a wait for a result: a
    /// timer of the system clock ends it.
    /// </summary>
    private static async Task InputPauseAsync(TimeSpan gap)
    {
        var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = TimeProvider.System.CreateTimer(
            _ => due.TrySetResult(), null, gap, Timeout.InfiniteTimeSpan);
        await due.Task;
    }

    private static double TimeOneByte(Socket writer, TimedSink sink)
    {
        var before = sink.Count;
        var sent = Stopwatch.GetTimestamp();
        writer.Send([(byte)' ']);
        return Stopwatch.GetElapsedTime(sent, sink.WaitFor(before + 1)).TotalMilliseconds;
    }

    private static double Percentile(List<double> values, int percent)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count * percent / 100];
    }

    private static void PlainBlockingReceive(Socket source, Stream destination)
    {
        source.Blocking = true;
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = source.Receive(buffer, SocketFlags.None);
            if (read == 0)
                return;
            destination.Write(buffer, 0, read);
        }
    }

    private static readonly TimeSpan SlowReaderStall = TimeSpan.FromMilliseconds(500);

    [SkippableFact]
    public async Task A_client_that_reads_slowly_gets_all_output_in_order_and_the_child_is_not_killed()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        const int lines = 200_000;
        await using var bridge = StartBridge("seq 1 " + lines + "; echo done");
        using var client = await ConnectAsync(bridge);
        // A small receive buffer makes sure that the client socket fills.
        client.ReceiveBufferSize = 4096;

        // The child writes about 1.3 MB, more than the pipe and the socket
        // buffers hold. When the output starts, the client stops reading for
        // a time, so the download copy fills the client socket and its send
        // waits. The stall is the slow reader of this test.
        await Poll.UntilAsync(() => client.Available > 0, "output in the client socket");
        await InputPauseAsync(SlowReaderStall);

        var text = await ReadToEndAsync(client, TimeSpan.FromSeconds(20));
        var expected = new StringBuilder();
        for (var i = 1; i <= lines; i++)
            expected.Append(i).Append('\n');
        expected.Append("done\n");
        Assert.Equal(expected.Length, text.Length);
        Assert.True(string.Equals(expected.ToString(), text, StringComparison.Ordinal), "output is not in order");
    }

    [SkippableFact]
    public async Task Output_in_flight_when_the_client_ends_its_input_reaches_the_client()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        // The child reads its stdin to the end, then writes a final line.
        await using var bridge = StartBridge("cat >/dev/null; echo final", downloadGrace: TimeSpan.FromSeconds(10));
        using var client = await ConnectAsync(bridge);
        await client.SendAsync(Encoding.ASCII.GetBytes("input\n"));
        client.Shutdown(SocketShutdown.Send);

        var text = await ReadToEndAsync(client, TimeSpan.FromSeconds(10));

        Assert.Equal("final\n", text);
    }

    [SkippableFact]
    public async Task The_bridge_stops_the_child_when_its_output_does_not_end_in_the_grace_time()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        // The child ignores the end of its input and keeps its output open.
        await using var bridge = StartBridge("echo ready; exec sleep 30", downloadGrace: TimeSpan.FromMilliseconds(200));
        using var client = await ConnectAsync(bridge);
        Assert.Equal("ready\n", await ReadLineAsync(client, TimeSpan.FromSeconds(10)));
        client.Shutdown(SocketShutdown.Send);

        var started = Stopwatch.GetTimestamp();
        var rest = await ReadToEndAsync(client, TimeSpan.FromSeconds(10));

        Assert.Equal("", rest);
        Assert.True(
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5),
            "the bridge did not stop the child after the grace time");
    }

    [SkippableFact]
    public async Task Dispose_returns_within_its_limit_while_a_copy_is_blocked()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var bridge = StartBridge("echo $$; exec sleep 30");
        using var client = await ConnectAsync(bridge);
        var pid = int.Parse(
            (await ReadLineAsync(client, TimeSpan.FromSeconds(10))).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        using var child = Process.GetProcessById(pid);

        // The upload waits for client bytes and the download waits for child output.
        var started = Stopwatch.GetTimestamp();
        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"dispose took {elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal("", await ReadToEndAsync(client, TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(bridge.LocalSocketPath));
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(2)), "the child is still running");
    }

    private SshStdioBridge StartBridge(string script, TimeSpan? downloadGrace = null) =>
        SshStdioBridge.Start(
            new SshStdioBridgeStartRequest
            {
                Target = "stand-in",
                Session = "default",
                LocalSocketPath = Path.Combine(_dir, "b", "bridge.sock"),
            },
            _ =>
            {
                var psi = new ProcessStartInfo("/bin/sh");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                return psi;
            },
            downloadGrace: downloadGrace);

    private static async Task<Socket> ConnectAsync(SshStdioBridge bridge)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(bridge.LocalSocketPath));
        return client;
    }

    private static async Task<string> ReadLineAsync(Socket client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var read = await client.ReceiveAsync(one, SocketFlags.None, cts.Token);
            if (read == 0)
                return line.ToString();
            line.Append((char)one[0]);
            if (one[0] == (byte)'\n')
                return line.ToString();
        }
    }

    private static async Task<string> ReadToEndAsync(Socket client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var all = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                break;
            }

            if (read == 0)
                break;
            all.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(all.ToArray());
    }

    private async Task<(Socket Writer, Socket Reader)> SocketPairAsync(string name)
    {
        var path = Path.Combine(_dir, name);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var writer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var connect = writer.ConnectAsync(new UnixDomainSocketEndPoint(path));
        var reader = await listener.AcceptAsync();
        await connect;
        return (writer, reader);
    }

    /// <summary>Records the time at which each byte arrives, and wakes a waiter at once.</summary>
    private sealed class TimedSink : Stream
    {
        private readonly object _gate = new();
        private readonly List<long> _arrivals = [];

        public int Count
        {
            get
            {
                lock (_gate)
                    return _arrivals.Count;
            }
        }

        public long WaitFor(int count)
        {
            lock (_gate)
            {
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                while (_arrivals.Count < count)
                {
                    var left = deadline - Stopwatch.GetTimestamp();
                    if (left <= 0)
                        throw new TimeoutException($"byte {count} did not arrive");
                    Monitor.Wait(_gate, TimeSpan.FromTicks(Math.Max(1, left * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                }

                return _arrivals[count - 1];
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Record(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer.Length);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Record(count);
            return Task.CompletedTask;
        }

        private void Record(int count)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                for (var i = 0; i < count; i++)
                    _arrivals.Add(now);
                Monitor.PulseAll(_gate);
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>The latency test measures sub-millisecond times. It does not run beside other timing tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SshStdioBridgeTimingCollection
{
    public const string Name = "SSH stdio bridge timing";
}
