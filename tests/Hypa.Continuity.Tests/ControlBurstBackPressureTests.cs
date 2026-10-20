using System.Net.Sockets;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Tests.Support;
using Xunit;

namespace Hypa.Continuity.Tests;

/// <summary>
/// A burst of frames that the reader takes too slowly. The session and the
/// splicer slow the sender. They do not reset while the reader takes data.
/// </summary>
public sealed class ControlBurstBackPressureTests : IDisposable
{
    private const string Placement = "plc_burst01";
    private readonly string _dir = Path.Combine("/tmp", "hypa-burst-" + Guid.NewGuid().ToString("N")[..8]);

    public ControlBurstBackPressureTests()
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
    public async Task More_than_a_megabyte_of_control_frames_to_a_paused_reader_keeps_the_session_open()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (peer, local) = await StreamPairAsync("control.sock");
        await using var peerStream = peer;
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var session = OutboundFramedSession.FromAccepted(
            local,
            MuxBinding(),
            Cipher(JoinRole.Mux),
            StreamBudget.Default,
            clock);
        var peerCipher = Cipher(JoinRole.Client);

        // 300 lines of 8 KiB: about 2.4 MiB, more than the 1 MiB control queue.
        const int lines = 300;
        var sender = Task.Run(async () =>
        {
            for (var i = 1; i <= lines; i++)
                await WriteSealedAsync(peerStream, peerCipher, StreamFrame.Control(StreamDirection.ClientToMux, (ulong)i, Line(i, 8 * 1024)));
        });

        // The reader pauses while the burst arrives. The control queue fills,
        // and the receive loop waits for room. The rest of the burst is more
        // than the socket buffers hold, so the sender must wait too.
        await Poll.GuardAsync(Task.WhenAny(clock.TimerStarted, sender), "the receive loop to wait for control room");
        Assert.False(sender.IsCompleted, "the sender was not slowed");

        for (var i = 1; i <= lines; i++)
        {
            var received = await session.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(received.Ok, $"line {i}: {received.Reason}: {received.Detail}");
            Assert.True(received.Value!.IsControl);
            Assert.Equal(Line(i, 8 * 1024), received.Value.Payload.ToArray());
        }

        await sender.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task Heartbeat_frames_behind_a_full_control_queue_do_not_reset_the_session()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (peer, local) = await StreamPairAsync("heartbeat.sock");
        await using var peerStream = peer;
        var budget = StreamBudget.Default with { MaxControlFrames = 2, MaxHeartbeatFrames = 2 };
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var session = OutboundFramedSession.FromAccepted(local, MuxBinding(), Cipher(JoinRole.Mux), budget, clock);
        var peerCipher = Cipher(JoinRole.Client);

        // Two lines fill the control queue. The third line makes the receive
        // loop wait for room. Then the peer sends heartbeat frames.
        for (var i = 1; i <= 3; i++)
            await WriteSealedAsync(peerStream, peerCipher, StreamFrame.Control(StreamDirection.ClientToMux, (ulong)i, Line(i, 64)));
        for (var i = 1; i <= 10; i++)
            await WriteSealedAsync(peerStream, peerCipher, StreamFrame.Heartbeat(StreamDirection.ClientToMux, (ulong)i));
        await WriteSealedAsync(peerStream, peerCipher, StreamFrame.Control(StreamDirection.ClientToMux, 4, Line(4, 64)));
        await Poll.GuardAsync(clock.TimerStarted, "the receive loop to wait for control room");

        var controlLines = new List<byte[]>();
        while (controlLines.Count < 4)
        {
            var received = await session.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(received.Ok, $"{received.Reason}: {received.Detail}");
            if (received.Value!.IsControl)
                controlLines.Add(received.Value.Payload.ToArray());
        }

        Assert.Equal([Line(1, 64), Line(2, 64), Line(3, 64), Line(4, 64)], controlLines);
    }

    [SkippableFact]
    public async Task A_reader_that_takes_no_frame_while_a_send_waits_for_resume_gets_the_reset_reason()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (peer, local) = await StreamPairAsync("resume.sock");
        await using var peerStream = peer;
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var budget = StreamBudget.Default with { MaxControlFrames = 2 };
        await using var session = OutboundFramedSession.FromAccepted(
            local,
            MuxBinding(),
            Cipher(JoinRole.Mux),
            budget,
            clock);
        var peerCipher = Cipher(JoinRole.Client);
        var drain = DrainAsync(peerStream);

        // The peer pauses the outgoing binary direction of the session. Then
        // it fills the control queue, and a resume waits behind a third line.
        await StreamFrameCodec.WriteAsync(
            peerStream,
            StreamFrame.FlowControl(StreamDirection.MuxToClient, StreamFlowCommand.Pause, 1),
            CancellationToken.None);
        await Poll.UntilAsync(() => session.IsBinaryPaused, "the session to pause the binary direction");
        for (var i = 1; i <= 3; i++)
            await WriteSealedAsync(peerStream, peerCipher, StreamFrame.Control(StreamDirection.ClientToMux, (ulong)i, Line(i, 64)));
        await StreamFrameCodec.WriteAsync(
            peerStream,
            StreamFrame.FlowControl(StreamDirection.MuxToClient, StreamFlowCommand.Resume, 2),
            CancellationToken.None);

        // The consumer waits in a binary send and does not read.
        var send = session.SendAsync(StreamFrame.Binary(StreamDirection.MuxToClient, 1, 0, new byte[] { 1 })).AsTask();
        // The receive loop waits for room for the third line, so it cannot
        // read the resume behind that line.
        await Poll.GuardAsync(Task.WhenAny(clock.TimerStarted, send), "the receive loop to wait for control room");
        Assert.False(send.IsCompleted, "the send did not wait for the resume");

        clock.Advance(TimeSpan.FromSeconds(6));

        await send.WaitAsync(TimeSpan.FromSeconds(10));
        var received = await session.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(received.Ok);
        Assert.Equal(ConnectivityReasons.StreamReset, received.Reason);
        Assert.Contains("no progress", received.Detail, StringComparison.Ordinal);
        await peerStream.DisposeAsync();
        await drain;
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    [SkippableFact]
    public async Task A_slow_reader_that_still_takes_data_keeps_the_splicer_open_past_the_timeout()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        await using var relay = await SplicerRelay.StartAsync(_dir);

        // The reader takes one frame for each second of the clock. The sender
        // keeps the queue at the high-water mark for 10 s.
        for (var second = 0; second < 10; second++)
        {
            await relay.TakeOneClientFrameAsync();
            // The queue stays above the low-water mark, so the stall timer runs.
            Assert.True(
                relay.Splicer.MuxToClient.BinaryBytes > 16 * 1024,
                $"the queue drained to {relay.Splicer.MuxToClient.BinaryBytes} bytes");
            relay.Clock.Advance(TimeSpan.FromSeconds(1));
            // The splicer cannot make progress until the next take. The stall
            // check of the splicer reads this state, so the state shows its result.
            Assert.False(
                relay.Splicer.MuxToClient.IsHighWaterTimeout(relay.Clock.GetUtcNow()),
                $"the stall timer expired after {second + 1} s");
            Assert.False(relay.Completed.IsCompleted, $"the splicer reset after {second + 1} s: {Outcome(relay)}");
        }
    }

    [SkippableFact]
    public async Task A_reader_that_takes_no_data_for_the_timeout_resets_the_splicer()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        await using var relay = await SplicerRelay.StartAsync(_dir);
        // The reader takes one frame. That write is progress. The splicer then
        // waits on the next write, and the reader takes no more data, so the
        // clock moves only after the last progress.
        await relay.TakeOneClientFrameAsync();

        relay.Clock.Advance(TimeSpan.FromSeconds(6));

        var outcome = await relay.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.StreamReset, outcome.Reason);
    }

    [SkippableFact]
    public async Task A_control_burst_through_the_splicer_waits_for_the_reader()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var budget = StreamBudget.Default with { MaxControlFrames = 4 };
        await using var relay = await SplicerRelay.StartAsync(_dir, budget, binaryBulk: false);
        const int lines = 50;
        var sender = Task.Run(async () =>
        {
            for (var i = 1; i <= lines; i++)
                await StreamFrameCodec.WriteAsync(relay.MuxPeer, StreamFrame.Control(StreamDirection.MuxToClient, (ulong)i, Line(i, 256)), CancellationToken.None);
        });

        // The reader takes nothing yet. The splicer queue fills, and the read
        // loop of the splicer waits for room.
        await Poll.GuardAsync(Task.WhenAny(relay.Clock.TimerStarted, relay.Completed), "the splicer to wait for control room");
        Assert.False(relay.Completed.IsCompleted, Outcome(relay));
        relay.ClientGate.Open();
        for (var i = 1; i <= lines; i++)
        {
            var frame = await relay.ReadOneClientFrameAsync();
            Assert.Equal(StreamFrameKind.Control, frame.Kind);
            Assert.Equal(Line(i, 256), frame.Payload.ToArray());
        }

        await sender.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(relay.Completed.IsCompleted, Outcome(relay));
    }

    private static string Outcome(SplicerRelay relay) =>
        relay.Completed.IsCompleted ? $"{relay.Completed.Result.Reason}: {relay.Completed.Result.Detail}" : "open";

    private static byte[] Line(int index, int length)
    {
        var line = new byte[length];
        line.AsSpan().Fill((byte)('a' + (index % 26)));
        var head = Encoding.ASCII.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
        head.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    private static JoinBinding MuxBinding() =>
        new()
        {
            PlacementId = PlacementId.TryParse(Placement, out var placement) ? placement : default,
            StreamClass = StreamClass.Control,
            Role = JoinRole.Mux,
        };

    private static ApplicationFrameCipher Cipher(JoinRole role)
    {
        var created = ApplicationFrameCipher.Create(
            Enumerable.Repeat((byte)7, 32).ToArray(),
            JoinNonce.TryParse("burst001", out var nonce) ? nonce : default,
            Placement,
            role);
        Assert.True(created.Ok, created.Detail);
        return created.Value!;
    }

    private static async Task WriteSealedAsync(Stream stream, ApplicationFrameCipher cipher, StreamFrame frame)
    {
        var sealedFrame = cipher.Seal(frame);
        Assert.True(sealedFrame.Ok, sealedFrame.Detail);
        await StreamFrameCodec.WriteAsync(stream, sealedFrame.Value!, CancellationToken.None);
    }

    private async Task<(NetworkStream Peer, NetworkStream Local)> StreamPairAsync(string name) =>
        await SocketStreams.PairAsync(Path.Combine(_dir, name));

    private static class SocketStreams
    {
        public static async Task<(NetworkStream A, NetworkStream B)> PairAsync(string path)
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var a = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connect = a.ConnectAsync(new UnixDomainSocketEndPoint(path));
            var b = await listener.AcceptAsync();
            await connect;
            return (new NetworkStream(a, ownsSocket: true), new NetworkStream(b, ownsSocket: true));
        }
    }

    /// <summary>
    /// A splicer between a mux peer and a client peer. With bulk, the mux peer
    /// sends binary frames without a pause, so the mux-to-client direction
    /// stays at the high-water mark. The client side of the splicer is a
    /// <see cref="GatedStream"/>: a write to the client waits until the test
    /// lets the reader take a frame.
    /// </summary>
    private sealed class SplicerRelay : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly NetworkStream _muxSplicerSide;
        private readonly NetworkStream _clientSplicerSide;
        private readonly Task _bulk;
        private readonly Task _muxDrain;

        private SplicerRelay(
            NetworkStream muxPeer,
            NetworkStream muxSplicerSide,
            NetworkStream clientPeer,
            NetworkStream clientSplicerSide,
            StreamBudget budget,
            bool binaryBulk)
        {
            MuxPeer = muxPeer;
            ClientPeer = clientPeer;
            _muxSplicerSide = muxSplicerSide;
            _clientSplicerSide = clientSplicerSide;
            ClientGate = new GatedStream(clientSplicerSide);
            Splicer = new JoinedStreamSplicer(muxSplicerSide, ClientGate, budget, Clock);
            Completed = Splicer.RunAsync(_stop.Token);
            _bulk = binaryBulk ? SendBulkAsync(_stop.Token) : Task.CompletedTask;
            // The splicer writes pause frames to the mux peer. Take them.
            _muxDrain = DrainAsync(muxPeer, _stop.Token);
        }

        public ManualClock Clock { get; } = new(DateTimeOffset.UnixEpoch);

        public NetworkStream MuxPeer { get; }

        public JoinedStreamSplicer Splicer { get; }

        public NetworkStream ClientPeer { get; }

        public GatedStream ClientGate { get; }

        public Task<ConnectivityOutcome> Completed { get; }

        public static async Task<SplicerRelay> StartAsync(string dir, StreamBudget? budget = null, bool binaryBulk = true)
        {
            var id = Guid.NewGuid().ToString("N")[..6];
            var (muxPeer, muxSide) = await SocketStreams.PairAsync(Path.Combine(dir, "m" + id + ".sock"));
            var (clientPeer, clientSide) = await SocketStreams.PairAsync(Path.Combine(dir, "c" + id + ".sock"));
            var used = budget ?? StreamBudget.Default with
            {
                BinaryHighWaterBytes = 64 * 1024,
                BinaryLowWaterBytes = 16 * 1024,
                HighWaterTimeout = TimeSpan.FromSeconds(5),
            };
            var relay = new SplicerRelay(muxPeer, muxSide, clientPeer, clientSide, used, binaryBulk);
            if (binaryBulk)
            {
                // The writer waits at the gate with the first frame, and the
                // read loop fills the queue to the high-water mark.
                await Poll.UntilAsync(
                    () => relay.ClientGate.WritesEntered > 0 && relay.Splicer.MuxToClient.IsBinaryPaused,
                    "the splicer queue to reach the high-water mark");
            }

            return relay;
        }

        /// <summary>
        /// The reader takes one frame: the gate lets one write through, and
        /// the client peer reads that frame. The method returns when the
        /// splicer waits at the gate with its next frame. The splicer then
        /// has recorded the progress of that write, and it cannot make more
        /// progress until the next take.
        /// </summary>
        public async Task<StreamFrame> TakeOneClientFrameAsync()
        {
            var entered = ClientGate.WritesEntered;
            ClientGate.Allow();
            var frame = await ReadOneClientFrameAsync();
            await Poll.UntilAsync(
                () => ClientGate.WritesEntered > entered,
                "the splicer to write its next frame to the client");
            return frame;
        }

        public async Task<StreamFrame> ReadOneClientFrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var header = await StreamFrameCodec.ReadHeaderAsync(ClientPeer, StreamBudget.Default, timeout.Token);
                Assert.True(header.Ok, header.Detail);
                var body = await StreamFrameCodec.ReadExactAsync(ClientPeer, header.Value.Length, timeout.Token);
                Assert.True(body.Ok, body.Detail);
                if (header.Value.Kind == StreamFrameKind.FlowControl)
                    continue;
                return StreamFrameCodec.ToFrame(header.Value, body.Value!);
            }
        }

        private async Task SendBulkAsync(CancellationToken ct)
        {
            var payload = new byte[8 * 1024];
            ulong seq = 1;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await StreamFrameCodec.WriteAsync(
                        MuxPeer,
                        StreamFrame.Binary(StreamDirection.MuxToClient, 1, seq++, payload),
                        ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private static async Task DrainAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer, ct) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await MuxPeer.DisposeAsync();
            await ClientPeer.DisposeAsync();
            await _muxSplicerSide.DisposeAsync();
            await _clientSplicerSide.DisposeAsync();
            try
            {
                await Task.WhenAll(Completed, _bulk, _muxDrain).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
            }

            _stop.Dispose();
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly TaskCompletionSource _timerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ticks = start.UtcTicks;

        /// <summary>
        /// Completes when the code under test starts its first timer on this
        /// clock. The session and the splicer start one only for the stall
        /// check of a wait for control room.
        /// </summary>
        public Task TimerStarted => _timerStarted.Task;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timerStarted.TrySetResult();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>
    /// The client side of a splicer. A write waits at the gate until the test
    /// lets one write through, or opens the gate. Thus the test knows each
    /// point where the splicer makes progress to the client. Reads pass
    /// through.
    /// </summary>
    private sealed class GatedStream(Stream inner) : Stream
    {
        private readonly SemaphoreSlim _permits = new(0);
        private volatile bool _open;
        private int _writesEntered;

        /// <summary>Writes that reached the gate, also the one that waits there now.</summary>
        public int WritesEntered => Volatile.Read(ref _writesEntered);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Lets one write through.</summary>
        public void Allow() => _permits.Release();

        /// <summary>Lets every write through from now on.</summary>
        public void Open()
        {
            _open = true;
            _permits.Release();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writesEntered);
            if (!_open)
                await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
