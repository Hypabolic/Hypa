using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>
/// Optional span style for one sidebar token.
/// </summary>
public sealed record SidebarTokenStyle
{
    public static SidebarTokenStyle Empty { get; } = new();

    public ThemeColor? Fg { get; init; }

    public bool? Bold { get; init; }

    public bool? Dim { get; init; }

    public bool IsEmpty => Fg is null && Bold is null && Dim is null;
}

public enum SidebarTokenRuleKind
{
    Equals,
    Contains,
    StartsWith,
    GreaterThan,
    LessThan,
}

/// <summary>
/// First-match token style rule.
/// </summary>
public sealed record SidebarTokenRule
{
    public required SidebarTokenRuleKind Kind { get; init; }

    public string Text { get; init; } = "";

    public double Threshold { get; init; }

    public bool IgnoreCase { get; init; }

    public SidebarTokenStyle Style { get; init; } = SidebarTokenStyle.Empty;

    public static SidebarTokenStyle FirstMatch(
        IReadOnlyList<SidebarTokenRule> rules,
        SidebarTokenStyle baseStyle,
        string value)
    {
        if (rules is null || rules.Count == 0)
            return baseStyle;

        double? parsed = null;
        var attempted = false;
        foreach (var rule in rules)
        {
            if (!rule.Matches(value, ref parsed, ref attempted))
                continue;
            return new SidebarTokenStyle
            {
                Fg = rule.Style.Fg ?? baseStyle.Fg,
                Bold = rule.Style.Bold ?? baseStyle.Bold,
                Dim = rule.Style.Dim ?? baseStyle.Dim,
            };
        }

        return baseStyle;
    }

    private bool Matches(string value, ref double? parsed, ref bool attempted)
    {
        switch (Kind)
        {
            case SidebarTokenRuleKind.Equals:
                return IgnoreCase
                    ? AsciiIgnoreCaseEquals(value, Text)
                    : string.Equals(value, Text, StringComparison.Ordinal);
            case SidebarTokenRuleKind.Contains:
                return IgnoreCase ? AsciiIgnoreCaseContains(value, Text) : value.Contains(Text, StringComparison.Ordinal);
            case SidebarTokenRuleKind.StartsWith:
                return IgnoreCase ? AsciiIgnoreCaseStartsWith(value, Text) : value.StartsWith(Text, StringComparison.Ordinal);
            case SidebarTokenRuleKind.GreaterThan:
            case SidebarTokenRuleKind.LessThan:
                if (!attempted)
                {
                    attempted = true;
                    parsed = TryParseFiniteNumber(value);
                }

                if (parsed is not double number)
                    return false;
                return Kind is SidebarTokenRuleKind.GreaterThan
                    ? number > Threshold
                    : number < Threshold;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>#RGB</c> or <c>#RRGGBB</c>. Named colours fail closed.
    /// </summary>
    public static bool TryParseFg(string? text, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(text) || text[0] != '#')
            return false;
        var hex = text.AsSpan(1);
        if (hex.Length == 3
            && TryHexNibble(hex[0], out var r3)
            && TryHexNibble(hex[1], out var g3)
            && TryHexNibble(hex[2], out var b3))
        {
            color = ThemeColor.Rgb((byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17));
            return true;
        }

        if (hex.Length == 6
            && TryHexByte(hex[..2], out var r)
            && TryHexByte(hex[2..4], out var g)
            && TryHexByte(hex[4..6], out var b))
        {
            color = ThemeColor.Rgb(r, g, b);
            return true;
        }

        return false;
    }

    private static double? TryParseFiniteNumber(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == ',')
                return null;
        }

        if (!double.TryParse(
                value,
                System.Globalization.NumberStyles.AllowLeadingSign
                | System.Globalization.NumberStyles.AllowDecimalPoint
                | System.Globalization.NumberStyles.AllowExponent,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            || !double.IsFinite(number))
        {
            return null;
        }

        return number;
    }

    private static bool AsciiIgnoreCaseEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        return AsciiIgnoreCaseEquals(left.AsSpan(), right.AsSpan());
    }

    private static bool AsciiIgnoreCaseStartsWith(string value, string prefix)
    {
        if ((uint)prefix.Length > (uint)value.Length)
            return false;
        return AsciiIgnoreCaseEquals(value.AsSpan(0, prefix.Length), prefix.AsSpan());
    }

    private static bool AsciiIgnoreCaseContains(string value, string needle)
    {
        if (needle.Length == 0)
            return true;
        if (needle.Length > value.Length)
            return false;
        var end = value.Length - needle.Length;
        for (var i = 0; i <= end; i++)
        {
            if (AsciiIgnoreCaseEquals(value.AsSpan(i, needle.Length), needle.AsSpan()))
                return true;
        }

        return false;
    }

    private static bool AsciiIgnoreCaseEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (AsciiFold(left[i]) != AsciiFold(right[i]))
                return false;
        }

        return true;
    }

    private static char AsciiFold(char ch) =>
        ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch;

    private static bool TryHexNibble(char ch, out byte value)
    {
        value = 0;
        if (ch is >= '0' and <= '9')
        {
            value = (byte)(ch - '0');
            return true;
        }

        if (ch is >= 'a' and <= 'f')
        {
            value = (byte)(ch - 'a' + 10);
            return true;
        }

        if (ch is >= 'A' and <= 'F')
        {
            value = (byte)(ch - 'A' + 10);
            return true;
        }

        return false;
    }

    private static bool TryHexByte(ReadOnlySpan<char> text, out byte value)
    {
        value = 0;
        if (text.Length != 2
            || !TryHexNibble(text[0], out var hi)
            || !TryHexNibble(text[1], out var lo))
        {
            return false;
        }

        value = (byte)((hi << 4) | lo);
        return true;
    }
}

/// <summary>
/// One configured sidebar token. Plain names stay strings in TOML.
/// Inline tables add optional style and first-match rules.
/// </summary>
public sealed record SidebarTokenSpec
{
    public string Id { get; init; } = "";

    public SidebarTokenStyle Style { get; init; } = SidebarTokenStyle.Empty;

    public IReadOnlyList<SidebarTokenRule> Rules { get; init; } = [];

    public static implicit operator SidebarTokenSpec(string id) =>
        new() { Id = id ?? "" };

    public SidebarTokenStyle StyleForValue(string? value) =>
        SidebarTokenRule.FirstMatch(Rules, Style, value ?? "");
}
