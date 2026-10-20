namespace Hypa.Cli.Attach;

/// <summary>
/// Host stdin framer for attach theme replies.
/// <c>src/raw_input.rs:125-167</c> <c>RawInputByteFramer</c>,
/// <c>host_color_query_sent</c>, and <c>discard_until</c>.
/// </summary>
/// <remarks>
/// The reply window has a deadline. It stays open for
/// <see cref="HostReplyQuietWindow"/> after the query write or after the last
/// parsed reply, whichever is later. A host that does not answer every query
/// thus stops holding Escape and <c>ESC ]</c> after that time.
/// </remarks>
internal sealed class AttachHostInputFramer
{
    /// <summary>
    /// OSC 10, OSC 11, then OSC 4 indices 0–255.
    /// <c>src/raw_input.rs:139</c> <c>HOST_COLOR_QUERY_REPLIES</c>.
    /// </summary>
    public const int HostColorQueryReplies = 258;

    /// <summary>
    /// <c>MAX_DISCARDED_CONTROL_TAIL_BYTES</c>.
    /// </summary>
    public const int MaxDiscardedOscTailBytes = 128;

    /// <summary>
    /// How long after the query write, or after the last parsed reply, the
    /// framer still waits for host color replies.
    /// <c>src/raw_input.rs:236</c> <c>HOST_REPLY_QUIET_WINDOW</c>.
    /// </summary>
    public static readonly TimeSpan HostReplyQuietWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a half-received OSC 10 or OSC 11 reply can wait for its next
    /// read before the framer drops it.
    /// <c>src/raw_input.rs:242</c> <c>HOST_COLOR_REPLY_IDLE_LIMIT</c>.
    /// </summary>
    public static readonly TimeSpan HostColorReplyIdleLimit = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _utcNow;
    private int _repliesAwaited;
    private DateTimeOffset? _replyActivityAt;
    private DateTimeOffset? _partialColorReplyReadAt;

    public AttachHostInputFramer()
        : this(TimeProvider.System)
    {
    }

    public AttachHostInputFramer(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _utcNow = time.GetUtcNow;
    }

    /// <summary>
    /// The attach live state passes its own clock. The state can replace its
    /// <see cref="TimeProvider"/> after this framer exists.
    /// </summary>
    internal AttachHostInputFramer(Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        _utcNow = utcNow;
    }

    public List<byte> Pending { get; } = new(8);

    public bool DiscardUntilOscTerminator { get; set; }

    public int DiscardedTailBytes { get; set; }

    /// <summary>
    /// The color replies that the framer still waits for. It is 0 when the
    /// quiet window is closed, also before the next idle flush clears it.
    /// </summary>
    public int HostColorRepliesAwaited => QuietWindowOpen() ? _repliesAwaited : 0;

    public bool HeldPendingHostReplyEsc { get; set; }

    public bool AwaitingHostColorReplies => HostColorRepliesAwaited > 0;

    /// <summary>
    /// The host answered <c>CSI ? u</c> with its kitty keyboard flags.
    /// </summary>
    public bool KittyKeyboardReplied { get; private set; }

    /// <summary>The host answered DA1 (<c>CSI ? ... c</c>). The probe is over.</summary>
    public bool PrimaryDeviceAttributesReplied { get; private set; }

    private bool _kittyReplyTaken;

    /// <summary>
    /// Record a <c>CSI ? flags u</c> reply. A reply after DA1 is not an
    /// answer to the probe, so it does not count.
    /// </summary>
    internal void NoteKittyKeyboardReply(int flags)
    {
        if (PrimaryDeviceAttributesReplied)
            return;
        KittyKeyboardReplied = true;
        HostKittyKeyboardFlags = flags;
    }

    /// <summary>The flags in the host reply, before the attach pushes its own.</summary>
    public int? HostKittyKeyboardFlags { get; private set; }

    internal void NotePrimaryDeviceAttributesReply() => PrimaryDeviceAttributesReplied = true;

    private bool _keyboardProbeSent;

    /// <summary>The attach wrote the kitty keyboard probe.</summary>
    public void NoteKeyboardProbeSent() => _keyboardProbeSent = true;

    /// <summary>
    /// The probe is written and DA1 has not come back. A reply can still split
    /// at its ESC, so a lone ESC keeps the full wait.
    /// </summary>
    public bool KeyboardProbeOutstanding => _keyboardProbeSent && !PrimaryDeviceAttributesReplied;

    /// <summary>
    /// True once, after the host answered the kitty keyboard query. The
    /// attach then pushes its flags. A host that answers only DA1 stays in
    /// legacy mode, and no timeout is necessary for that case.
    /// </summary>
    public bool TakeKittyKeyboardReply()
    {
        if (!KittyKeyboardReplied || _kittyReplyTaken)
            return false;
        _kittyReplyTaken = true;
        return true;
    }

    public void NoteHostColorQuerySent()
    {
        _repliesAwaited = HostColorQueryReplies;
        HeldPendingHostReplyEsc = false;
        _replyActivityAt = _utcNow();
    }

    public void NoteHostColorReplies(int count)
    {
        if (count <= 0 || _repliesAwaited <= 0)
            return;
        _repliesAwaited = Math.Max(0, _repliesAwaited - count);
        // Each reply starts the quiet window again, so a long burst of
        // palette replies stays protected.
        _replyActivityAt = _utcNow();
    }

    /// <summary>
    /// Stop waiting for replies that the host did not send, when the quiet
    /// window is closed. <c>src/raw_input.rs:370-384</c>.
    /// </summary>
    public void ExpireUnansweredHostQueries()
    {
        if (_replyActivityAt is null || QuietWindowOpen())
            return;
        _repliesAwaited = 0;
        HeldPendingHostReplyEsc = false;
        _replyActivityAt = null;
    }

    public void BeginAttempt()
    {
        Pending.Clear();
        DiscardUntilOscTerminator = false;
        DiscardedTailBytes = 0;
        HeldPendingHostReplyEsc = false;
        _partialColorReplyReadAt = null;
        // The first attempt writes the query before this call. Thus only a
        // closed quiet window ends the wait here. A reset without that
        // condition would close the Ghostty reply window at once.
        ExpireUnansweredHostQueries();
    }

    /// <summary>
    /// Record the time of the read that left <paramref name="pending"/> as an
    /// unfinished OSC 10 or OSC 11 reply. Any other pending state clears it.
    /// </summary>
    internal void NotePendingAfterRead(List<byte> pending)
    {
        _partialColorReplyReadAt = IsIncompleteDefaultColorReply(pending)
            ? _utcNow()
            : null;
    }

    /// <summary>
    /// Drop an unfinished OSC 10 or OSC 11 reply that waited
    /// <see cref="HostColorReplyIdleLimit"/> or more for its next read, or that
    /// grew to <see cref="MaxDiscardedOscTailBytes"/> or more. Call it before a
    /// new read joins the pending bytes: that read is then input, not a reply
    /// tail. OSC 4 is out of scope, because Ghostty can batch OSC 4 replies.
    /// <c>src/raw_input.rs:321-334</c>.
    /// </summary>
    /// <returns>True when the pending bytes were dropped.</returns>
    internal bool DropAbandonedHostColorReply(List<byte> pending)
    {
        if (!IsIncompleteDefaultColorReply(pending))
        {
            _partialColorReplyReadAt = null;
            return false;
        }

        var abandoned = pending.Count >= MaxDiscardedOscTailBytes
            || _partialColorReplyReadAt is not { } at
            || _utcNow() - at >= HostColorReplyIdleLimit;
        if (!abandoned)
            return false;
        pending.Clear();
        _partialColorReplyReadAt = null;
        return true;
    }

    /// <summary>
    /// <paramref name="pending"/> starts with <c>ESC ]10;</c> or
    /// <c>ESC ]11;</c> and has no terminator yet.
    /// </summary>
    internal static bool IsIncompleteDefaultColorReply(List<byte> pending) =>
        pending.Count >= 5
        && pending[0] == 0x1b
        && pending[1] == (byte)']'
        && pending[2] == (byte)'1'
        && pending[3] is (byte)'0' or (byte)'1'
        && pending[4] == (byte)';';

    private bool QuietWindowOpen() =>
        _replyActivityAt is { } at && _utcNow() - at < HostReplyQuietWindow;
}
