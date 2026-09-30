namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional port so <c>server.stop</c> can stop the generic host after the RPC result.
/// </summary>
public interface IRuntimeHostStop
{
    void RequestStop();
}
