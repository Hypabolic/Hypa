using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>
/// A userspace link impairment proxy on loopback. It listens on one port
/// number for both TCP and UDP, because the accept serves TLS on TCP and QUIC
/// on UDP at the same port. It forwards each protocol to the same port
/// protocol of the target and applies the <see cref="ImpairmentProfile"/> in
/// each direction. It needs no kernel feature and no root access.
/// </summary>
internal sealed class ImpairmentProxy : IAsyncDisposable
{
    private const int TcpSegmentBytes = 1448;
    private const int SmallSocketBuffer = 16 * 1024;
    private const int UdpSocketBuffer = 4 * 1024 * 1024;

    // A blocking receive polls with this limit, so a reader sees the stop
    // without a dispose of a socket that a syscall still uses.
    private const int PollMicroseconds = 100_000;

    private readonly ImpairmentProfile _profile;
    private readonly IPEndPoint _target;
    private readonly Socket _udp;
    private readonly Socket _tcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<EndPoint, UdpFlow> _udpFlows = new();
    private readonly ConcurrentBag<TcpFlow> _tcpFlows = [];
    private readonly Thread _udpThread;
    private readonly Task _tcpAccept;
    private int _flowSeed;
    private long _slowReaderWaits;

    private ImpairmentProxy(ImpairmentProfile profile, IPEndPoint target, Socket udp, Socket tcp)
    {
        _profile = profile;
        _target = target;
        _udp = udp;
        _tcp = tcp;
        Port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        _udpThread = new Thread(UdpReceiveLoop) { IsBackground = true, Name = "impair-udp-rx" };
        _udpThread.Start();
        _tcpAccept = Task.Run(TcpAcceptLoopAsync);
    }

    /// <summary>The loopback port of the proxy, for TCP and for UDP.</summary>
    public int Port { get; }

    public ImpairmentProfile Profile => _profile;

    /// <summary>Counters of every line, for the bench report.</summary>
    public ImpairmentCounters Counters => Up.Sum(Down);

    /// <summary>Counters of the client to target lines.</summary>
    public ImpairmentCounters Up
    {
        get
        {
            var counters = new ImpairmentCounters();
            foreach (var flow in _udpFlows.Values)
                counters = counters.Add(flow.Up);
            foreach (var flow in _tcpFlows)
                counters = counters.Add(flow.Up);
            return counters;
        }
    }

    /// <summary>Counters of the target to client lines.</summary>
    public ImpairmentCounters Down
    {
        get
        {
            var counters = new ImpairmentCounters();
            foreach (var flow in _udpFlows.Values)
                counters = counters.Add(flow.Down);
            foreach (var flow in _tcpFlows)
                counters = counters.Add(flow.Down);
            return counters;
        }
    }

    /// <summary>TCP writes that waited a poll interval for room, because the peer read slowly.</summary>
    public long SlowReaderWaits => Interlocked.Read(ref _slowReaderWaits);

    /// <summary>The state of each line, for a report when a probe waits too long.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        foreach (var flow in _tcpFlows)
            lines.Add($"tcp up[{flow.Up.Describe()}] down[{flow.Down.Describe()}]");
        foreach (var flow in _udpFlows.Values)
            lines.Add($"udp up[{flow.Up.Describe()}] down[{flow.Down.Describe()}]");
        lines.Add("slow_reader_waits=" + SlowReaderWaits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join("; ", lines);
    }

    /// <summary>
    /// Starts a proxy to <paramref name="targetPort"/> on 127.0.0.1. The proxy
    /// binds UDP first, then TCP on the same port number. It tries again when
    /// that TCP port is in use.
    /// </summary>
    public static ImpairmentProxy Start(int targetPort, ImpairmentProfile profile)
    {
        var target = new IPEndPoint(IPAddress.Loopback, targetPort);
        for (var attempt = 0; ; attempt++)
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            TrySetBuffers(udp, UdpSocketBuffer);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                tcp.Listen(16);
                return new ImpairmentProxy(profile, target, udp, tcp);
            }
            catch (SocketException) when (attempt < 20)
            {
                tcp.Dispose();
                udp.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the readers first. Each one polls, so it sees the stop and
        // leaves its receive before the socket closes.
        await _stop.CancelAsync();
        _udpThread.Join(TimeSpan.FromSeconds(2));
        // The accept loop ends before the flows close, so no flow is added
        // after the dispose below.
        try
        {
            await _tcpAccept.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        foreach (var flow in _udpFlows.Values)
            flow.Dispose();
        foreach (var flow in _tcpFlows)
            flow.Dispose();
        _tcp.Dispose();
        _udp.Dispose();
        _stop.Dispose();
    }

    private void UdpReceiveLoop()
    {
        var buffer = new byte[65_536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            int n;
            try
            {
                if (!_udp.Poll(PollMicroseconds, SelectMode.SelectRead))
                    continue;
                n = _udp.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException) when (!_stop.IsCancellationRequested)
            {
                // A port-unreachable report for an earlier send. Keep going.
                continue;
            }
            catch (Exception)
            {
                return;
            }

            var flow = _udpFlows.GetOrAdd(
                from,
                endpoint => new UdpFlow(this, (IPEndPoint)endpoint, Interlocked.Increment(ref _flowSeed)));
            flow.Up.Enqueue(buffer.AsSpan(0, n));
        }
    }

    private async Task TcpAcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _tcp.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await upstream.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                upstream.Dispose();
                client.Dispose();
                continue;
            }

            _tcpFlows.Add(new TcpFlow(this, client, upstream, Interlocked.Increment(ref _flowSeed)));
        }
    }

    private static void TrySetBuffers(Socket socket, int bytes)
    {
        try
        {
            socket.ReceiveBufferSize = bytes;
            socket.SendBufferSize = bytes;
        }
        catch (SocketException)
        {
        }
    }

    private sealed class UdpFlow : IDisposable
    {
        private readonly ImpairmentProxy _proxy;
        private readonly Thread _thread;

        public UdpFlow(ImpairmentProxy proxy, IPEndPoint client, int index)
        {
            _proxy = proxy;
            Upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            TrySetBuffers(Upstream, UdpSocketBuffer);
            Upstream.Connect(proxy._target);
            Up = new DelayLine(
                proxy._profile,
                stream: false,
                proxy._profile.Seed + (2 * index),
                packet =>
                {
                    if (packet is not null)
                        Upstream.Send(packet);
                },
                "impair-udp-up");
            Down = new DelayLine(
                proxy._profile,
                stream: false,
                proxy._profile.Seed + (2 * index) + 1,
                packet =>
                {
                    if (packet is not null)
                        proxy._udp.SendTo(packet, client);
                },
                "impair-udp-down");
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "impair-udp-flow" };
            _thread.Start();
        }

        public Socket Upstream { get; }

        public DelayLine Up { get; }

        public DelayLine Down { get; }

        public void Dispose()
        {
            _thread.Join(TimeSpan.FromSeconds(2));
            Up.Dispose();
            Down.Dispose();
            Upstream.Dispose();
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[65_536];
            while (!_proxy._stop.IsCancellationRequested)
            {
                int n;
                try
                {
                    if (!Upstream.Poll(PollMicroseconds, SelectMode.SelectRead))
                        continue;
                    n = Upstream.Receive(buffer);
                }
                catch (SocketException) when (!_proxy._stop.IsCancellationRequested)
                {
                    continue;
                }
                catch (Exception)
                {
                    return;
                }

                Down.Enqueue(buffer.AsSpan(0, n));
            }
        }
    }

    private sealed class TcpFlow : IDisposable
    {
        private readonly ImpairmentProxy _proxy;
        private readonly Socket _client;
        private readonly Socket _upstream;
        private readonly Thread _upReader;
        private readonly Thread _downReader;

        public TcpFlow(ImpairmentProxy proxy, Socket client, Socket upstream, int index)
        {
            _proxy = proxy;
            _client = client;
            _upstream = upstream;
            client.NoDelay = true;
            upstream.NoDelay = true;
            // Non-blocking sockets: a write waits for room with a poll, so a
            // stop does not wait on a peer that reads nothing, and a send
            // returns the exact count of bytes it moved.
            client.Blocking = false;
            upstream.Blocking = false;
            if (proxy._profile.BandwidthBytesPerSecond > 0)
            {
                // Large kernel buffers would hold more than the bottleneck
                // queue and hide the cap.
                TrySetBuffers(client, SmallSocketBuffer);
                TrySetBuffers(upstream, SmallSocketBuffer);
            }

            Up = new DelayLine(proxy._profile, stream: true, proxy._profile.Seed + 1000 + (2 * index), packet => Write(upstream, packet), "impair-tcp-up");
            Down = new DelayLine(proxy._profile, stream: true, proxy._profile.Seed + 1001 + (2 * index), packet => Write(client, packet), "impair-tcp-down");

            _upReader = new Thread(() => Pump(client, Up)) { IsBackground = true, Name = "impair-tcp-rx-up" };
            _downReader = new Thread(() => Pump(upstream, Down)) { IsBackground = true, Name = "impair-tcp-rx-down" };
            _upReader.Start();
            _downReader.Start();
        }

        public DelayLine Up { get; }

        public DelayLine Down { get; }

        public void Dispose()
        {
            _upReader.Join(TimeSpan.FromSeconds(2));
            _downReader.Join(TimeSpan.FromSeconds(2));
            Up.Dispose();
            Down.Dispose();
            _client.Dispose();
            _upstream.Dispose();
        }

        private void Pump(Socket source, DelayLine line)
        {
            var buffer = new byte[16 * 1024];
            while (!_proxy._stop.IsCancellationRequested)
            {
                // Wait for room for one full segment, as a TCP sender sends
                // whole segments. Reads of a few bytes would cost one line
                // item each and load the CPU.
                var room = line.WaitForRoom(TcpSegmentBytes, _proxy._stop.Token);
                if (room < TcpSegmentBytes)
                    continue;

                int n;
                try
                {
                    if (!source.Poll(PollMicroseconds, SelectMode.SelectRead))
                        continue;
                    // Read no more than the line has room for, so a TCP queue
                    // never grows past its limit.
                    n = source.Receive(buffer, 0, (int)Math.Clamp(room, 1, buffer.Length), SocketFlags.None, out var error);
                    if (error == SocketError.WouldBlock)
                        continue;
                    if (error != SocketError.Success)
                        n = 0;
                }
                catch (Exception)
                {
                    n = 0;
                }

                if (n <= 0)
                {
                    line.EnqueueEnd();
                    return;
                }

                for (var offset = 0; offset < n; offset += TcpSegmentBytes)
                    line.Enqueue(buffer.AsSpan(offset, Math.Min(TcpSegmentBytes, n - offset)));
            }
        }

        private void Write(Socket target, byte[]? packet)
        {
            if (packet is null)
            {
                target.Shutdown(SocketShutdown.Send);
                return;
            }

            var sent = 0;
            while (sent < packet.Length)
            {
                if (!target.Poll(PollMicroseconds, SelectMode.SelectWrite))
                {
                    if (_proxy._stop.IsCancellationRequested)
                        throw new OperationCanceledException("the proxy stopped");
                    // The peer reads slowly. Wait for room.
                    Interlocked.Increment(ref _proxy._slowReaderWaits);
                    continue;
                }

                // A non-blocking send moves what fits and returns that count,
                // so the next send starts at the first byte that is not sent.
                var n = target.Send(packet, sent, packet.Length - sent, SocketFlags.None, out var error);
                if (error == SocketError.WouldBlock)
                    continue;
                if (error != SocketError.Success)
                {
                    // The flow cannot deliver its bytes. Close both sides, so
                    // each end sees the failure now and does not wait on a
                    // dead line.
                    Abort();
                    throw new SocketException((int)error);
                }

                sent += n;
            }
        }

        private void Abort()
        {
            foreach (var socket in new[] { _client, _upstream })
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            }
        }
    }
}

/// <summary>What the lines of one proxy did.</summary>
internal readonly record struct ImpairmentCounters(
    long Packets,
    long Bytes,
    long Lost,
    long QueueDrops,
    long Stalls)
{
    public ImpairmentCounters Add(DelayLine line) =>
        new(
            Packets + line.Packets,
            Bytes + line.Bytes,
            Lost + line.Lost,
            QueueDrops + line.QueueDrops,
            Stalls + line.Stalls);

    public ImpairmentCounters Sum(ImpairmentCounters other) =>
        new(
            Packets + other.Packets,
            Bytes + other.Bytes,
            Lost + other.Lost,
            QueueDrops + other.QueueDrops,
            Stalls + other.Stalls);
}
