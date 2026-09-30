namespace Hypa.Cli.Attach;

/// <summary>
// / Host stdin framer for attach theme replies.
/// <c>src/raw_input.rs:125-167</c> <c>RawInputByteFramer</c>,
/// <c>host_color_query_sent</c>, and <c>discard_until</c>.
/// </summary>
internal sealed class AttachHostInputFramer
{
    /// <summary>
    // / OSC 10, OSC 11, then OSC 4 indices 0–255.
    /// <c>src/raw_input.rs:139</c> <c>HOST_COLOR_QUERY_REPLIES</c>.
    /// </summary>
    public const int HostColorQueryReplies = 258;

    /// <summary>
    /// <c>MAX_DISCARDED_CONTROL_TAIL_BYTES</c>.
    /// </summary>
    public const int MaxDiscardedOscTailBytes = 128;

    public List<byte> Pending { get; } = new(8);

    public bool DiscardUntilOscTerminator { get; set; }

    public int DiscardedTailBytes { get; set; }

    public int HostColorRepliesAwaited { get; set; }

    public bool HeldPendingHostReplyEsc { get; set; }

    public bool AwaitingHostColorReplies => HostColorRepliesAwaited > 0;

    public void NoteHostColorQuerySent()
    {
        HostColorRepliesAwaited = HostColorQueryReplies;
        HeldPendingHostReplyEsc = false;
    }

    public void NoteHostColorReplies(int count)
    {
        if (count <= 0 || HostColorRepliesAwaited <= 0)
            return;
        HostColorRepliesAwaited = Math.Max(0, HostColorRepliesAwaited - count);
    }

    public void BeginAttempt()
    {
        Pending.Clear();
        DiscardUntilOscTerminator = false;
        DiscardedTailBytes = 0;
        HeldPendingHostReplyEsc = false;
    }
}
