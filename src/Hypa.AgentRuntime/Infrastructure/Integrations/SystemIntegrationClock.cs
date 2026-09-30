using Hypa.AgentRuntime.Application.Integrations;

namespace Hypa.AgentRuntime.Infrastructure.Integrations;

public sealed class SystemIntegrationClock : IIntegrationClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
