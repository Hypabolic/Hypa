namespace Hypa.Cli.Attach;

/// <summary>
/// Kitty keyboard protocol sequences for the host terminal. The attach asks
/// for flag 1 (disambiguate escape codes). A bare Escape then comes as one
/// complete key report, so the attach does not wait for more bytes.
/// </summary>
public static class HostKeyboardMode
{
    /// <summary>Kitty flag 1: disambiguate escape codes.</summary>
    public const int DisambiguateFlags = 1;

    /// <summary>Ask the host for its kitty flags, then DA1. DA1 ends the probe.</summary>
    public const string ProbeSequence = "\u001b[?u\u001b[c";

    /// <summary>Push flag 1 on the host kitty keyboard stack.</summary>
    public const string PushSequence = "\u001b[>1u";

    /// <summary>Pop one entry from the host kitty keyboard stack.</summary>
    public const string PopSequence = "\u001b[<u";
}
