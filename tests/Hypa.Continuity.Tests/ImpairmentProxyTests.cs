using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Hypa.Continuity.Tests.Impairment;
using Hypa.Continuity.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.Continuity.Tests;

public sealed class ImpairmentProxyTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Tcp_round_trip_through_the_proxy_takes_at_least_the_configured_rtt()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var echo = TcpEchoServer.Start();
        await using var proxy = ImpairmentProxy.Start(echo.Port, ImpairmentProfile.Wan(40, 0) with { Jitter = TimeSpan.Zero });
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));
        var times = new List<double>();
        var one = new byte[1];
        for (var i = 0; i < 10; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await client.SendAsync(new byte[] { (byte)i });
            Assert.Equal(1, await client.ReceiveAsync(one));
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var stats = LatencyStats.From(times);
        output.WriteLine("tcp rtt 40 ms through the proxy: " + stats);
        Assert.True(stats.P50 >= 39.5, stats.ToString());
    }

    [SkippableFact]
    public async Task Tcp_loss_stalls_segments_and_keeps_the_byte_stream_intact()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var echo = TcpEchoServer.Start();
        await using var proxy = ImpairmentProxy.Start(echo.Port, ImpairmentProfile.Wan(0, 5));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));
        var payload = new byte[256 * 1024];
        new Random(5).NextBytes(payload);
        var send = Task.Run(async () =>
        {
            var sent = 0;
            while (sent < payload.Length)
                sent += await client.SendAsync(payload.AsMemory(sent, Math.Min(4096, payload.Length - sent)));
        });
        var received = new byte[payload.Length];
        var got = 0;
        while (got < received.Length)
        {
            var n = await client.ReceiveAsync(received.AsMemory(got));
            Assert.True(n > 0, "the proxy closed the stream");
            got += n;
        }

        await send;
        Assert.Equal(payload, received);
        Assert.True(proxy.Counters.Stalls > 0, "no TCP segment was stalled at 5% loss");
        Assert.Equal(0, proxy.Counters.Lost);
    }

    [SkippableFact]
    public async Task Udp_loss_drops_datagrams_and_the_counters_match_what_arrives()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sink.ReceiveBufferSize = 4 * 1024 * 1024;
        var sinkPort = ((IPEndPoint)sink.LocalEndPoint!).Port;
        await using var proxy = ImpairmentProxy.Start(sinkPort, ImpairmentProfile.Wan(0, 10));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Connect(new IPEndPoint(IPAddress.Loopback, proxy.Port));
        const int sent = 1000;
        for (var i = 0; i < sent; i++)
        {
            client.Send(BitConverter.GetBytes(i));
            // Send in batches of 50. Each batch waits until the proxy read the
            // datagrams before it, so no socket buffer drops one.
            if (i % 50 == 0)
                await Poll.UntilAsync(() => proxy.Counters.Packets >= i + 1, "the proxy to read the batch");
        }

        // The loss draw happens when the proxy reads a datagram. Wait until the
        // proxy read all of them, then until each one that it kept arrives.
        await Poll.UntilAsync(() => proxy.Counters.Packets == sent, "the proxy to read every datagram");
        var lost = proxy.Counters.Lost;
        var arrived = 0;
        var buffer = new byte[64];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (arrived < sent - lost)
        {
            await sink.ReceiveAsync(buffer, deadline.Token);
            arrived++;
        }

        // No extra datagram arrives after the expected count.
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sink.ReceiveAsync(buffer, quiet.Token));
        output.WriteLine($"udp loss 10%: sent={sent} lost={lost} arrived={arrived}");
        Assert.Equal(sent - lost, arrived);
        Assert.InRange(lost, 50, 160);
    }

    [SkippableFact]
    public async Task A_bandwidth_cap_paces_a_tcp_transfer()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var echo = TcpEchoServer.Start();
        var profile = ImpairmentProfile.None with { Name = "cap", BandwidthBytesPerSecond = 2_000_000 };
        await using var proxy = ImpairmentProxy.Start(echo.Port, profile);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));
        var payload = new byte[500_000];
        var started = Stopwatch.GetTimestamp();
        var send = Task.Run(async () =>
        {
            var sent = 0;
            while (sent < payload.Length)
                sent += await client.SendAsync(payload.AsMemory(sent, Math.Min(8192, payload.Length - sent)));
        });
        var got = 0;
        var buffer = new byte[64 * 1024];
        while (got < payload.Length)
        {
            var n = await client.ReceiveAsync(buffer);
            Assert.True(n > 0);
            got += n;
        }

        await send;
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        output.WriteLine($"500 kB at a 2 MB/s cap took {seconds:0.000} s");
        // The cap applies in each direction. The echo pipelines the two.
        Assert.True(seconds >= 0.2, $"{seconds:0.000} s");
    }

    [SkippableFact]
    public async Task A_slow_tcp_reader_gets_each_byte_exactly_once()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var payload = new byte[4 * 1024 * 1024];
        new Random(11).NextBytes(payload);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var source = Task.Run(async () =>
        {
            using var peer = await listener.AcceptAsync();
            var sent = 0;
            while (sent < payload.Length)
                sent += await peer.SendAsync(payload.AsMemory(sent));
            peer.Shutdown(SocketShutdown.Send);
        });
        await using var proxy = ImpairmentProxy.Start(((IPEndPoint)listener.LocalEndPoint!).Port, ImpairmentProfile.None);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 16 * 1024,
        };
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port));

        // The client stops reading three times. The proxy write to the client
        // then fills the socket and waits for room after it moved part of its
        // buffer. Each pause ends after the proxy reports that wait.
        var received = new MemoryStream();
        var buffer = new byte[64 * 1024];
        for (var pause = 0; pause < 3; pause++)
        {
            var waits = proxy.SlowReaderWaits;
            await Poll.UntilAsync(() => proxy.SlowReaderWaits > waits, "the proxy write to wait for room");
            var n = await client.ReceiveAsync(buffer);
            Assert.True(n > 0, "the proxy closed the stream");
            received.Write(buffer, 0, n);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var n = await client.ReceiveAsync(buffer, deadline.Token);
            if (n == 0)
                break;
            received.Write(buffer, 0, n);
            Assert.True(received.Length <= payload.Length, $"the proxy sent {received.Length} bytes for a {payload.Length} byte stream");
        }

        await source;
        output.WriteLine($"slow reader: received={received.Length} slow_reader_waits={proxy.SlowReaderWaits}");
        Assert.True(proxy.SlowReaderWaits > 0, "the proxy write to the slow reader never waited for room");
        Assert.Equal(payload, received.ToArray());
    }

    private sealed class TcpEchoServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _stop = new();

        private TcpEchoServer(Socket listener)
        {
            _listener = listener;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

        public static TcpEchoServer Start()
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(4);
            return new TcpEchoServer(listener);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket peer;
                try
                {
                    peer = await _listener.AcceptAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                peer.NoDelay = true;
                _ = Task.Run(async () =>
                {
                    using (peer)
                    {
                        var buffer = new byte[16 * 1024];
                        try
                        {
                            while (true)
                            {
                                var n = await peer.ReceiveAsync(buffer);
                                if (n <= 0)
                                    return;
                                var sent = 0;
                                while (sent < n)
                                    sent += await peer.SendAsync(buffer.AsMemory(sent, n - sent));
                            }
                        }
                        catch (SocketException)
                        {
                        }
                    }
                });
            }
        }
    }
}
