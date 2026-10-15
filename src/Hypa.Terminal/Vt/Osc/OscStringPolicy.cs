namespace Hypa.Terminal.Vt.Osc;

/// <summary>What the tokenizer does with an ESC byte that is not part of the string terminator.</summary>
public enum OscEscapeInBody
{
    /// <summary>The ESC ends the string without a result. The next byte starts a new escape sequence.</summary>
    AbortAndRestart,

    /// <summary>
    /// The ESC and the next byte stay in the body.
    /// The pair ESC BEL ends the string, and the ESC stays in the body.
    /// </summary>
    KeepAndEndOnBel,

    /// <summary>The ESC and the next byte stay in the body. BEL after ESC is body text.</summary>
    Keep,
}

/// <summary>
/// How the tokenizer reads the string of one OSC number.
/// A handler registers one policy for each number it owns.
/// </summary>
/// <param name="MaxBodyBytes">
/// The body is the bytes between the introducer and the terminator.
/// A longer body gives no result. The tokenizer then skips to the end of the string.
/// </param>
/// <param name="EscapeInBody">The rule for an ESC inside the body.</param>
/// <param name="CancelAborts">CAN and SUB end the string without a result when this is true.</param>
public readonly record struct OscStringPolicy(
    int MaxBodyBytes,
    OscEscapeInBody EscapeInBody,
    bool CancelAborts);
