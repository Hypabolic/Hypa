using System.Globalization;

namespace Hypa.Connectivity.Domain;

/// <summary>
/// Transferable pairing QR. The short pairing code is not this value.
/// Body is code, expiry, public key, and device signature.
/// </summary>
public static class PairingOfferFormat
{
    public static string EncodeQr(
        string pairingCode,
        long expiresUnix,
        string spkiBase64Url,
        string signatureBase64Url) =>
        DevicePairingPaths.QrPrefix
        + pairingCode
        + "."
        + expiresUnix.ToString(CultureInfo.InvariantCulture)
        + "."
        + spkiBase64Url
        + "."
        + signatureBase64Url;

    public static string SignedBody(
        string pairingCode,
        long expiresUnix,
        string spkiBase64Url) =>
        pairingCode
        + "."
        + expiresUnix.ToString(CultureInfo.InvariantCulture)
        + "."
        + spkiBase64Url;

    public static bool TryDecodeQr(
        string? value,
        out string pairingCode,
        out long expiresUnix,
        out byte[] spki,
        out byte[] signature)
    {
        pairingCode = "";
        expiresUnix = 0;
        spki = [];
        signature = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(DevicePairingPaths.QrPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = trimmed[DevicePairingPaths.QrPrefix.Length..];
        var parts = body.Split('.', 4, StringSplitOptions.None);
        if (parts.Length != 4)
            return false;
        if (parts[0].Length != 8)
            return false;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out expiresUnix)
            || expiresUnix <= 0)
        {
            return false;
        }

        if (!TryFromBase64Url(parts[2], out spki) || spki.Length == 0)
            return false;
        if (!TryFromBase64Url(parts[3], out signature) || signature.Length == 0)
            return false;

        pairingCode = parts[0];
        return true;
    }

    public static string ToBase64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryFromBase64Url(string? value, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                return false;
        }

        try
        {
            data = Convert.FromBase64String(padded);
            return data.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
