namespace Hypa.Terminal.Vt.Osc;

/// <summary>One complete OSC string. The body holds the bytes between the introducer and the terminator.</summary>
public readonly ref struct OscSequence
{
    public OscSequence(int number, ReadOnlySpan<byte> body, int terminatorBytes, int endOffset)
    {
        Number = number;
        Body = body;
        TerminatorBytes = terminatorBytes;
        EndOffset = endOffset;
    }

    /// <summary>The OSC number. It is the decimal text before the first semicolon.</summary>
    public int Number { get; }

    /// <summary>The full body, with the number and the semicolon. The span is valid only during the call.</summary>
    public ReadOnlySpan<byte> Body { get; }

    /// <summary>1 for BEL, 2 for ESC backslash.</summary>
    public int TerminatorBytes { get; }

    /// <summary>The index in the current chunk after the last terminator byte.</summary>
    public int EndOffset { get; }
}

/// <summary>
/// Reads the body of one complete OSC string and adds results to the output of the chunk.
/// A handler keeps its own state. It never reads the byte stream itself.
/// </summary>
public interface IOscHandler
{
    void Handle(in OscSequence sequence, OscOutput output);
}

/// <summary>Sees the control sequences that reset the terminal. The tokenizer calls it in stream order.</summary>
public interface IControlSequenceObserver
{
    /// <summary>RIS, <c>ESC c</c>.</summary>
    void OnFullReset(int endOffset, OscOutput output);

    /// <summary>DECSTR, <c>CSI ! p</c>.</summary>
    void OnSoftReset(int endOffset, OscOutput output);
}
