using System.Security.Cryptography;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Per-join P-256 ECDH key. The relay forwards the public point only.
/// </summary>
public sealed class JoinEphemeralKeyPair : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    private bool _disposed;

    private JoinEphemeralKeyPair(ECDiffieHellman ecdh, string publicKey)
    {
        _ecdh = ecdh;
        PublicKey = publicKey;
    }

    public string PublicKey { get; }

    public static JoinEphemeralKeyPair Create()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(includePrivateParameters: false);
        var raw = EncodePoint(parameters.Q);
        return new JoinEphemeralKeyPair(ecdh, JoinEphPublicKeys.Encode(raw));
    }

    public static string CreatePublicKey()
    {
        using var keys = Create();
        return keys.PublicKey;
    }

    public ConnectivityOutcome<byte[]> DeriveSharedSecret(string? peerPublicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!JoinEphPublicKeys.TryDecode(peerPublicKey, out var raw))
        {
            return ConnectivityOutcome<byte[]>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "peer ephemeral public key is required");
        }

        try
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = DecodePoint(raw),
            });
            return ConnectivityOutcome<byte[]>.Success(_ecdh.DeriveRawSecretAgreement(peer.PublicKey));
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome<byte[]>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "peer ephemeral public key is invalid");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ecdh.Dispose();
        GC.SuppressFinalize(this);
    }

    private static byte[] EncodePoint(ECPoint point)
    {
        var x = PadCoordinate(point.X);
        var y = PadCoordinate(point.Y);
        var raw = new byte[JoinEphPublicKeys.SizeBytes];
        raw[0] = JoinEphPublicKeys.UncompressedPrefix;
        x.CopyTo(raw.AsSpan(1));
        y.CopyTo(raw.AsSpan(1 + 32));
        return raw;
    }

    private static ECPoint DecodePoint(byte[] raw) =>
        new()
        {
            X = raw.AsSpan(1, 32).ToArray(),
            Y = raw.AsSpan(33, 32).ToArray(),
        };

    private static byte[] PadCoordinate(byte[]? value)
    {
        if (value is null || value.Length > 32)
            throw new CryptographicException("P-256 coordinate is invalid");

        if (value.Length == 32)
            return value;

        var padded = new byte[32];
        value.CopyTo(padded, 32 - value.Length);
        return padded;
    }
}
