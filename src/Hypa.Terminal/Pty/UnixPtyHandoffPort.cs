using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Unix AF_UNIX private handoff port for H2. Length-prefixed control messages;
/// HandleMeta carries one SCM_RIGHTS master FD. Timeout is owned by the caller (5s).
/// </summary>
public sealed class UnixPtyHandoffPort : IPtyHandoffPort
{
    private readonly Socket _socket;
    private readonly string? _listenPath;
    private readonly string? _privateDir;
    private readonly bool _ownsListenerPath;
    private NetworkStream? _stream;
    private PtyHandoffHandle? _pendingHandle;
    private int _disposed;
    /// <summary>Serialize sends. Independent of receive so Abort can be observed mid-Adopt/Commit.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    /// <summary>Serialize receives. UDS is full-duplex; concurrent send+recv is required for H2 fail-closed.</summary>
    private readonly SemaphoreSlim _recvGate = new(1, 1);

    private UnixPtyHandoffPort(
        Socket socket,
        string? listenPath,
        string? privateDir,
        bool ownsListenerPath)
    {
        _socket = socket;
        _listenPath = listenPath;
        _privateDir = privateDir;
        _ownsListenerPath = ownsListenerPath;
    }

    public string? SocketPath => _listenPath;

    /// <summary>
    /// Create a listening port in a private 0700 directory (XDG_RUNTIME_DIR or temp).
    /// Peer must <see cref="ConnectAsync"/>. Socket mode is 0600; accept checks peer UID.
    /// </summary>
    public static UnixPtyHandoffPort Listen()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("H2 handoff port is Unix-only.");

        var (dir, path) = UnixPrivateSocketPath.Create("hypa-handoff");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            UnixPrivateSocketPath.HardenSocketFile(path);
            UnixPtyFdValidation.SetCloexec(listener.SafeHandle.DangerousGetHandle().ToInt32());
            listener.Listen(1);
            return new UnixPtyHandoffPort(listener, path, dir, ownsListenerPath: true);
        }
        catch
        {
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
            throw;
        }
    }

    /// <summary>Connect to a peer that called <see cref="Listen"/>.</summary>
    public static async Task<UnixPtyHandoffPort> ConnectAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("H2 handoff port is Unix-only.");
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Fail closed on path replacement / wrong mode before dialing (criterion 17).
        UnixPrivateSocketPath.ValidateConnectPath(path);

        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
            UnixPrivateSocketPath.ValidatePeerIsSelf(sock);
            UnixPtyFdValidation.SetCloexec(sock.SafeHandle.DangerousGetHandle().ToInt32());
            var port = new UnixPtyHandoffPort(sock, listenPath: null, privateDir: null, ownsListenerPath: false);
            port._stream = new NetworkStream(sock, ownsSocket: false);
            return port;
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    /// <summary>Accept one peer connection (listener side).</summary>
    public async Task AcceptAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_stream is not null)
            return;

        var client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
        try
        {
            UnixPrivateSocketPath.ValidatePeerIsSelf(client);
            UnixPtyFdValidation.SetCloexec(client.SafeHandle.DangerousGetHandle().ToInt32());
        }
        catch
        {
            try { client.Dispose(); } catch { /* ignore */ }
            throw;
        }

        // Replace listener with accepted socket for subsequent I/O.
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // ignore
        }

        // We cannot reassign readonly _socket — hold accepted socket via stream only.
        // Use a field for the accepted connection.
        _accepted = client;
        _stream = new NetworkStream(client, ownsSocket: false);
    }

    private Socket? _accepted;

    private Socket IoSocket => _accepted ?? _socket;

    public async ValueTask SendAsync(PtyHandoffMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureConnected();

        if (message.Kind == PtyHandoffMessageKind.HandleMeta)
            throw new InvalidOperationException("Use SendHandleAsync for HandleMeta + SCM_RIGHTS.");

        var payload = EncodeMessage(message);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(payload.type, payload.body, withFd: false, fd: -1, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask SendHandleAsync(
        PtyHandoffMessage meta,
        PtyHandoffHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(handle);
        if (meta.Kind != PtyHandoffMessageKind.HandleMeta)
            throw new ArgumentException("Handle send requires HandleMeta.", nameof(meta));

        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureConnected();

        var payload = EncodeMessage(meta);
        var fd = handle.DangerousGetFileDescriptor();

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(payload.type, payload.body, withFd: true, fd, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<PtyHandoffMessage> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureConnected();

        await _recvGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (type, body, handle) = await ReadFrameAsync(ct).ConfigureAwait(false);
            var msg = DecodeMessage(type, body);
            if (handle is not null)
            {
                _pendingHandle?.Dispose();
                _pendingHandle = handle;
            }

            return msg;
        }
        finally
        {
            _recvGate.Release();
        }
    }

    public ValueTask<PtyHandoffHandle> ReceiveHandleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ct.ThrowIfCancellationRequested();

        if (_pendingHandle is null)
        {
            return ValueTask.FromException<PtyHandoffHandle>(
                new InvalidOperationException(
                    "No pending handoff handle. Receive HandleMeta first via ReceiveAsync."));
        }

        var h = _pendingHandle;
        _pendingHandle = null;
        return ValueTask.FromResult(h);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _pendingHandle?.Dispose();
        _pendingHandle = null;

        try { _stream?.Dispose(); }
        catch { /* ignore */ }

        try { _accepted?.Dispose(); }
        catch { /* ignore */ }

        try { _socket.Dispose(); }
        catch { /* ignore */ }

        _sendGate.Dispose();
        _recvGate.Dispose();

        if (_ownsListenerPath)
        {
            if (_listenPath is not null)
                UnixPrivateSocketPath.TryUnlink(_listenPath);
            UnixPrivateSocketPath.TryDeleteDirectory(_privateDir);
        }
    }

    private void EnsureConnected()
    {
        if (_stream is null)
            throw new InvalidOperationException("Handoff port is not connected. Call AcceptAsync or ConnectAsync first.");
    }

    private async Task WriteFrameAsync(
        byte type,
        byte[] body,
        bool withFd,
        int fd,
        CancellationToken ct)
    {
        // length = 1 (type) + body
        var bodyLen = 1 + body.Length;
        var header = new byte[4 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)bodyLen);
        header[4] = type;

        // Always write control bytes via the raw socket so NetworkStream buffering
        // cannot swallow the SCM_RIGHTS dummy byte that follows HandleMeta.
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        if (body.Length > 0)
            body.CopyTo(frame.AsSpan(header.Length));

        // AF_UNIX SOCK_STREAM sends are not message-atomic; loop until complete.
        var offset = 0;
        while (offset < frame.Length)
        {
            var n = await IoSocket.SendAsync(
                    frame.AsMemory(offset, frame.Length - offset),
                    SocketFlags.None,
                    ct)
                .ConfigureAwait(false);
            if (n <= 0)
                throw new IOException("Handoff peer closed during frame write.");
            offset += n;
        }

        if (withFd)
        {
            var sockFd = IoSocket.SafeHandle.DangerousGetHandle().ToInt32();
            UnixAncillaryFd.SendFd(sockFd, fd);
        }
    }

    private async Task<(byte Type, byte[] Body, PtyHandoffHandle? Handle)> ReadFrameAsync(
        CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactSocketAsync(IoSocket, lenBuf, ct).ConfigureAwait(false);
        var bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (bodyLen == 0 || bodyLen > 1_048_576)
            throw new InvalidDataException($"Invalid handoff frame length {bodyLen}.");

        var bodyWithType = new byte[bodyLen];
        await ReadExactSocketAsync(IoSocket, bodyWithType, ct).ConfigureAwait(false);
        var type = bodyWithType[0];
        var payload = bodyWithType.Length == 1
            ? Array.Empty<byte>()
            : bodyWithType.AsSpan(1).ToArray();

        PtyHandoffHandle? handle = null;
        if (type == (byte)PtyHandoffMessageKind.HandleMeta)
        {
            var sockFd = IoSocket.SafeHandle.DangerousGetHandle().ToInt32();
            // Reject non-PTY SCM_RIGHTS payloads; set CLOEXEC on the adopted master.
            var safe = UnixAncillaryFd.RecvPtyMasterSafeHandle(sockFd);
            handle = new PtyHandoffHandle(safe);
        }

        return (type, payload, handle);
    }

    private static async Task ReadExactSocketAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("Handoff peer closed during frame read.");
            offset += n;
        }
    }

    internal static (byte type, byte[] body) EncodeMessage(PtyHandoffMessage message)
    {
        using var ms = new MemoryStream();
        switch (message.Kind)
        {
            case PtyHandoffMessageKind.Hello:
                WriteString(ms, message.RuntimeSessionId);
                WriteString(ms, message.PaneId);
                WriteInt32(ms, message.Generation);
                WriteNonce(ms, message.Nonce);
                break;
            case PtyHandoffMessageKind.Prepare:
                WriteNonce(ms, message.Nonce);
                WriteString(ms, message.TargetRuntimeId);
                break;
            case PtyHandoffMessageKind.HandleMeta:
                WriteNonce(ms, message.Nonce);
                WriteInt32(ms, message.Generation);
                WriteInt32(ms, message.ChildPid);
                WriteUInt16(ms, message.Cols);
                WriteUInt16(ms, message.Rows);
                break;
            case PtyHandoffMessageKind.Commit:
                WriteNonce(ms, message.Nonce);
                break;
            case PtyHandoffMessageKind.Abort:
                WriteInt32(ms, message.AbortReason);
                WriteString(ms, message.AbortMessage ?? string.Empty);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.Kind, "Unknown handoff message kind.");
        }

        return ((byte)message.Kind, ms.ToArray());
    }

    internal static PtyHandoffMessage DecodeMessage(byte type, ReadOnlySpan<byte> body)
    {
        var kind = (PtyHandoffMessageKind)type;
        var offset = 0;
        switch (kind)
        {
            case PtyHandoffMessageKind.Hello:
                {
                    var runtimeId = ReadString(body, ref offset);
                    var paneId = ReadString(body, ref offset);
                    var gen = ReadInt32(body, ref offset);
                    var nonce = ReadNonce(body, ref offset);
                    return PtyHandoffMessage.CreateHello(runtimeId, paneId, gen, nonce);
                }
            case PtyHandoffMessageKind.Prepare:
                {
                    var nonce = ReadNonce(body, ref offset);
                    var target = ReadString(body, ref offset);
                    return PtyHandoffMessage.CreatePrepare(nonce, target);
                }
            case PtyHandoffMessageKind.HandleMeta:
                {
                    var nonce = ReadNonce(body, ref offset);
                    var gen = ReadInt32(body, ref offset);
                    var pid = ReadInt32(body, ref offset);
                    var cols = ReadUInt16(body, ref offset);
                    var rows = ReadUInt16(body, ref offset);
                    return PtyHandoffMessage.CreateHandleMeta(nonce, gen, pid, cols, rows);
                }
            case PtyHandoffMessageKind.Commit:
                {
                    var nonce = ReadNonce(body, ref offset);
                    return PtyHandoffMessage.CreateCommit(nonce);
                }
            case PtyHandoffMessageKind.Abort:
                {
                    var reason = body.Length >= 4 ? ReadInt32(body, ref offset) : 0;
                    var msg = body.Length > offset ? ReadString(body, ref offset) : null;
                    return PtyHandoffMessage.CreateAbort(reason, msg);
                }
            default:
                throw new InvalidDataException($"Unknown handoff message type {type}.");
        }
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteNonce(Stream s, byte[] nonce)
    {
        if (nonce.Length != PtyHandoffMessage.NonceLength)
            throw new InvalidOperationException("Nonce must be 16 bytes.");
        s.Write(nonce);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteUInt16(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        s.Write(buf);
    }

    private static string ReadString(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length)
            throw new InvalidDataException("Truncated string length.");
        var len = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        if (len > 1_048_576 || offset + (int)len > body.Length)
            throw new InvalidDataException("Truncated string body.");
        var s = Encoding.UTF8.GetString(body.Slice(offset, (int)len));
        offset += (int)len;
        return s;
    }

    private static byte[] ReadNonce(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + PtyHandoffMessage.NonceLength > body.Length)
            throw new InvalidDataException("Truncated nonce.");
        var n = body.Slice(offset, PtyHandoffMessage.NonceLength).ToArray();
        offset += PtyHandoffMessage.NonceLength;
        return n;
    }

    private static int ReadInt32(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length)
            throw new InvalidDataException("Truncated int32.");
        var v = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        return v;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 2 > body.Length)
            throw new InvalidDataException("Truncated uint16.");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        return v;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("Handoff peer closed during frame read.");
            offset += n;
        }
    }

}
