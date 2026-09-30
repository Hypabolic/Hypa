using System.Buffers;

namespace Hypa.AgentIntelligence;

/// <summary>
/// Byte-level pre-filter for <see cref="DefaultEventPayloadRedactor"/>.
/// A chunk with no confirmed anchor and no hold tail cannot match the rule pack.
/// </summary>
internal static class TerminalSecretAnchors
{
    internal const int MaxAnchorCandidates = 512;
    internal const int TailWindowBytes = 32;

    /// <summary>
    /// First bytes of every derived rule anchor. Does not include <c>=</c> or
    /// <c>"</c>: those bytes appear in almost every build line and no rule
    /// fires on them alone.
    /// </summary>
    private static readonly SearchValues<byte> LeadBytes = SearchValues.Create("-_AaBbgPpSsTt"u8);

    // ASCII bitmap of characters that can end a prefix-fragment hold.
    // Letters from ExactPrefixTokens, IgnoreCasePrefixTokens, Bearer, and
    // IncompleteKeyPrefixAtEnd, both cases, plus '-', '_', and space.
    // Does not hold digits, '.', '/', ':', ',', ';', '=', '"', or whitespace
    // other than space. Does not hold f/j/m/q/v in either case.
    private static readonly ulong TailLo;
    private static readonly ulong TailHi;

    static TerminalSecretAnchors()
    {
        ulong lo = 0;
        ulong hi = 0;
        Set(ref lo, ref hi, (byte)'-');
        Set(ref lo, ref hi, (byte)'_');
        Set(ref lo, ref hi, (byte)' ');
        foreach (var ch in "ABCDEGHIKLNOPRSTUWXYZ"u8)
        {
            Set(ref lo, ref hi, ch);
            Set(ref lo, ref hi, (byte)(ch + 32));
        }

        TailLo = lo;
        TailHi = hi;
    }

    internal static bool ContainsLeadByte(ReadOnlySpan<byte> span) =>
        span.IndexOfAny(LeadBytes) >= 0;

    internal static bool ContainsAnchor(ReadOnlySpan<byte> span)
    {
        var offset = 0;
        var inspected = 0;
        while (offset < span.Length)
        {
            var rel = span[offset..].IndexOfAny(LeadBytes);
            if (rel < 0)
                return false;

            inspected++;
            if (inspected > MaxAnchorCandidates)
                return true;

            var at = offset + rel;
            if (AnchorStartsAt(span, at))
                return true;

            offset = at + 1;
        }

        return false;
    }

    /// <summary>
    /// True when the chunk may start a carry hold even with no full anchor.
    /// Covers an incomplete UTF-8 tail and a trailing prefix fragment.
    /// </summary>
    internal static bool MayStartHold(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return false;
        if (span[^1] >= 0x80)
            return true;
        if (!LastByteMayEndFragment(span[^1]))
            return false;

        // A trailing '_' can close IncompleteKeyPrefixAtEnd on an identifier
        // longer than the tail window. Take the slow path.
        if (span[^1] == (byte)'_')
            return true;

        var n = Math.Min(TailWindowBytes, span.Length);
        Span<char> tail = stackalloc char[TailWindowBytes];
        var start = span.Length - n;
        for (var i = 0; i < n; i++)
            tail[i] = (char)span[start + i];

        return DefaultEventPayloadRedactor.TailWindowStartsHold(tail[..n]);
    }

    private static bool AnchorStartsAt(ReadOnlySpan<byte> span, int i)
    {
        var b = span[i];
        switch (b)
        {
            case (byte)'-':
                return StartsWith(span, i, "-----BEGIN"u8);
            case (byte)'_':
                return StartsWithIgnoreCase(span, i, "_key"u8);
            case (byte)'A':
                return StartsWith(span, i, "AKIA"u8)
                    || StartsWithIgnoreCase(span, i, "authorization"u8);
            case (byte)'a':
                return StartsWithIgnoreCase(span, i, "authorization"u8);
            case (byte)'B':
            case (byte)'b':
                return StartsWithIgnoreCase(span, i, "bearer"u8);
            case (byte)'g':
                return IsGhTokenPrefix(span, i);
            case (byte)'P':
            case (byte)'p':
                return StartsWithIgnoreCase(span, i, "password"u8)
                    || StartsWithIgnoreCase(span, i, "passwd"u8)
                    || StartsWithIgnoreCase(span, i, "pwd"u8);
            case (byte)'S':
                return StartsWithIgnoreCase(span, i, "secret"u8);
            case (byte)'s':
                return StartsWith(span, i, "sk-"u8)
                    || StartsWithIgnoreCase(span, i, "secret"u8);
            case (byte)'T':
            case (byte)'t':
                return StartsWithIgnoreCase(span, i, "token"u8);
            default:
                return false;
        }
    }

    private static bool IsGhTokenPrefix(ReadOnlySpan<byte> span, int i)
    {
        if (i + 4 > span.Length)
            return false;
        if (span[i] != (byte)'g' || span[i + 1] != (byte)'h')
            return false;
        var third = span[i + 2];
        if (third is not ((byte)'p' or (byte)'o' or (byte)'u' or (byte)'s' or (byte)'r'))
            return false;
        return span[i + 3] == (byte)'_';
    }

    private static bool StartsWith(ReadOnlySpan<byte> span, int i, ReadOnlySpan<byte> exact) =>
        span[i..].StartsWith(exact);

    private static bool StartsWithIgnoreCase(
        ReadOnlySpan<byte> span, int i, ReadOnlySpan<byte> lowerAscii)
    {
        if (i + lowerAscii.Length > span.Length)
            return false;
        for (var n = 0; n < lowerAscii.Length; n++)
        {
            var b = span[i + n];
            if ((byte)(b - (byte)'A') <= 25)
                b += 32;
            if (b != lowerAscii[n])
                return false;
        }

        return true;
    }

    private static bool LastByteMayEndFragment(byte b)
    {
        if (b < 64)
            return (TailLo & (1UL << b)) != 0;
        if (b < 128)
            return (TailHi & (1UL << (b - 64))) != 0;
        return false;
    }

    private static void Set(ref ulong lo, ref ulong hi, byte b)
    {
        if (b < 64)
            lo |= 1UL << b;
        else if (b < 128)
            hi |= 1UL << (b - 64);
    }
}
