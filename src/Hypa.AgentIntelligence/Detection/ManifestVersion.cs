using System.Globalization;
using System.Text;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>Compare dotted numeric versions. Empty segments are invalid.</summary>
internal readonly struct ManifestVersion : IComparable<ManifestVersion>
{
    private readonly string _value;

    private ManifestVersion(string value) => _value = value;

    public static ManifestVersion Parse(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("version must not be empty");
        foreach (var segment in trimmed.Split('.'))
        {
            if (segment.Length == 0)
                throw new ArgumentException($"version {trimmed} contains an empty segment");
            foreach (var ch in segment)
            {
                if (!char.IsAsciiDigit(ch))
                    throw new ArgumentException($"version {trimmed} must be dotted numeric");
            }

            if (!ulong.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException($"version {trimmed} contains an oversized segment");
        }

        return new ManifestVersion(trimmed);
    }

    public static bool TryParse(string? value, out ManifestVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            version = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public int CompareTo(ManifestVersion other)
    {
        var left = (_value ?? "").Split('.');
        var right = (other._value ?? "").Split('.');
        var i = 0;
        while (true)
        {
            var hasLeft = i < left.Length;
            var hasRight = i < right.Length;
            if (!hasLeft && !hasRight)
                return 0;
            if (hasLeft && hasRight)
            {
                var l = ulong.Parse(left[i], CultureInfo.InvariantCulture);
                var r = ulong.Parse(right[i], CultureInfo.InvariantCulture);
                var cmp = l.CompareTo(r);
                if (cmp != 0)
                    return cmp;
                i++;
                continue;
            }

            if (hasLeft)
            {
                var l = ulong.Parse(left[i], CultureInfo.InvariantCulture);
                if (l == 0)
                {
                    i++;
                    continue;
                }

                return 1;
            }

            var rv = ulong.Parse(right[i], CultureInfo.InvariantCulture);
            if (rv == 0)
            {
                i++;
                continue;
            }

            return -1;
        }
    }

    public override string ToString() => _value ?? "";
}

internal static class ManifestRegex
{
    /// <summary>
    // Translate constructs that
    /// <c>\x{HHHH}</c> / <c>\u{HHHH}</c> → .NET <c>\uXXXX</c> UTF-16 units.
    /// Supplementary scalars use <see cref="char.ConvertFromUtf32"/> (surrogate pair).
    /// .NET regex does not accept <c>\UXXXXXXXX</c>.
    /// <c>\p{Alphabetic}</c> → <c>\p{L}</c> (Unicode letter).
    /// </summary>
    public static string TranslateForDotNet(string pattern) =>
        TranslateUnicodeProperties(TranslateRustHexEscapes(pattern));

    public static string TranslateRustHexEscapes(string pattern)
    {
        var sb = new StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\'
                && i + 3 < pattern.Length
                && pattern[i + 1] is 'x' or 'u'
                && pattern[i + 2] == '{')
            {
                var end = pattern.IndexOf('}', i + 3);
                if (end > i + 3)
                {
                    var hex = pattern[(i + 3)..end];
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                        && Rune.IsValid((int)code))
                    {
                        foreach (var unit in char.ConvertFromUtf32((int)code))
                            sb.Append(CultureInfo.InvariantCulture, $"\\u{(ushort)unit:x4}");
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(pattern[i]);
        }

        return sb.ToString();
    }

    private static string TranslateUnicodeProperties(string pattern) =>
        pattern.Replace(@"\p{Alphabetic}", @"\p{L}", StringComparison.Ordinal);
}
