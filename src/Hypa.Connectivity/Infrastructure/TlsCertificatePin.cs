using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>SHA-256 pin of a peer certificate. Catalog data stores this hash only.</summary>
public readonly record struct TlsCertificatePin
{
    public string Sha256Hex { get; } = "";

    private TlsCertificatePin(string sha256Hex) => Sha256Hex = sha256Hex;

    public static bool TryParse(string? value, out TlsCertificatePin pin)
    {
        pin = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim().Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
        if (trimmed.Length != 64)
            return false;

        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        pin = new TlsCertificatePin(trimmed);
        return true;
    }

    public bool Matches(X509Certificate? certificate)
    {
        if (certificate is null)
            return false;

        var hex = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))
            .ToLowerInvariant();
        return string.Equals(hex, Sha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
