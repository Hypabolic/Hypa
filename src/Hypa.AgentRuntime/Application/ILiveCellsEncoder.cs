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
/// frame equals the client baseline. <c>inputAck</c> is the highest written
/// input seq of the observer connection. Null omits the field.
/// <c>echoMode</c> is true when the observer connection negotiated
/// <c>hypa.attach.echo_mode.v1</c>: the payload then carries the
/// <see cref="VtFrameModes.Echo"/> and <see cref="VtFrameModes.Canonical"/>
/// flags of the frame when they are known.
/// <c>rowShift</c> is true when the observer connection negotiated
/// <c>hypa.attach.row_shift.v1</c>: a delta can then carry a row shift
/// (<see cref="TerminalRenderCellsPayload.Shift"/>).
/// </summary>
public interface ILiveCellsEncoder
{
    LiveCellsEncoding? Encode(
        VtFrame frame,
        VtFrame? baseline,
        string paneId,
        long generation,
        long baseGeneration,
        int occupantGeneration,
        long? inputAck = null,
        bool echoMode = false,
        bool rowShift = false);
}
