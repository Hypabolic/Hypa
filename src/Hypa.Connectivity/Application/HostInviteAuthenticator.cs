using System.Security.Cryptography;
using System.Text;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Prove possession of the redeeming or revoking device private key.</summary>
public static class HostInviteAuthenticator
{
    public static string SignedBody(HostInviteRedeemRequest request) =>
        HostInviteRedeemCodec.TypeName
        + "|"
        + request.InviteId.Value
        + "|"
        + request.DeviceId.Value
        + "|"
        + request.DevicePublicKeySpkiBase64
        + "|"
        + request.IssuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string SignedBody(HostInviteRevokeRequest request) =>
        HostInviteRevokeCodec.TypeName
        + "|"
        + request.DeviceId.Value
        + "|"
        + request.DevicePublicKeySpkiBase64
        + "|"
        + request.IssuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static ConnectivityOutcome<HostInviteRedeemRequest> Stamp(
        HostInviteRedeemRequest request,
        ReadOnlySpan<byte> pkcs8)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stamped = Stamp(SignedBody(request), pkcs8);
        return stamped.Ok
            ? ConnectivityOutcome<HostInviteRedeemRequest>.Success(
                request with { Signature = stamped.Value! })
            : ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                stamped.Reason ?? ConnectivityReasons.Unauthorized,
                stamped.Detail ?? "device signature is required");
    }

    public static ConnectivityOutcome<HostInviteRevokeRequest> Stamp(
        HostInviteRevokeRequest request,
        ReadOnlySpan<byte> pkcs8)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stamped = Stamp(SignedBody(request), pkcs8);
        return stamped.Ok
            ? ConnectivityOutcome<HostInviteRevokeRequest>.Success(
                request with { Signature = stamped.Value! })
            : ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                stamped.Reason ?? ConnectivityReasons.Unauthorized,
                stamped.Detail ?? "device signature is required");
    }

    public static ConnectivityOutcome Verify(HostInviteRedeemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Verify(
            request.DeviceId,
            request.DevicePublicKeySpkiBase64,
            request.Signature,
            SignedBody(request));
    }

    public static ConnectivityOutcome Verify(HostInviteRevokeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Verify(
            request.DeviceId,
            request.DevicePublicKeySpkiBase64,
            request.Signature,
            SignedBody(request));
    }

    private static ConnectivityOutcome<string> Stamp(string body, ReadOnlySpan<byte> pkcs8)
    {
        if (pkcs8.Length == 0)
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.Unauthorized,
                "device private key is missing");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            var signature = ecdsa.SignData(
                Encoding.UTF8.GetBytes(body),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return ConnectivityOutcome<string>.Success(PairingOfferFormat.ToBase64Url(signature));
        }
        catch (CryptographicException ex)
        {
            return ConnectivityOutcome<string>.Failure(
                ConnectivityReasons.Unauthorized,
                ex.Message);
        }
    }

    private static ConnectivityOutcome Verify(
        DeviceId deviceId,
        string spkiBase64,
        string signatureText,
        string body)
    {
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device signature is required");
        }

        if (!PairingOfferFormat.TryFromBase64Url(signatureText, out var signature)
            || signature.Length == 0)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "device signature is invalid");
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(spkiBase64);
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

        if (!string.Equals(derivedId.Value, deviceId.Value, StringComparison.Ordinal))
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
                    Encoding.UTF8.GetBytes(body),
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
