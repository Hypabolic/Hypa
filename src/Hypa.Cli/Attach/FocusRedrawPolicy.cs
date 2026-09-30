namespace Hypa.Cli.Attach;

/// <summary>
/// hard-codes <c>ui.redraw_on_focus_gained=true</c>.
/// Tests may override via <c>HYPA_REDRAW_ON_FOCUS_GAINED</c> or the constructor.
/// </summary>
public sealed class FocusRedrawPolicy
{
    public const string EnvName = "HYPA_REDRAW_ON_FOCUS_GAINED";

    public FocusRedrawPolicy(bool? overrideValue = null)
    {
        RedrawOnFocusGained = overrideValue ?? ReadEnvDefault();
    }

    public bool RedrawOnFocusGained { get; }

    public int FocusInCount { get; private set; }

    public int RedrawRequests { get; private set; }

    public bool OnFocusIn()
    {
        FocusInCount++;
        if (!RedrawOnFocusGained)
            return false;
        RedrawRequests++;
        return true;
    }

    public static bool ReadEnvDefault()
    {
        var raw = Environment.GetEnvironmentVariable(EnvName);
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (raw is "0" or "false" or "False" or "no")
            return false;
        return true;
    }
}
