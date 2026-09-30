namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>Clock port for official integration install. Tests inject a fixed time.</summary>
public interface IIntegrationClock
{
    DateTimeOffset UtcNow { get; }
}
