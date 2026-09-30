using System.Buffers.Binary;
using System.Text;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PtyHostFrameCodecTests
{
    [Theory]
    [InlineData(PtyHostFrameType.Hello)]
    [InlineData(PtyHostFrameType.Close)]
    [InlineData(PtyHostFrameType.Output)]
    [InlineData(PtyHostFrameType.Input)]
    [InlineData(PtyHostFrameType.Resize)]
    [InlineData(PtyHostFrameType.Signal)]
    [InlineData(PtyHostFrameType.Exit)]
    [InlineData(PtyHostFrameType.Error)]
    [InlineData(PtyHostFrameType.Spawn)]
    [InlineData(PtyHostFrameType.Spawned)]
    public void Encode_decode_round_trips(PtyHostFrameType type)
    {
        var frame = type switch
        {
            PtyHostFrameType.Hello => PtyHostFrameCodec.CreateHello(),
            PtyHostFrameType.Close => PtyHostFrameCodec.CreateClose(),
            PtyHostFrameType.Output => PtyHostFrameCodec.CreateOutput("hi"u8),
            PtyHostFrameType.Input => PtyHostFrameCodec.CreateInput("in"u8),
            PtyHostFrameType.Resize => PtyHostFrameCodec.CreateResize(120, 40),
            PtyHostFrameType.Signal => PtyHostFrameCodec.CreateSignal(15),
            PtyHostFrameType.Exit => PtyHostFrameCodec.CreateExit(0, 42),
            PtyHostFrameType.Error => PtyHostFrameCodec.CreateError(1, "nope"),
            PtyHostFrameType.Spawn => PtyHostFrameCodec.CreateSpawn(
                80, 24, "/tmp",
                ["/bin/sh", "-c", "true"],
                new Dictionary<string, string> { ["TERM"] = "xterm-256color" }),
            PtyHostFrameType.Spawned => PtyHostFrameCodec.CreateSpawned(4242),
            _ => throw new InvalidOperationException(),
        };

        var bytes = PtyHostFrameCodec.Encode(frame);
        var (decoded, consumed) = PtyHostFrameCodec.Decode(bytes);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(frame.Type, decoded.Type);
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public void Length_prefix_is_little_endian()
    {
        var frame = PtyHostFrameCodec.CreateClose();
        var bytes = PtyHostFrameCodec.Encode(frame);
        // body = 1 (type only)
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal((byte)PtyHostFrameType.Close, bytes[4]);
    }

    [Fact]
    public void Rejects_zero_length_body()
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 0);
        var ex = Assert.Throws<InvalidDataException>(() => PtyHostFrameCodec.Decode(buf));
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_oversize_body()
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)PtyHostFrameCodec.MaxBodyLength + 1);
        var ex = Assert.Throws<InvalidDataException>(() => PtyHostFrameCodec.Decode(buf));
        Assert.Contains("oversize", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_truncated_frame()
    {
        var frame = PtyHostFrameCodec.CreateHello();
        var full = PtyHostFrameCodec.Encode(frame);
        var truncated = full.AsSpan(0, full.Length - 1).ToArray();
        Assert.Throws<InvalidDataException>(() => PtyHostFrameCodec.Decode(truncated));
    }

    [Fact]
    public void Output_input_chunk_limit_is_64_kib()
    {
        var ok = new byte[PtyHostFrameCodec.MaxChunkPayload];
        var encoded = PtyHostFrameCodec.Encode(PtyHostFrameCodec.CreateOutput(ok));
        Assert.True(encoded.Length > 4);

        var tooBig = new byte[PtyHostFrameCodec.MaxChunkPayload + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => PtyHostFrameCodec.CreateOutput(tooBig));
        Assert.Throws<ArgumentOutOfRangeException>(() => PtyHostFrameCodec.CreateInput(tooBig));
    }

    [Fact]
    public void Hello_magic_and_minor_match_spec()
    {
        var frame = PtyHostFrameCodec.CreateHello(minor: 0);
        Assert.True(PtyHostFrameCodec.TryParseHello(frame.Payload, out var minor, out var err), err);
        Assert.Equal((ushort)0, minor);
        Assert.True(frame.Payload.AsSpan(0, 7).SequenceEqual("HYPTY1\0"u8));
    }

    [Fact]
    public void Golden_hello_bytes()
    {
        // length=10 (1 type + 9 payload), type=1, magic HYPTY1\0, minor=0
        var expected = new byte[]
        {
            0x0A, 0x00, 0x00, 0x00, // length LE = 10
            0x01,                   // Hello
            (byte)'H', (byte)'Y', (byte)'P', (byte)'T', (byte)'Y', (byte)'1', 0x00,
            0x00, 0x00,             // minor 0
        };
        Assert.Equal(expected, PtyHostFrameCodec.Encode(PtyHostFrameCodec.CreateHello()));
    }

    [Fact]
    public void Golden_minimal_spawn_bytes()
    {
        var frame = PtyHostFrameCodec.CreateSpawn(
            cols: 80,
            rows: 24,
            cwd: "/",
            argv: ["/bin/true"],
            env: new Dictionary<string, string>());

        var bytes = PtyHostFrameCodec.Encode(frame);
        Assert.Equal((byte)PtyHostFrameType.Spawn, bytes[4]);
        // body length LE at start
        var bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.Equal(bytes.Length - 4, (int)bodyLen);

        // Round-trip via decode
        var (decoded, _) = PtyHostFrameCodec.Decode(bytes);
        Assert.Equal(PtyHostFrameType.Spawn, decoded.Type);

        // Payload starts with cols=80, rows=24
        Assert.Equal(80, BinaryPrimitives.ReadUInt16LittleEndian(decoded.Payload.AsSpan(0, 2)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(decoded.Payload.AsSpan(2, 2)));
        // cwd len = 1, cwd = "/"
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(decoded.Payload.AsSpan(4, 4)));
        Assert.Equal("/", Encoding.UTF8.GetString(decoded.Payload, 8, 1));
    }

    [Fact]
    public void Parse_exit_and_resize_and_signal()
    {
        var exit = PtyHostFrameCodec.CreateExit(128 + 9, 99);
        var (code, pid) = PtyHostFrameCodec.ParseExit(exit.Payload);
        Assert.Equal(137, code);
        Assert.Equal(99, pid);

        var resize = PtyHostFrameCodec.CreateResize(100, 50);
        var (c, r) = PtyHostFrameCodec.ParseResize(resize.Payload);
        Assert.Equal((ushort)100, c);
        Assert.Equal((ushort)50, r);

        Assert.Equal(2, PtyHostFrameCodec.ParseSignal(PtyHostFrameCodec.CreateSignal(2).Payload));
    }

    [Fact]
    public void Reserved_handoff_type_codes_are_stable()
    {
        Assert.Equal(10, (int)PtyHostFrameType.PauseOutput);
        Assert.Equal(11, (int)PtyHostFrameType.Adopt);
        Assert.Equal(12, (int)PtyHostFrameType.Adopted);
        Assert.Equal(13, (int)PtyHostFrameType.CloseOldOwner);
        Assert.Equal(14, (int)PtyHostFrameType.Spawned);
        Assert.Equal(15, (int)PtyHostFrameType.ResumeHandoff);
        Assert.Equal(16, (int)PtyHostFrameType.ReleaseAdopted);
    }

    [Fact]
    public void Handoff_frames_10_13_encode_decode_round_trips()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 + i)).ToArray();
        const int gen = 9;
        const int pid = 555;
        const string path = "/tmp/hypa-fdpass-test.sock";

        var pause = PtyHostFrameCodec.CreatePauseOutput(nonce, gen, path);
        Assert.Equal(PtyHostFrameType.PauseOutput, pause.Type);
        var (pn, pg, pp) = PtyHostFrameCodec.ParsePauseOutput(pause.Payload);
        Assert.Equal(nonce, pn);
        Assert.Equal(gen, pg);
        Assert.Equal(path, pp);
        RoundTrip(pause);

        var adopt = PtyHostFrameCodec.CreateAdopt(nonce, gen, pid, path);
        Assert.Equal(PtyHostFrameType.Adopt, adopt.Type);
        var (an, ag, ap, aPath) = PtyHostFrameCodec.ParseAdopt(adopt.Payload);
        Assert.Equal(nonce, an);
        Assert.Equal(gen, ag);
        Assert.Equal(pid, ap);
        Assert.Equal(path, aPath);
        RoundTrip(adopt);

        var adopted = PtyHostFrameCodec.CreateAdopted(nonce, gen, pid);
        Assert.Equal(PtyHostFrameType.Adopted, adopted.Type);
        var (dn, dg, dp) = PtyHostFrameCodec.ParseAdopted(adopted.Payload);
        Assert.Equal(nonce, dn);
        Assert.Equal(gen, dg);
        Assert.Equal(pid, dp);
        RoundTrip(adopted);

        var closeOld = PtyHostFrameCodec.CreateCloseOldOwner(nonce, gen);
        Assert.Equal(PtyHostFrameType.CloseOldOwner, closeOld.Type);
        var (cn, cg) = PtyHostFrameCodec.ParseCloseOldOwner(closeOld.Payload);
        Assert.Equal(nonce, cn);
        Assert.Equal(gen, cg);
        RoundTrip(closeOld);

        var resume = PtyHostFrameCodec.CreateResumeHandoff();
        Assert.Equal(PtyHostFrameType.ResumeHandoff, resume.Type);
        Assert.Empty(resume.Payload);
        RoundTrip(resume);

        var release = PtyHostFrameCodec.CreateReleaseAdopted();
        Assert.Equal(PtyHostFrameType.ReleaseAdopted, release.Type);
        Assert.Empty(release.Payload);
        RoundTrip(release);

        static void RoundTrip(PtyHostFrame frame)
        {
            var bytes = PtyHostFrameCodec.Encode(frame);
            var (decoded, consumed) = PtyHostFrameCodec.Decode(bytes);
            Assert.Equal(bytes.Length, consumed);
            Assert.Equal(frame.Type, decoded.Type);
            Assert.Equal(frame.Payload, decoded.Payload);
        }
    }

    [Fact]
    public void Spawned_encode_decode_child_pid()
    {
        var frame = PtyHostFrameCodec.CreateSpawned(12345);
        Assert.Equal(PtyHostFrameType.Spawned, frame.Type);
        Assert.Equal(12345, PtyHostFrameCodec.ParseSpawned(frame.Payload));

        var bytes = PtyHostFrameCodec.Encode(frame);
        Assert.Equal((byte)PtyHostFrameType.Spawned, bytes[4]);
        var (decoded, _) = PtyHostFrameCodec.Decode(bytes);
        Assert.Equal(12345, PtyHostFrameCodec.ParseSpawned(decoded.Payload));
    }

    /// <summary>
    /// Design §9.2 /: large helper output is framed as multiple ≤64 KiB Output
    /// chunks. Encode/decode a multi-chunk stream without loss or oversize rejection.
    /// </summary>
    [Fact]
    public void Multi_chunk_large_output_stream_decodes_sequentially()
    {
        // ~256 KiB total as four max-size chunks (simulates helper pump framing).
        const int chunks = 4;
        var chunkPayloads = new byte[chunks][];
        using var stream = new MemoryStream();
        for (var i = 0; i < chunks; i++)
        {
            var payload = new byte[PtyHostFrameCodec.MaxChunkPayload];
            // Distinct pattern per chunk so reassembly order is verifiable.
            payload.AsSpan().Fill((byte)(0xA0 + i));
            payload[0] = (byte)i;
            payload[^1] = (byte)(0xF0 + i);
            chunkPayloads[i] = payload;
            var encoded = PtyHostFrameCodec.Encode(PtyHostFrameCodec.CreateOutput(payload));
            stream.Write(encoded);
        }

        var buffer = stream.ToArray();
        var offset = 0;
        var totalBytes = 0;
        for (var i = 0; i < chunks; i++)
        {
            var (frame, consumed) = PtyHostFrameCodec.Decode(buffer.AsSpan(offset));
            Assert.Equal(PtyHostFrameType.Output, frame.Type);
            Assert.Equal(chunkPayloads[i], frame.Payload);
            offset += consumed;
            totalBytes += frame.Payload.Length;
        }

        Assert.Equal(buffer.Length, offset);
        Assert.Equal(chunks * PtyHostFrameCodec.MaxChunkPayload, totalBytes);
    }
}
