namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Runtime-local agent presentation compressor (mux path).
/// Does not replace <c>Hypa.Runtime</c> <c>IOutputCompressor</c>; no dependency on that port.
/// Must not mutate terminal/VT state.
/// </summary>
public interface IAgentPresentationCompressor
{
    /// <summary>
    /// Compress raw pane text for agent context. Never throws for expected truncation/timeout;
    /// returns a bounded head+tail fallback instead.
    /// </summary>
    PresentationResult Compress(PresentationRequest request);
}
