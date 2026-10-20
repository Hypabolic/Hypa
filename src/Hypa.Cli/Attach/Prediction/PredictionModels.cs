namespace Hypa.Cli.Attach.Prediction;

/// <summary>A cursor position in pane cells.</summary>
internal readonly record struct PredictedCursor(int Col, int Row);

/// <summary>
/// One predicted cell to stamp over the pane frame. <see cref="Cell"/> has
/// the expected style. It has no marker (ADR 0018, decision 2).
/// </summary>
internal readonly record struct PredictedCell(int Col, int Row, AssembledCell Cell);

/// <summary>
/// What the client shows of its predictions in one compose: the predicted
/// cells and the predicted cursor of one pane. Coordinates are pane cells.
/// </summary>
internal sealed record PredictionView(
    string PaneId,
    IReadOnlyList<PredictedCell> Cells,
    PredictedCursor? Cursor,
    IReadOnlyList<long> Shown)
{
    /// <summary>The pane rows that have a predicted cell.</summary>
    public IEnumerable<int> Rows => Cells.Select(cell => cell.Row).Distinct();
}

/// <summary>Why the predictor deleted its epochs.</summary>
internal enum PredictionResetReason
{
    FocusChange,
    AlternateScreen,
    SynchronizedOutput,
    Paste,
    Resize,
    Reanchor,
    OccupantChange,
    NotAllowed,
    NoAck,
    InputDropped,
    NoSeq,

    /// <summary>The detected agent of the pane ended: the alternate screen no longer allows prediction.</summary>
    AgentEnded,

    /// <summary>
    /// The pane PTY has echo off in canonical mode: a password prompt
    /// (getpass, <c>read -s</c>, sudo, ssh).
    /// </summary>
    EchoOff,
}

/// <summary>Counters of one predictor.</summary>
internal sealed record EchoPredictorStats
{
    /// <summary>Predictions that a host paint showed: glyph cells and cursor moves.</summary>
    public long PredictedShown { get; init; }

    /// <summary>
    /// Shown predictions that a mismatch frame contradicted, and shown
    /// predictions of an epoch that an expiry deleted.
    /// </summary>
    public long MispredictedShown { get; init; }

    public long ConfirmedByGlyph { get; init; }

    public long ConfirmedByCursor { get; init; }

    /// <summary>Predictions that a later unpredicted key made impossible to judge. They count as neither.</summary>
    public long Retired { get; init; }

    public long Mismatches { get; init; }

    /// <summary>Epochs deleted because a prediction got no ack in time.</summary>
    public long Expired { get; init; }

    public long Resets { get; init; }

    /// <summary>Predictions that a typed key made.</summary>
    public long Made { get; init; }

    /// <summary>Keys with no prediction: an unknown key, or the prediction limit.</summary>
    public long Unpredicted { get; init; }

    /// <summary>Keys that waited for the ack of an earlier unpredicted key.</summary>
    public long BarrierSkips { get; init; }

    /// <summary>Calls of each reset, also when no epoch was there to delete.</summary>
    public IReadOnlyDictionary<PredictionResetReason, long> ResetCalls { get; init; } =
        new Dictionary<PredictionResetReason, long>();

    /// <summary>Predictions that wait for an ack or a confirm now.</summary>
    public int Pending { get; init; }

    public TimeSpan? Srtt { get; init; }
}

/// <summary>The kind of one typed key for the predictor.</summary>
internal enum EchoKeyKind
{
    Printable,
    Erase,
    Right,
    Left,
    Other,
}

/// <summary>One key of a <c>SendPaneBytes</c> payload.</summary>
internal readonly record struct EchoKey(EchoKeyKind Kind, byte Glyph = 0);

/// <summary>
/// Splits pane input bytes into keys. An escape sequence, a UTF-8 sequence
/// and each other byte is one key, as <c>AttachLatencyMeter.CountKeys</c>
/// counts them.
/// </summary>
internal static class EchoKeyReader
{
    public static List<EchoKey> Read(ReadOnlySpan<byte> bytes, bool applicationCursor)
    {
        var keys = new List<EchoKey>(bytes.Length);
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i++];
            if (b is >= 0x20 and <= 0x7e)
            {
                keys.Add(new EchoKey(EchoKeyKind.Printable, b));
                continue;
            }

            if (b is 0x7f or 0x08)
            {
                keys.Add(new EchoKey(EchoKeyKind.Erase));
                continue;
            }

            if (b == 0x1b)
            {
                keys.Add(ReadEscape(bytes, ref i, applicationCursor));
                continue;
            }

            if (b >= 0xc0)
            {
                while (i < bytes.Length && (bytes[i] & 0xc0) == 0x80)
                    i++;
            }

            keys.Add(new EchoKey(EchoKeyKind.Other));
        }

        return keys;
    }

    private static EchoKey ReadEscape(ReadOnlySpan<byte> bytes, ref int i, bool applicationCursor)
    {
        if (i >= bytes.Length)
            return new EchoKey(EchoKeyKind.Other);
        var next = bytes[i++];
        if (next == (byte)'[')
        {
            var paramStart = i;
            while (i < bytes.Length && bytes[i] is >= 0x20 and <= 0x3f)
                i++;
            if (i >= bytes.Length)
                return new EchoKey(EchoKeyKind.Other);
            var final = bytes[i++];
            // Only a bare CSI C or CSI D moves the cursor one cell. A
            // parameter (a count or a modifier) is another key.
            if (i - 1 != paramStart || applicationCursor)
                return new EchoKey(EchoKeyKind.Other);
            return final switch
            {
                (byte)'C' => new EchoKey(EchoKeyKind.Right),
                (byte)'D' => new EchoKey(EchoKeyKind.Left),
                _ => new EchoKey(EchoKeyKind.Other),
            };
        }

        if (next == (byte)'O' && i < bytes.Length)
        {
            var final = bytes[i++];
            if (!applicationCursor)
                return new EchoKey(EchoKeyKind.Other);
            return final switch
            {
                (byte)'C' => new EchoKey(EchoKeyKind.Right),
                (byte)'D' => new EchoKey(EchoKeyKind.Left),
                _ => new EchoKey(EchoKeyKind.Other),
            };
        }

        return new EchoKey(EchoKeyKind.Other);
    }
}
