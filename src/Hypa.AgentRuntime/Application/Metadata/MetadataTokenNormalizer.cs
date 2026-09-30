using System.Text;

namespace Hypa.AgentRuntime.Application.Metadata;

public static class MetadataTokenNormalizer
{
    public static bool TryNormalizeKey(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;
        if (normalized.Length is < MetadataTokenLimits.MinKeyLength or > MetadataTokenLimits.MaxKeyLength)
            return false;
        foreach (var ch in normalized)
        {
            if (!IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
                return false;
        }
        return true;
    }

    public static bool TryNormalizeSource(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;
        if (normalized.Length is < MetadataTokenLimits.MinSourceLength or > MetadataTokenLimits.MaxSourceLength)
            return false;
        foreach (var ch in normalized)
        {
            if (!IsAsciiLetterOrDigit(ch) && ch is not ':' and not '.' and not '_' and not '-')
                return false;
        }
        return true;
    }

    public static string NormalizeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (!char.IsControl(ch))
                builder.Append(ch);
            if (builder.Length == MetadataTokenLimits.MaxValueLength)
                break;
        }
        return builder.ToString().Trim();
    }

    public static bool TryNormalizeTtl(int? ttlMs, out TimeSpan? ttl)
    {
        ttl = null;
        if (ttlMs is null)
            return true;
        if (ttlMs.Value is < MetadataTokenLimits.MinTtlMilliseconds or > MetadataTokenLimits.MaxTtlMilliseconds)
            return false;
        ttl = TimeSpan.FromMilliseconds(ttlMs.Value);
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
