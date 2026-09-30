namespace Hypa.Cli.Attach.Splash;

/// <summary>
/// Owns splash lifetime. Mux attach runs in parallel. Skip closes at once.
/// The splash waits for the logo effect to finish.
/// </summary>
internal sealed class StartupSplashSession
{
    public static readonly TimeSpan MinVisible = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);

    public bool Skipped { get; private set; }
    public bool MuxReady { get; private set; }

    public void Skip() => Skipped = true;

    public void NoteMuxReady() => MuxReady = true;

    public bool ShouldDismiss(TimeSpan elapsed, bool overlayFinished)
    {
        if (Skipped)
            return true;
        if (elapsed >= Failsafe)
            return true;
        if (!overlayFinished)
            return false;
        if (!MuxReady)
            return false;
        return elapsed >= MinVisible;
    }
}
