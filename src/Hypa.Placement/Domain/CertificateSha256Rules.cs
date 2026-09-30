namespace Hypa.Placement.Domain;

/// <summary>SHA-256 certificate fingerprint. Catalog data stores no private keys.</summary>
public static class CertificateSha256Rules
{
    public const int HexLength = 64;

    public static bool TryNormalize(string? value, out string hex, out string? error)
    {
        hex = "";
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "certificate fingerprint is required";
            return false;
        }

        var trimmed = value.Trim().Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
        if (trimmed.Length != HexLength)
        {
            error = "certificate fingerprint must be 64 hex characters";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
            {
                error = "certificate fingerprint must be 64 hex characters";
                return false;
            }
        }

        hex = trimmed;
        return true;
    }

    public static bool TryNormalizeOptional(string? value, out string? hex, out string? error)
    {
        hex = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!TryNormalize(value, out var normalized, out error))
            return false;

        hex = normalized;
        return true;
    }
}
