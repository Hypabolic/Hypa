namespace Hypa.Cli.Attach.ReleaseNotes;

public sealed class ReleaseNotesOverlayModel
{
    public const string FilterDismiss = "dismiss";
    public const string FilterScroll = "scroll";
    public const string TitlePrefix = "v";
    public const string Subtitle = "what's new in this release";
    public const string CloseLabel = " esc close ";
    public const string FooterScrollHint = "scroll";
    public const string FooterScrollKeys = "wheel ↑↓";
    public const string FooterCloseHint = "close";
    public const string FooterCloseKeys = "esc / enter";

    public const int TargetCols = 80;
    public const int TargetRows = 24;

    public bool IsOpen { get; private set; }

    public string Version { get; private set; } = "";

    public string Body { get; private set; } = "";

    public int Scroll { get; private set; }

    public ReleaseNotesLayout? Layout { get; set; }

    public IReadOnlyList<string> DisplayLines { get; private set; } = [];

    public bool Open(PackNotesDocument notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (string.IsNullOrWhiteSpace(notes.Body))
            return false;
        IsOpen = true;
        Version = notes.Version.Trim();
        Body = notes.Body;
        Scroll = 0;
        DisplayLines = ReleaseNotesMarkdownLines.Build(Body);
        Layout = ReleaseNotesPainter.Measure(this, TargetCols, TargetRows);
        return true;
    }

    public bool ScrollBy(int delta, int maxScroll)
    {
        if (!IsOpen || delta == 0)
            return false;
        var next = Math.Clamp(Scroll + delta, 0, Math.Max(0, maxScroll));
        if (next == Scroll)
            return false;
        Scroll = next;
        return true;
    }

    public bool SetScroll(int offset, int maxScroll)
    {
        if (!IsOpen)
            return false;
        var next = Math.Clamp(offset, 0, Math.Max(0, maxScroll));
        if (next == Scroll)
            return false;
        Scroll = next;
        return true;
    }

    public void Close()
    {
        IsOpen = false;
        Scroll = 0;
        Layout = null;
        DisplayLines = [];
        Version = "";
        Body = "";
    }
}
