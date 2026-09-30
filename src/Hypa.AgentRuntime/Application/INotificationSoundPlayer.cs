namespace Hypa.AgentRuntime.Application;

/// <summary>Plays a notification sound. Missing files are silent.</summary>
public interface INotificationSoundPlayer
{
    Task PlayAsync(string kind, string? path, CancellationToken ct = default);
}
