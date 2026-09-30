namespace Hypa.Cli.Attach;

/// <summary>
/// Client 1049h overlay. Strip pane 1049h/l from live bytes. Restore writes
/// the only 1049l. Track pane alt only for snapshot paint.
/// Carry incomplete <c>ESC[?1049</c> prefixes across render events so a
/// chunker split cannot leak pane alt-screen control to the host.
/// </summary>
public static class ClientOverlay
{
    public static ReadOnlySpan<byte> Enter1049Bytes => "\u001b[?1049h"u8;

    public static ReadOnlySpan<byte> Leave1049Bytes => "\u001b[?1049l"u8;

    /// <summary>
    /// Overlay-filter / alt-screen plan. <see cref="Bytes"/> is not a host
    // / payload.
    /// Frame. Attach must compose a host-size cell frame, then encode once.
    /// </summary>
    public readonly record struct LivePaintPlan(
        byte[] Bytes,
        bool NowAlt,
        bool LeftPaneAlt,
        bool RequestSnapshot);

    public static bool ShouldWriteRestoreSequence(bool overlayActive) => overlayActive;

    public static bool NoteTransition(ReadOnlySpan<byte> bytes, bool wasAlt, out bool nowAlt)
    {
        nowAlt = wasAlt;
        var enter = Enter1049Bytes;
        var leave = Leave1049Bytes;
        var rest = bytes;
        while (!rest.IsEmpty)
        {
            var enterAt = rest.IndexOf(enter);
            var leaveAt = rest.IndexOf(leave);
            if (enterAt < 0 && leaveAt < 0)
                break;

            if (enterAt >= 0 && (leaveAt < 0 || enterAt < leaveAt))
            {
                nowAlt = true;
                rest = rest[(enterAt + enter.Length)..];
            }
            else
            {
                nowAlt = false;
                rest = rest[(leaveAt + leave.Length)..];
            }
        }

        return wasAlt && !nowAlt;
    }

    public static byte[] FilterLiveBytes(ReadOnlySpan<byte> bytes, bool overlayActive)
    {
        if (bytes.IsEmpty || !overlayActive)
            return [];

        return RemoveAll1049(bytes);
    }

    public static LivePaintPlan PlanLivePaint(
        bool overlayActive,
        bool wasAlt,
        ReadOnlySpan<byte> bytes,
        ClientOverlayCarry? carry = null)
    {
        var combined = Combine(carry, bytes);
        var left = NoteTransition(combined, wasAlt, out var nowAlt);
        var entered = !wasAlt && nowAlt;

        byte[] output;
        if (overlayActive)
        {
            var filtered = RemoveAll1049(combined);
            output = HoldSuffixPrefix(filtered, carry);
        }
        else
        {
            _ = HoldSuffixPrefix(combined, carry);
            // Overlay-inactive live bytes are pane-relative ANSI. Do not
            // return them as a host payload.
            output = [];
        }

        return new LivePaintPlan(
            output,
            nowAlt,
            left,
            RequestSnapshot: overlayActive && (left || entered));
    }

    private static byte[] Combine(ClientOverlayCarry? carry, ReadOnlySpan<byte> bytes)
    {
        if (carry is null || carry.Length == 0)
            return bytes.ToArray();
        if (bytes.IsEmpty)
            return carry.Span.ToArray();

        var dest = new byte[carry.Length + bytes.Length];
        carry.Span.CopyTo(dest);
        bytes.CopyTo(dest.AsSpan(carry.Length));
        return dest;
    }

    private static byte[] HoldSuffixPrefix(byte[] data, ClientOverlayCarry? carry)
    {
        if (carry is null)
            return data;

        var hold = SuffixPrefixLength(data);
        if (hold <= 0)
        {
            carry.Clear();
            return data;
        }

        carry.Replace(data.AsSpan(data.Length - hold));
        return hold == data.Length ? [] : data.AsSpan(0, data.Length - hold).ToArray();
    }

    /// <summary>
    /// Longest suffix that is a proper prefix of 1049h/l. Full sequences are
    /// already removed (overlay) or already written (host).
    /// </summary>
    internal static int SuffixPrefixLength(ReadOnlySpan<byte> data)
    {
        var enter = Enter1049Bytes;
        var max = Math.Min(data.Length, enter.Length - 1);
        for (var n = max; n >= 1; n--)
        {
            if (enter.StartsWith(data[^n..]))
                return n;
        }

        return 0;
    }

    public static string StripLeading1049(string painted)
    {
        if (string.IsNullOrEmpty(painted))
            return painted;
        if (painted.StartsWith(SnapshotPainter.LeaveAltScreen, StringComparison.Ordinal))
            return painted[SnapshotPainter.LeaveAltScreen.Length..];
        if (painted.StartsWith(SnapshotPainter.EnterAltScreen, StringComparison.Ordinal))
            return painted[SnapshotPainter.EnterAltScreen.Length..];
        return painted;
    }

    private static byte[] RemoveAll1049(ReadOnlySpan<byte> source)
    {
        var enter = Enter1049Bytes;
        var leave = Leave1049Bytes;
        if (source.IndexOf(enter) < 0 && source.IndexOf(leave) < 0)
            return source.ToArray();

        var dest = new byte[source.Length];
        var written = 0;
        var rest = source;
        while (!rest.IsEmpty)
        {
            var enterAt = rest.IndexOf(enter);
            var leaveAt = rest.IndexOf(leave);
            if (enterAt < 0 && leaveAt < 0)
            {
                rest.CopyTo(dest.AsSpan(written));
                written += rest.Length;
                break;
            }

            int skipAt;
            int skipLen;
            if (enterAt >= 0 && (leaveAt < 0 || enterAt < leaveAt))
            {
                skipAt = enterAt;
                skipLen = enter.Length;
            }
            else
            {
                skipAt = leaveAt;
                skipLen = leave.Length;
            }

            if (skipAt > 0)
            {
                rest[..skipAt].CopyTo(dest.AsSpan(written));
                written += skipAt;
            }

            rest = rest[(skipAt + skipLen)..];
        }

        return written == dest.Length ? dest : dest.AsSpan(0, written).ToArray();
    }
}

/// <summary>
/// Incomplete <c>ESC[?1049</c> prefix held across <c>terminal.render</c> events.
/// </summary>
public sealed class ClientOverlayCarry
{
    private byte[] _pending = [];

    public int Length => _pending.Length;

    public ReadOnlySpan<byte> Span => _pending;

    public void Clear() => _pending = [];

    internal void Replace(ReadOnlySpan<byte> bytes) =>
        _pending = bytes.IsEmpty ? [] : bytes.ToArray();
}
