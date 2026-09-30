namespace Hypa.Cli.Attach.Mouse;

/// <summary>Host mouse tracking sequences. Always disabled on restore.</summary>
public static class MouseCapture
{
    public const string Enable1000 = "\u001b[?1000h";
    public const string Enable1002 = "\u001b[?1002h";
    public const string Enable1003 = "\u001b[?1003h";
    public const string Enable1006 = "\u001b[?1006h";
    public const string Disable1000 = "\u001b[?1000l";
    public const string Disable1002 = "\u001b[?1002l";
    public const string Disable1003 = "\u001b[?1003l";
    public const string Disable1005 = "\u001b[?1005l";
    public const string Disable1006 = "\u001b[?1006l";
    public const string Disable1015 = "\u001b[?1015l";
    public const string Disable1016 = "\u001b[?1016l";

    // 1003+1006: any-event tracking including hover. 1003 supersedes 1002.
    public const string EnableSequence = Enable1003 + Enable1006;

    public const string DisableSequence =
        Disable1006 + Disable1016 + Disable1015 + Disable1005
        + Disable1003 + Disable1002 + Disable1000;

    public static string EnableIf(bool capture) => capture ? EnableSequence : "";
}
