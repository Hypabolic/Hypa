using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One packed cells payload and the UTF-8 of that payload.
/// Full is true for a reanchor pack. Full is false for a delta pack.
/// encodes one message one time.
/// </summary>
public readonly record struct LiveCellsEncoding(
    TerminalRenderCellsPayload Payload,
    byte[] PayloadUtf8);

/// <summary>
/// Packs and serialises one live cells payload.
/// encodes above the client loop, then sends the same bytes to every client.
/// frame equals the client baseline.
/// </summary>
public interface ILiveCellsEncoder
{
    LiveCellsEncoding? Encode(
        VtFrame frame,
        VtFrame? baseline,
        string paneId,
        long generation,
        long baseGeneration,
        int occupantGeneration);
}
