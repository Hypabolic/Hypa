namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Strategy port for history-block compression. The in-box adapter is
/// Brotli. The port allows a later codec swap without a store rewrite.
/// </summary>
public interface IPaneHistoryCodec
{
    PaneHistoryResult<byte[]> Compress(ReadOnlySpan<VtFrameCell> cells);

    PaneHistoryResult<VtFrameCell[]> Decompress(ReadOnlySpan<byte> compressed, int cellCount);
}
