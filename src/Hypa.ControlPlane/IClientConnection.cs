using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// Per-client NDJSON connection with a single writer gate for line-atomic responses and pushes.
/// </summary>
public interface IClientConnection : IEventPushSink
{
    /// <summary>
    /// Serialize a complete NDJSON line under the per-connection writer gate.
    /// </summary>
    new Task WriteLineAsync(string jsonLine, CancellationToken ct = default);
}
