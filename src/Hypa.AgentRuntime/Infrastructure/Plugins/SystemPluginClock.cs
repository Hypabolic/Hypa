using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Infrastructure.Plugins;

public sealed class SystemPluginClock : IPluginClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
