using System.Buffers.Binary;
using System.Text;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Pure encoder/decoder for hypa-pty-host length-prefixed frames.
/// </summary>
public static class PtyHostFrameCodec
{
    public const int MaxBodyLength = 1_048_576; // 1 MiB
    public const int MaxChunkPayload = 65_536; // 64 KiB
    public const ushort ProtocolMinor = 0;

    /// <summary>ASCII "HYPTY1\0"</summary>
    public static ReadOnlySpan<byte> HelloMagic => "HYPTY1\0"u8;

    public static byte[] Encode(PtyHostFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Payload);

        if (frame.Type is PtyHostFrameType.Output or PtyHostFrameType.Input
            && frame.Payload.Length > MaxChunkPayload)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"Output/Input payload must be ≤ {MaxChunkPayload} bytes; chunk larger streams.");
        }

        var bodyLen = frame.BodyLength;
        if (bodyLen is 0 or > MaxBodyLength)
            throw new ArgumentOutOfRangeException(nameof(frame), $"Invalid body length {bodyLen}.");

        var buffer = new byte[4 + bodyLen];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)bodyLen);
        buffer[4] = (byte)frame.Type;
        if (frame.Payload.Length > 0)
            frame.Payload.AsSpan().CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    /// <summary>
    /// Decodes one complete frame from <paramref name="source"/> starting at offset 0.
    /// Returns bytes consumed (4 + body). Throws on protocol errors.
    /// </summary>
    public static (PtyHostFrame Frame, int BytesConsumed) Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4)
            throw new InvalidDataException("Truncated frame: need 4-byte length.");

        var bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (bodyLen == 0)
            throw new InvalidDataException("Rejected zero-length body (type byte required).");
        if (bodyLen > MaxBodyLength)
            throw new InvalidDataException($"Rejected oversize body {bodyLen} (max {MaxBodyLength}).");

        var total = 4 + (int)bodyLen;
        if (source.Length < total)
            throw new InvalidDataException($"Truncated frame: need {total} bytes, have {source.Length}.");

        var type = (PtyHostFrameType)source[4];
        var payloadLen = (int)bodyLen - 1;
        var payload = payloadLen == 0 ? Array.Empty<byte>() : source.Slice(5, payloadLen).ToArray();

        if (type is PtyHostFrameType.Output or PtyHostFrameType.Input
            && payload.Length > MaxChunkPayload)
        {
            throw new InvalidDataException(
                $"Output/Input payload {payload.Length} exceeds chunk limit {MaxChunkPayload}.");
        }

        return (new PtyHostFrame(type, payload), total);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out PtyHostFrame? frame,
        out int bytesConsumed,
        out string? error)
    {
        frame = null;
        bytesConsumed = 0;
        error = null;
        try
        {
            (frame, bytesConsumed) = Decode(source);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static PtyHostFrame CreateHello(ushort minor = ProtocolMinor)
    {
        var payload = new byte[HelloMagic.Length + 2];
        HelloMagic.CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(HelloMagic.Length), minor);
        return new PtyHostFrame(PtyHostFrameType.Hello, payload);
    }

    public static bool TryParseHello(ReadOnlySpan<byte> payload, out ushort minor, out string? error)
    {
        minor = 0;
        error = null;
        if (payload.Length < HelloMagic.Length + 2)
        {
            error = "Hello payload too short.";
            return false;
        }

        if (!payload[..HelloMagic.Length].SequenceEqual(HelloMagic))
        {
            error = "Hello magic mismatch.";
            return false;
        }

        minor = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(HelloMagic.Length, 2));
        return true;
    }

    public static PtyHostFrame CreateSpawn(
        ushort cols,
        ushort rows,
        string cwd,
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(env);
        if (argv.Count == 0)
            throw new ArgumentException("Spawn requires at least argv[0] (executable).", nameof(argv));

        using var ms = new MemoryStream();
        WriteUInt16(ms, cols);
        WriteUInt16(ms, rows);
        WriteString(ms, cwd);
        WriteUInt32(ms, (uint)argv.Count);
        foreach (var arg in argv)
            WriteString(ms, arg ?? string.Empty);
        WriteUInt32(ms, (uint)env.Count);
        foreach (var kv in env)
        {
            WriteString(ms, kv.Key);
            WriteString(ms, kv.Value ?? string.Empty);
        }

        return new PtyHostFrame(PtyHostFrameType.Spawn, ms.ToArray());
    }

    public static PtyHostFrame CreateResize(ushort cols, ushort rows)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), cols);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), rows);
        return new PtyHostFrame(PtyHostFrameType.Resize, payload);
    }

    public static PtyHostFrame CreateSignal(int sig)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, sig);
        return new PtyHostFrame(PtyHostFrameType.Signal, payload);
    }

    public static PtyHostFrame CreateExit(int exitCode, int pid)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), exitCode);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), pid);
        return new PtyHostFrame(PtyHostFrameType.Exit, payload);
    }

    public static PtyHostFrame CreateError(int code, string message)
    {
        message ??= string.Empty;
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[8 + msgBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), code);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)msgBytes.Length);
        msgBytes.CopyTo(payload.AsSpan(8));
        return new PtyHostFrame(PtyHostFrameType.Error, payload);
    }

    public static PtyHostFrame CreateClose() => PtyHostFrame.Empty(PtyHostFrameType.Close);

    /// <summary>
    /// Helper → server ack after successful Spawn. Payload is int32 LE child_pid.
    /// </summary>
    public static PtyHostFrame CreateSpawned(int childPid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, childPid);
        return new PtyHostFrame(PtyHostFrameType.Spawned, payload);
    }

    public static int ParseSpawned(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("Spawned payload too short.");
        return BinaryPrimitives.ReadInt32LittleEndian(payload);
    }

    public static PtyHostFrame CreateOutput(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxChunkPayload)
            throw new ArgumentOutOfRangeException(nameof(data), "Chunk Output to ≤ 64 KiB.");
        return new PtyHostFrame(PtyHostFrameType.Output, data.ToArray());
    }

    public static PtyHostFrame CreateInput(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxChunkPayload)
            throw new ArgumentOutOfRangeException(nameof(data), "Chunk Input to ≤ 64 KiB.");
        return new PtyHostFrame(PtyHostFrameType.Input, data.ToArray());
    }

    public static (int ExitCode, int Pid) ParseExit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new InvalidDataException("Exit payload too short.");
        return (
            BinaryPrimitives.ReadInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)));
    }

    public static (ushort Cols, ushort Rows) ParseResize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("Resize payload too short.");
        return (
            BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2)));
    }

    public static int ParseSignal(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("Signal payload too short.");
        return BinaryPrimitives.ReadInt32LittleEndian(payload);
    }

    public const int HandoffNonceLength = 16;

    /// <summary>
    /// PauseOutput (10): nonce[16] + generation i32 + path_len u32 + path utf-8.
    /// </summary>
    public static PtyHostFrame CreatePauseOutput(
        ReadOnlySpan<byte> nonce,
        int generation,
        string fdSocketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fdSocketPath);
        ValidateNonce(nonce);
        using var ms = new MemoryStream();
        ms.Write(nonce);
        WriteInt32(ms, generation);
        WriteString(ms, fdSocketPath);
        return new PtyHostFrame(PtyHostFrameType.PauseOutput, ms.ToArray());
    }

    /// <summary>
    /// Adopt (11): nonce[16] + generation i32 + child_pid i32 + path_len u32 + path utf-8.
    /// </summary>
    public static PtyHostFrame CreateAdopt(
        ReadOnlySpan<byte> nonce,
        int generation,
        int childPid,
        string fdSocketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fdSocketPath);
        ValidateNonce(nonce);
        using var ms = new MemoryStream();
        ms.Write(nonce);
        WriteInt32(ms, generation);
        WriteInt32(ms, childPid);
        WriteString(ms, fdSocketPath);
        return new PtyHostFrame(PtyHostFrameType.Adopt, ms.ToArray());
    }

    /// <summary>
    /// Adopted (12): nonce[16] + generation i32 + child_pid i32.
    /// </summary>
    public static PtyHostFrame CreateAdopted(ReadOnlySpan<byte> nonce, int generation, int childPid)
    {
        ValidateNonce(nonce);
        var payload = new byte[HandoffNonceLength + 8];
        nonce.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(HandoffNonceLength, 4), generation);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(HandoffNonceLength + 4, 4), childPid);
        return new PtyHostFrame(PtyHostFrameType.Adopted, payload);
    }

    /// <summary>
    /// CloseOldOwner (13): nonce[16] + generation i32.
    /// </summary>
    public static PtyHostFrame CreateCloseOldOwner(ReadOnlySpan<byte> nonce, int generation)
    {
        ValidateNonce(nonce);
        var payload = new byte[HandoffNonceLength + 4];
        nonce.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(HandoffNonceLength, 4), generation);
        return new PtyHostFrame(PtyHostFrameType.CloseOldOwner, payload);
    }

    /// <summary>ResumeHandoff (15): empty — unpause after failed export.</summary>
    public static PtyHostFrame CreateResumeHandoff() =>
        PtyHostFrame.Empty(PtyHostFrameType.ResumeHandoff);

    /// <summary>ReleaseAdopted (16): empty — new helper drops master without killing child.</summary>
    public static PtyHostFrame CreateReleaseAdopted() =>
        PtyHostFrame.Empty(PtyHostFrameType.ReleaseAdopted);

    public static (byte[] Nonce, int Generation, string Path) ParsePauseOutput(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandoffNonceLength + 8)
            throw new InvalidDataException("PauseOutput payload too short.");
        var nonce = payload[..HandoffNonceLength].ToArray();
        var gen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength, 4));
        var path = ReadPrefixedString(payload, HandoffNonceLength + 4);
        return (nonce, gen, path);
    }

    public static (byte[] Nonce, int Generation, int ChildPid, string Path) ParseAdopt(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandoffNonceLength + 12)
            throw new InvalidDataException("Adopt payload too short.");
        var nonce = payload[..HandoffNonceLength].ToArray();
        var gen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength, 4));
        var pid = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength + 4, 4));
        var path = ReadPrefixedString(payload, HandoffNonceLength + 8);
        return (nonce, gen, pid, path);
    }

    public static (byte[] Nonce, int Generation, int ChildPid) ParseAdopted(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandoffNonceLength + 8)
            throw new InvalidDataException("Adopted payload too short.");
        return (
            payload[..HandoffNonceLength].ToArray(),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength + 4, 4)));
    }

    public static (byte[] Nonce, int Generation) ParseCloseOldOwner(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HandoffNonceLength + 4)
            throw new InvalidDataException("CloseOldOwner payload too short.");
        return (
            payload[..HandoffNonceLength].ToArray(),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HandoffNonceLength, 4)));
    }

    private static void ValidateNonce(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != HandoffNonceLength)
            throw new ArgumentException($"Nonce must be {HandoffNonceLength} bytes.", nameof(nonce));
    }

    private static string ReadPrefixedString(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset + 4 > payload.Length)
            throw new InvalidDataException("Truncated string length.");
        var len = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        if (offset + 4 + len > (uint)payload.Length)
            throw new InvalidDataException("Truncated string body.");
        return Encoding.UTF8.GetString(payload.Slice(offset + 4, (int)len));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }
}
