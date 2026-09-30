using System.Security.Cryptography;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Authenticate the peer ephemeral public key with the join secret.
/// The relay does not hold that secret.
/// </summary>
public static class JoinEphAuthenticator
{
    public static ConnectivityOutcome<JoinBootstrap> Stamp(
        JoinBootstrap bootstrap,
        JoinEphemeralKeyPair keys)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(keys);
        var mac = CreateMac(bootstrap.Capability.Secret, keys.PublicKey);
        if (!mac.Ok || mac.Value is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                mac.Reason ?? ConnectivityReasons.ApplicationEncryptionRequired,
                mac.Detail ?? "join secret is required");
        }

        return ConnectivityOutcome<JoinBootstrap>.Success(
            bootstrap with
            {
                EphPublicKey = keys.PublicKey,
                EphPublicMac = mac.Value,
            });
    }

    public static ConnectivityOutcome VerifyPeer(
        JoinSecret secret,
        string? peerPublicKey,
        string? peerPublicMac)
    {
        var key = JoinEphPublicKeys.Require(peerPublicKey);
        if (!key.Ok)
            return key;

        if (secret.IsEmpty)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "join secret is required");
        }

        var expected = CreateMac(secret, peerPublicKey);
        if (!expected.Ok || expected.Value is null)
            return expected.WithoutValue();

        if (!JoinEphPublicMacs.TryDecode(peerPublicMac, out var actual)
            || !JoinEphPublicMacs.TryDecode(expected.Value, out var want)
            || !CryptographicOperations.FixedTimeEquals(actual, want))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "peer ephemeral public key is not authenticated");
        }

        return ConnectivityOutcome.Success();
    }

    public static ConnectivityOutcome<string> CreateMac(JoinSecret secret, string? publicKey)
    {
        if (secret.IsEmpty)
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "join secret is required");
        }

        if (!JoinEphPublicKeys.TryDecode(publicKey, out var raw))
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "ephemeral public key is required");
        }

        return ConnectivityOutcome<string>.Success(
            Convert.ToBase64String(HMACSHA256.HashData(secret.Value, raw)));
    }
}
