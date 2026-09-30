using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// AES-GCM for control, binary, and resize payloads. Heartbeat stays hop-by-hop.
/// </summary>
public sealed class ApplicationFrameCipher : IApplicationFrameCipher
{
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly object _receiveGate = new();
    private readonly ulong?[] _lastOpenedByKind = new ulong?[6];

    private ApplicationFrameCipher(byte[] sendKey, byte[] receiveKey)
    {
        _sendKey = sendKey;
        _receiveKey = receiveKey;
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

        try
        {
            var plaintext = frame.Payload.Span;
            var sealedPayload = new byte[ApplicationEncryption.CiphertextLength(plaintext.Length)];
            var nonce = CreateNonce(frame);
            var aad = CreateAad(frame);
            using var gcm = new AesGcm(_sendKey, StreamLimits.ApplicationAeadTagBytes);
            gcm.Encrypt(
                nonce,
                plaintext,
                sealedPayload.AsSpan(0, plaintext.Length),
                sealedPayload.AsSpan(plaintext.Length),
                aad);
            return ConnectivityOutcome<StreamFrame>.Success(frame with { Payload = sealedPayload });
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome<StreamFrame>.Failure(
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
            var nonce = CreateNonce(frame);
            var aad = CreateAad(frame);
            using var gcm = new AesGcm(_receiveKey, StreamLimits.ApplicationAeadTagBytes);
            gcm.Decrypt(
                nonce,
                ciphertext[..plaintextLength],
                ciphertext[plaintextLength..],
                plaintext,
                aad);
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

    private static byte[] CreateNonce(StreamFrame frame)
    {
        var nonce = new byte[StreamLimits.ApplicationAeadNonceBytes];
        nonce[0] = (byte)frame.Kind;
        nonce[1] = (byte)frame.Direction;
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), frame.Sequence);
        return nonce;
    }

    private static byte[] CreateAad(StreamFrame frame)
    {
        var aad = new byte[14];
        aad[0] = (byte)frame.Kind;
        aad[1] = (byte)frame.Direction;
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(2), frame.ChannelId);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(6), frame.Sequence);
        return aad;
    }
}
