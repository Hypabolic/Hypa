namespace Hypa.AgentRuntime.Application;

/// <summary>Host desktop notification. Missing helpers are silent.</summary>
public interface ISystemNotifier
{
    Task ShowAsync(string title, string? body, CancellationToken ct = default);
}
