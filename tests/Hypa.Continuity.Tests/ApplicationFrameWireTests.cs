using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

/// <summary>
/// The sealed bytes of an application frame are part of the wire protocol.
/// A client and a cube can run different builds, so a change of the cipher
/// code must give the same bytes. The NDJSON splitter of the framed control
/// stream must give the same frames for each split of the byte stream.
/// </summary>
public sealed class ApplicationFrameWireTests
{
    private const string GoldenControl = "9A825C27EC2670C06853362C055F9F90CB53221FF7BF2A";
    private const string GoldenBinary = "03C8DCCB42268E61F01FB2C5405867F76A2669A9662028DC9280772A99C7";

    [Fact]
    public void A_sealed_control_frame_has_the_golden_bytes()
    {
        var sealedFrame = Cipher(JoinRole.Mux).Seal(
            StreamFrame.Control(StreamDirection.MuxToClient, 7, "{\"a\":1}"u8.ToArray()));
        Assert.True(sealedFrame.Ok, sealedFrame.Detail);
        Assert.Equal(GoldenControl, Convert.ToHexString(sealedFrame.Value!.Payload.Span));
    }

    [Fact]
    public void A_sealed_binary_frame_has_the_golden_bytes()
    {
        var sealedFrame = Cipher(JoinRole.Client).Seal(
            StreamFrame.Binary(StreamDirection.ClientToMux, 3, 9, "terminal bytes"u8.ToArray()));
        Assert.True(sealedFrame.Ok, sealedFrame.Detail);
        Assert.Equal(GoldenBinary, Convert.ToHexString(sealedFrame.Value!.Payload.Span));
    }

    [Fact]
    public void Seal_to_a_buffer_gives_the_golden_bytes_and_copies_a_frame_it_does_not_protect()
    {
        var cipher = Cipher(JoinRole.Mux);
        var control = StreamFrame.Control(StreamDirection.MuxToClient, 7, "{\"a\":1}"u8.ToArray());
        var sealedBytes = new byte[cipher.SealedLength(control)];
        Assert.True(cipher.SealTo(control, sealedBytes).Ok);
        Assert.Equal(GoldenControl, Convert.ToHexString(sealedBytes));

        var heartbeat = StreamFrame.Heartbeat(StreamDirection.MuxToClient, 8);
        var plain = new byte[cipher.SealedLength(heartbeat)];
        Assert.True(cipher.SealTo(heartbeat, plain).Ok);
        Assert.Equal(heartbeat.Payload.ToArray(), plain);
    }

    [Theory]
    [InlineData(JoinRole.Mux, JoinRole.Client, StreamDirection.MuxToClient)]
    [InlineData(JoinRole.Client, JoinRole.Mux, StreamDirection.ClientToMux)]
    public void A_frame_sealed_by_one_end_opens_at_the_other_end(
        JoinRole sender,
        JoinRole receiver,
        StreamDirection direction)
    {
        var seal = Cipher(sender);
        var open = Cipher(receiver);
        for (ulong sequence = 1; sequence <= 50; sequence++)
        {
            var payload = Encoding.UTF8.GetBytes($"{{\"n\":{sequence}}}");
            var sealedFrame = seal.Seal(StreamFrame.Control(direction, sequence, payload));
            Assert.True(sealedFrame.Ok, sealedFrame.Detail);
            var opened = open.Open(sealedFrame.Value!);
            Assert.True(opened.Ok, opened.Detail);
            Assert.Equal(payload, opened.Value!.Payload.ToArray());
        }
    }

    [Fact]
    public async Task Concurrent_seals_give_frames_that_all_open()
    {
        var seal = Cipher(JoinRole.Mux);
        var open = Cipher(JoinRole.Client);
        const int writers = 8;
        const int perWriter = 400;
        var sealedFrames = new StreamFrame[writers * perWriter];
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var index = (w * perWriter) + i;
                var payload = Encoding.UTF8.GetBytes($"frame {index}");
                var sealedFrame = seal.Seal(
                    StreamFrame.Control(StreamDirection.MuxToClient, (ulong)index + 1, payload));
                Assert.True(sealedFrame.Ok, sealedFrame.Detail);
                sealedFrames[index] = sealedFrame.Value!;
            }
        })));

        for (var index = 0; index < sealedFrames.Length; index++)
        {
            var opened = open.Open(sealedFrames[index]);
            Assert.True(opened.Ok, opened.Detail);
            Assert.Equal($"frame {index}", Encoding.UTF8.GetString(opened.Value!.Payload.Span));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_control_stream_sends_the_same_lines_for_each_split_of_the_bytes(bool sessionCopies)
    {
        var random = new Random(4040);
        for (var round = 0; round < 300; round++)
        {
            var lines = new List<string>();
            var count = random.Next(1, 12);
            for (var i = 0; i < count; i++)
                lines.Add(random.Next(5) == 0 ? "" : new string((char)('a' + (i % 26)), random.Next(1, 300)));
            var text = new StringBuilder();
            foreach (var line in lines)
                text.Append(line).Append(random.Next(3) == 0 ? "\r\n" : "\n");
            var bytes = Encoding.UTF8.GetBytes(text.ToString());

            var session = new RecordingSession(sessionCopies);
            await using var stream = new FramedControlStream(session, ownsSession: false);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var size = Math.Min(bytes.Length - offset, random.Next(1, 64));
                await stream.WriteAsync(bytes.AsMemory(offset, size));
                offset += size;
            }

            Assert.Equal(lines, session.Lines);
        }
    }

    [Fact]
    public async Task A_carriage_return_at_the_end_of_one_write_is_stripped_when_the_next_write_starts_with_a_newline()
    {
        var session = new RecordingSession();
        await using var stream = new FramedControlStream(session, ownsSession: false);
        await stream.WriteAsync("{\"a\":1}\r"u8.ToArray());
        Assert.Empty(session.Lines);
        await stream.WriteAsync("\n"u8.ToArray());
        Assert.Equal(["{\"a\":1}"], session.Lines);
    }

    [Fact]
    public async Task A_read_gives_each_received_line_with_one_newline_for_small_buffers()
    {
        var session = new RecordingSession(copies: true);
        session.Incoming.Enqueue(StreamFrame.Control(StreamDirection.ClientToMux, 1, "{\"a\":1}"u8.ToArray()));
        session.Incoming.Enqueue(StreamFrame.Control(StreamDirection.ClientToMux, 2, "b\n"u8.ToArray()));
        session.Incoming.Enqueue(StreamFrame.Control(StreamDirection.ClientToMux, 3, "cd"u8.ToArray()));
        await using var stream = new FramedControlStream(session, ownsSession: false);
        var read = new List<byte>();
        var size = 1;
        var buffer = new byte[3];
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(0, size))) > 0)
        {
            read.AddRange(buffer.AsSpan(0, n).ToArray());
            size = size % 3 + 1;
        }

        Assert.Equal("{\"a\":1}\nb\ncd\n", Encoding.UTF8.GetString(read.ToArray()));
    }

    [Fact]
    public async Task A_partial_line_longer_than_the_limit_is_refused()
    {
        var session = new RecordingSession();
        await using var stream = new FramedControlStream(session, ownsSession: false);
        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            for (var written = 0; written <= StreamLimits.MaxControlLineBytes; written += chunk.Length)
                await stream.WriteAsync(chunk);
        });
    }

    private static ApplicationFrameCipher Cipher(JoinRole role)
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.True(JoinNonce.TryParse("noncegold", out var nonce));
        var cipher = ApplicationFrameCipher.Create(secret, nonce, "plc_gold01", role);
        Assert.True(cipher.Ok, cipher.Detail);
        return cipher.Value!;
    }

    /// <summary>
    /// Records the payload of each control frame as one line. A session that
    /// copies decodes the payload on send. Any other session keeps the frame
    /// and decodes it when the test reads the lines, so a payload that the
    /// stream changed after the send shows.
    /// </summary>
    private sealed class RecordingSession(bool copies = false) : IFramedSession
    {
        private readonly List<string> _lines = [];
        private readonly List<ReadOnlyMemory<byte>> _kept = [];

        public Queue<StreamFrame> Incoming { get; } = new();

        public bool CopiesPayloadOnSend => copies;

        public List<string> Lines
        {
            get
            {
                lock (_lines)
                    return copies ? [.. _lines] : [.. _kept.Select(p => Encoding.UTF8.GetString(p.Span))];
            }
        }

        public JoinBinding Binding { get; } = new()
        {
            PlacementId = PlacementId.TryParse("plc_gold01", out var placement) ? placement : default,
            StreamClass = StreamClass.Control,
            Role = JoinRole.Mux,
            PeerEphPublicKey = "",
        };

        public bool IsBinaryPaused => false;

        public bool ApplicationEncryptionEnabled => true;

        public IReadOnlyList<ChannelCursor> LastReceived => [];

        public ValueTask<ConnectivityOutcome> SendAsync(StreamFrame frame, CancellationToken cancellationToken = default)
        {
            lock (_lines)
            {
                if (copies)
                    _lines.Add(Encoding.UTF8.GetString(frame.Payload.Span));
                else
                    _kept.Add(frame.Payload);
            }

            return ValueTask.FromResult(ConnectivityOutcome.Success());
        }

        public ValueTask<ConnectivityOutcome<StreamFrame>> ReceiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Incoming.TryDequeue(out var frame)
                ? ConnectivityOutcome<StreamFrame>.Success(frame)
                : ConnectivityOutcome<StreamFrame>.Failure(ConnectivityReasons.PeerUnavailable, "closed"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
