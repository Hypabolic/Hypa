using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// AES-GCM for control, binary, and resize payloads. Heartbeat stays hop-by-hop.
/// One <see cref="AesGcm"/> for each direction serves every frame of the
/// session. A new one for each frame put a finalizable native handle on the
/// GC heap for each frame.
/// </summary>
public sealed class ApplicationFrameCipher : IApplicationFrameCipher
{
    private const int AadBytes = 14;

    private readonly AesGcm _seal;
    private readonly AesGcm _open;
    // AesGcm is not safe for calls from more than one thread at a time.
    // Senders seal before they take the session write lock.
    private readonly object _sealGate = new();
    private readonly object _openGate = new();
    private readonly object _receiveGate = new();
    private readonly ulong?[] _lastOpenedByKind = new ulong?[6];

    private ApplicationFrameCipher(byte[] sendKey, byte[] receiveKey)
    {
        _seal = new AesGcm(sendKey, StreamLimits.ApplicationAeadTagBytes);
        _open = new AesGcm(receiveKey, StreamLimits.ApplicationAeadTagBytes);
    }

    public static ConnectivityOutcome<ApplicationFrameCipher> Create(
        ReadOnlySpan<byte> sharedSecret,
        JoinNonce nonce,
        string placementId,
        JoinRole role)
    {
        if (sharedSecret.Length == 0)
        {
            return ConnectivityOutcome<ApplicationFrameCipher>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "join shared secret is required");
        }

        if (string.IsNullOrWhiteSpace(placementId))
        {
            return ConnectivityOutcome<ApplicationFrameCipher>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "placement id is required");
        }

        var salt = Encoding.UTF8.GetBytes(nonce.Value);
        var muxToClient = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret.ToArray(),
            32,
            salt,
            Encoding.UTF8.GetBytes("hypa.join.v0.mux-to-client." + placementId));
        var clientToMux = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret.ToArray(),
            32,
            salt,
            Encoding.UTF8.GetBytes("hypa.join.v0.client-to-mux." + placementId));
        var send = role == JoinRole.Mux ? muxToClient : clientToMux;
        var receive = role == JoinRole.Mux ? clientToMux : muxToClient;
        return ConnectivityOutcome<ApplicationFrameCipher>.Success(new ApplicationFrameCipher(send, receive));
    }

    public ConnectivityOutcome<StreamFrame> Seal(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ApplicationEncryption.Protects(frame.Kind))
            return ConnectivityOutcome<StreamFrame>.Success(frame);

        var sealedPayload = new byte[SealedLength(frame)];
        var result = SealTo(frame, sealedPayload);
        return result.Ok
            ? ConnectivityOutcome<StreamFrame>.Success(frame with { Payload = sealedPayload })
            : ConnectivityOutcome<StreamFrame>.Failure(
                result.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                result.Detail ?? "application frame seal failed");
    }

    public int SealedLength(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return ApplicationEncryption.Protects(frame.Kind)
            ? ApplicationEncryption.CiphertextLength(frame.PayloadLength)
            : frame.PayloadLength;
    }

    public ConnectivityOutcome SealTo(StreamFrame frame, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (destination.Length != SealedLength(frame))
            throw new ArgumentException("destination is not the sealed length", nameof(destination));

        var plaintext = frame.Payload.Span;
        if (!ApplicationEncryption.Protects(frame.Kind))
        {
            plaintext.CopyTo(destination);
            return ConnectivityOutcome.Success();
        }

        try
        {
            Span<byte> nonce = stackalloc byte[StreamLimits.ApplicationAeadNonceBytes];
            Span<byte> aad = stackalloc byte[AadBytes];
            WriteNonce(frame, nonce);
            WriteAad(frame, aad);
            lock (_sealGate)
            {
                _seal.Encrypt(
                    nonce,
                    plaintext,
                    destination[..plaintext.Length],
                    destination[plaintext.Length..],
                    aad);
            }

            return ConnectivityOutcome.Success();
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "application frame seal failed");
        }
    }

    public ConnectivityOutcome<StreamFrame> Open(StreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ApplicationEncryption.Protects(frame.Kind))
            return ConnectivityOutcome<StreamFrame>.Success(frame);

        if (IsReplayed(frame.Kind, frame.Sequence))
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "application frame was replayed");
        }

        var ciphertext = frame.Payload.Span;
        if (ciphertext.Length < StreamLimits.ApplicationAeadTagBytes)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "application frame is not encrypted");
        }

        try
        {
            var plaintextLength = ciphertext.Length - StreamLimits.ApplicationAeadTagBytes;
            var plaintext = new byte[plaintextLength];
            Span<byte> nonce = stackalloc byte[StreamLimits.ApplicationAeadNonceBytes];
            Span<byte> aad = stackalloc byte[AadBytes];
            WriteNonce(frame, nonce);
            WriteAad(frame, aad);
            lock (_openGate)
            {
                _open.Decrypt(
                    nonce,
                    ciphertext[..plaintextLength],
                    ciphertext[plaintextLength..],
                    plaintext,
                    aad);
            }
            if (!TryMarkOpened(frame.Kind, frame.Sequence))
            {
                return ConnectivityOutcome<StreamFrame>.Failure(
                    ConnectivityReasons.ApplicationEncryptionRequired,
                    "application frame was replayed");
            }

            return ConnectivityOutcome<StreamFrame>.Success(frame with { Payload = plaintext });
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "application frame is not encrypted");
        }
    }

    private bool IsReplayed(StreamFrameKind kind, ulong sequence)
    {
        var index = (int)kind;
        lock (_receiveGate)
        {
            if (index < 0 || index >= _lastOpenedByKind.Length)
                return true;
            return _lastOpenedByKind[index] is ulong last && sequence <= last;
        }
    }

    private bool TryMarkOpened(StreamFrameKind kind, ulong sequence)
    {
        var index = (int)kind;
        lock (_receiveGate)
        {
            if (index < 0 || index >= _lastOpenedByKind.Length)
                return false;
            if (_lastOpenedByKind[index] is ulong last && sequence <= last)
                return false;
            _lastOpenedByKind[index] = sequence;
            return true;
        }
    }

    private static void WriteNonce(StreamFrame frame, Span<byte> nonce)
    {
        nonce.Clear();
        nonce[0] = (byte)frame.Kind;
        nonce[1] = (byte)frame.Direction;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], frame.Sequence);
    }

    private static void WriteAad(StreamFrame frame, Span<byte> aad)
    {
        aad[0] = (byte)frame.Kind;
        aad[1] = (byte)frame.Direction;
        BinaryPrimitives.WriteUInt32BigEndian(aad[2..], frame.ChannelId);
        BinaryPrimitives.WriteUInt64BigEndian(aad[6..], frame.Sequence);
    }
}
