namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Starts a detached custom-command child. No wait on the UI thread.
/// Unix adapter lives in Cli. Tests inject a fake.
/// </summary>
public interface IDetachedCommandLauncher
{
    bool TryStart(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? cwd,
        out string? error);
}
