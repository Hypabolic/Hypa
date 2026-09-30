namespace Hypa.Cli.Attach.Onboarding;

public sealed class OnboardingOverlayModel
{
    public const string FilterComplete = "complete";
    public const string Title = "hypa";
    public const string Tagline = "terminal workspace manager for coding agents";
    public const string MouseLine1 = "this is a mouse-first terminal.";
    public const string MouseLine2 = "click the sidebar to switch workspaces, drag pane";
    public const string MouseLine3 = "borders to resize, right-click for context menus.";
    public const string PrefixHint = " enters prefix mode · ";
    public const string HelpHint = " shows keybinds and settings";
    public const string NextLine = "next: optional agent integrations.";
    public const string NextLine2 = "nothing is installed until you confirm.";
    public const string ContinueLabel = "↵ continue";
    public const string DefaultPrefixLabel = "ctrl+b";

    public bool IsOpen { get; private set; }

    public bool PendingComplete { get; private set; }

    public bool PrefixArmed { get; private set; }

    public string PrefixLabel { get; private set; } = DefaultPrefixLabel;

    public OnboardingLayout? Layout { get; set; }

    public void Open(string? prefixLabel = null)
    {
        if (IsOpen)
            return;
        IsOpen = true;
        PendingComplete = false;
        PrefixArmed = false;
        PrefixLabel = string.IsNullOrWhiteSpace(prefixLabel)
            ? DefaultPrefixLabel
            : prefixLabel.Trim();
        Layout = OnboardingPainter.Measure(this, 80, 24);
    }

    public void RequestComplete()
    {
        if (!IsOpen)
            return;
        PendingComplete = true;
        PrefixArmed = false;
    }

    public void ClearPendingComplete() => PendingComplete = false;

    public void ArmPrefix()
    {
        if (!IsOpen)
            return;
        PrefixArmed = true;
    }

    public void ClearPrefixArmed() => PrefixArmed = false;

    public void Close()
    {
        IsOpen = false;
        PendingComplete = false;
        PrefixArmed = false;
        Layout = null;
    }
}
