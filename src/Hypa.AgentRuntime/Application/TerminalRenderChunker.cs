namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Strategy: split live <c>terminal.render</c> bytes on UTF-8 and 7-bit escape
/// boundaries. Never drops the tail. Each part is independently parseable so
/// live-queue drop/coalesce cannot cut a CSI / OSC / DCS sequence or a UTF-8
/// scalar. This mux is UTF-8: 8-bit C1 bytes (0x80-0x9F) are not introducers.
/// </summary>
public static class TerminalRenderChunker
{
    public const int DefaultMaxRawBytes = 64 * 1024;

    /// <summary>
    /// Largest single complete escape sequence that may exceed
    /// <see cref="DefaultMaxRawBytes"/> so the NDJSON line stays under 1 MiB.
    /// </summary>
    public const int MaxAtomicSequenceBytes = 750 * 1024;

    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> data, int maxRaw = DefaultMaxRawBytes)
    {
        if (maxRaw <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRaw));

        var parts = new List<byte[]>();
        if (data.IsEmpty)
            return parts;

        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data[offset..];
            var take = NextPartLength(remaining, maxRaw);
            if (take <= 0)
                take = remaining.Length;
            parts.Add(remaining[..take].ToArray());
            offset += take;
        }

        return parts;
    }

    internal static int NextPartLength(ReadOnlySpan<byte> data, int maxRaw)
    {
        if (data.IsEmpty)
            return 0;
        if (data.Length <= maxRaw)
            return data.Length;

        var cut = AlignUtf8(data, maxRaw);
        cut = AdjustForEscape(data, cut, maxRaw);
        // Escape adjustment must not undo AlignUtf8 by landing on a continuation.
        if (cut > 0 && cut < data.Length && IsUtf8Continuation(data[cut]))
            cut = AlignUtf8(data, cut);
        if (cut <= 0)
            cut = Math.Max(1, AlignUtf8(data, Math.Min(maxRaw, data.Length)));
        return Math.Min(cut, data.Length);
    }

    private static int AlignUtf8(ReadOnlySpan<byte> data, int max)
    {
        if (max >= data.Length)
            return data.Length;
        if (max <= 0)
            return 0;
        if (!IsUtf8Continuation(data[max]))
            return max;

        var cut = max;
        while (cut > 0 && IsUtf8Continuation(data[cut]))
            cut--;
        return cut;
    }

    private static bool IsUtf8Continuation(byte b) => (b & 0b1100_0000) == 0b1000_0000;

    private static int AdjustForEscape(ReadOnlySpan<byte> data, int cut, int maxRaw)
    {
        if (cut <= 0 || cut >= data.Length)
            return cut;

        if (!TryFindOpenEscape(data, cut, out var start, out var kind))
            return cut;

        var end = FindEscapeEnd(data, start, kind);
        if (end > 0)
        {
            if (start == 0)
            {
                if (end <= MaxAtomicSequenceBytes)
                    return end;
                return cut;
            }

            return start;
        }

        // Incomplete in this write.
        if (start > 0)
            return start;

        if (data.Length <= MaxAtomicSequenceBytes)
            return data.Length;

        return cut > 0 ? cut : Math.Min(maxRaw, data.Length);
    }

    private enum EscapeKind
    {
        Csi,
        Osc,
        StringTerminated,
        TwoByteEsc,
    }

    private static bool TryFindOpenEscape(
        ReadOnlySpan<byte> data, int cut, out int start, out EscapeKind kind)
    {
        start = 0;
        kind = EscapeKind.Csi;
        var i = 0;
        while (i < cut)
        {
            if (!TryReadIntroducer(data, i, out var introLen, out var introKind))
            {
                i++;
                continue;
            }

            var seqStart = i;
            var end = FindEscapeEnd(data, seqStart, introKind);
            if (end > 0)
            {
                if (end <= cut)
                {
                    i = end;
                    continue;
                }

                start = seqStart;
                kind = introKind;
                return true;
            }

            start = seqStart;
            kind = introKind;
            return true;
        }

        return false;
    }

    private static bool TryReadIntroducer(
        ReadOnlySpan<byte> data, int i, out int introLen, out EscapeKind kind)
    {
        introLen = 0;
        kind = EscapeKind.Csi;
        var b = data[i];
        if (b == 0x1B)
        {
            if (i + 1 >= data.Length)
            {
                introLen = 1;
                kind = EscapeKind.TwoByteEsc;
                return true;
            }

            var n = data[i + 1];
            if (n == (byte)'[')
            {
                introLen = 2;
                kind = EscapeKind.Csi;
                return true;
            }

            if (n == (byte)']')
            {
                introLen = 2;
                kind = EscapeKind.Osc;
                return true;
            }

            if (n is (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
            {
                introLen = 2;
                kind = EscapeKind.StringTerminated;
                return true;
            }

            introLen = 1;
            kind = EscapeKind.TwoByteEsc;
            return true;
        }

        // 8-bit C1 (0x80-0x9F) is a UTF-8 continuation in this mux. Do not
        // treat 0x90/0x98/0x9B/0x9D/0x9E/0x9F as introducers.
        return false;
    }

    private static int FindEscapeEnd(ReadOnlySpan<byte> data, int start, EscapeKind kind)
    {
        if (!TryReadIntroducer(data, start, out var introLen, out var resolved) || resolved != kind)
            return -1;

        var i = start + introLen;
        switch (kind)
        {
            case EscapeKind.Csi:
                while (i < data.Length && data[i] is >= 0x20 and <= 0x3F)
                    i++;
                if (i < data.Length && data[i] is >= 0x40 and <= 0x7E)
                    return i + 1;
                return -1;

            case EscapeKind.Osc:
                return FindOscEnd(data, i);

            case EscapeKind.StringTerminated:
                return FindStEnd(data, i);

            case EscapeKind.TwoByteEsc:
                if (start + 1 >= data.Length)
                    return -1;
                var n = data[start + 1];
                if (n is >= 0x20 and <= 0x2F)
                {
                    i = start + 2;
                    while (i < data.Length && data[i] is >= 0x20 and <= 0x2F)
                        i++;
                    if (i < data.Length && data[i] is >= 0x30 and <= 0x7E)
                        return i + 1;
                    return -1;
                }

                if (n is >= 0x30 and <= 0x7E)
                    return start + 2;
                return -1;

            default:
                return -1;
        }
    }

    private static int FindOscEnd(ReadOnlySpan<byte> data, int i)
    {
        while (i < data.Length)
        {
            if (data[i] == 0x07)
                return i + 1;
            if (data[i] == 0x1B && i + 1 < data.Length && data[i + 1] == (byte)'\\')
                return i + 2;
            i++;
        }

        return -1;
    }

    private static int FindStEnd(ReadOnlySpan<byte> data, int i)
    {
        while (i < data.Length)
        {
            if (data[i] == 0x1B && i + 1 < data.Length && data[i + 1] == (byte)'\\')
                return i + 2;
            i++;
        }

        return -1;
    }
}
