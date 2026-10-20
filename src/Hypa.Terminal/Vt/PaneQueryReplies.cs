using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Terminal.Vt;

/// <summary>
/// Rules for the replies that the pane VT writes back to the program.
/// The functions are pure and keep no state.
/// </summary>
internal static class PaneQueryReplies
{
    private const byte Esc = 0x1b;
    private const byte Bel = 0x07;

    /// <summary>
    /// Remove each reply at or after <paramref name="start"/> that the pane
    /// must not send. Today that is the kitty keyboard flags reply
    /// (<c>CSI ? flags u</c>). The attach sends legacy key bytes to panes, so a
    /// pane must not say that it has kitty keyboard mode.
    /// </summary>
    public static void RemoveUnsupported(List<byte[]> replies, int start)
    {
        ArgumentNullException.ThrowIfNull(replies);
        for (var i = replies.Count - 1; i >= Math.Max(start, 0); i--)
        {
            if (IsKittyKeyboardFlagsReply(replies[i]))
                replies.RemoveAt(i);
        }
    }

    /// <summary>
    /// Remove the last VT reply at or after <paramref name="start"/> to an
    /// OSC 10 or OSC 11 query on <paramref name="channel"/>. Hypa sends the
    /// host theme reply in its place. Returns true when a reply was removed.
    /// </summary>
    public static bool RemoveLastDefaultColorReply(
        List<byte[]> replies,
        int start,
        HostDefaultColorKind channel)
    {
        ArgumentNullException.ThrowIfNull(replies);
        for (var i = replies.Count - 1; i >= Math.Max(start, 0); i--)
        {
            if (IsDefaultColorReply(replies[i], channel))
            {
                replies.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary><c>CSI ? digits u</c>.</summary>
    public static bool IsKittyKeyboardFlagsReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4 || reply[0] != Esc || reply[1] != (byte)'[' || reply[2] != (byte)'?'
            || reply[^1] != (byte)'u')
        {
            return false;
        }

        var digits = reply[3..^1];
        if (digits.IsEmpty)
            return false;
        foreach (var b in digits)
        {
            if (b is < (byte)'0' or > (byte)'9')
                return false;
        }

        return true;
    }

    /// <summary><c>OSC 10 ; rgb:...</c> or <c>OSC 11 ; rgb:...</c>, ended by BEL or ST.</summary>
    public static bool IsDefaultColorReply(ReadOnlySpan<byte> reply, HostDefaultColorKind channel)
    {
        ReadOnlySpan<byte> prefix = channel is HostDefaultColorKind.Foreground
            ? "\u001b]10;rgb:"u8
            : "\u001b]11;rgb:"u8;
        if (!reply.StartsWith(prefix))
            return false;
        return reply[^1] == Bel
            || (reply.Length >= prefix.Length + 2 && reply[^2] == Esc && reply[^1] == (byte)'\\');
    }
}
