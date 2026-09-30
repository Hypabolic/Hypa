using System.Security.Cryptography;
using System.Text;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Prove possession of the paired device private key on a join bootstrap.
/// </summary>
public static class JoinDeviceAuthenticator
{
    public static string SignedBody(JoinBootstrap bootstrap) =>
        bootstrap.ProtocolVersion.ToString()
        + "|"
        + bootstrap.PlacementId.Value
        + "|"
        + bootstrap.Nonce.Value
        + "|"
        + bootstrap.Role
        + "|"
        + bootstrap.Capability.DeviceId.Value
        + "|"
        + ConnectivityTimestamp.FormatUtc(bootstrap.Capability.ExpiresAt)
        + "|"
        + bootstrap.EphPublicKey;

    public static async ValueTask<ConnectivityOutcome<JoinBootstrap>> StampAsync(
        JoinBootstrap bootstrap,
        IDeviceKeyStore keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(keys);
        if (string.IsNullOrWhiteSpace(bootstrap.EphPublicKey))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.ApplicationEncryptionRequired,
                "ephemeral public key is required");
        }

        var pkcs8 = await keys.LoadPrivateKeyAsync(bootstrap.Capability.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (pkcs8 is null || pkcs8.Length == 0)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Unauthorized,
                "device private key is missing");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            var signature = ecdsa.SignData(
                Encoding.UTF8.GetBytes(SignedBody(bootstrap)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return ConnectivityOutcome<JoinBootstrap>.Success(
                bootstrap with { DeviceSignature = PairingOfferFormat.ToBase64Url(signature) });
        }
        catch (CryptographicException ex)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Unauthorized,
                ex.Message);
        }
    }

    public static ConnectivityOutcome Verify(
        JoinBootstrap bootstrap,
        string publicKeySpkiBase64)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (string.IsNullOrWhiteSpace(bootstrap.DeviceSignature))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device signature is required");
        }

        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device public key is missing");
        }

        if (!PairingOfferFormat.TryFromBase64Url(bootstrap.DeviceSignature, out var signature)
            || signature.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device signature is invalid");
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(publicKeySpkiBase64);
        }
        catch (FormatException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device public key is invalid");
        }

        if (spki.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device public key is invalid");
        }

        DeviceId derivedId;
        try
        {
            derivedId = DevicePairingService.DeviceIdFromPublicKey(spki);
        }
        catch (InvalidOperationException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device public key is invalid");
        }

        if (!string.Equals(derivedId.Value, bootstrap.Capability.DeviceId.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device id does not match public key");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            if (!ecdsa.VerifyData(
                    Encoding.UTF8.GetBytes(SignedBody(bootstrap)),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return ConnectivityOutcome.Failure(
                    ConnectivityReasons.Unauthorized,
                    "device signature is invalid");
            }
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device public key is invalid");
        }

        return ConnectivityOutcome.Success();
    }
}
