namespace Hypa.AgentRuntime.Application;

/// <summary>
/// How long a client waits for a mux socket after it starts the server.
/// </summary>
public static class MuxReadyBudget
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
}
