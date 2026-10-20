namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Default live cells encoder. Packs against the admitted baseline, then
// / serialises one time.
// / <c>write_message</c> encodes one message one time.
/// <c>src/server/render_stream.rs:75-78</c> returns none for an unchanged
/// frame.
/// </summary>
public sealed class VtCellsEncoder : ILiveCellsEncoder
{
    public LiveCellsEncoding? Encode(
        VtFrame frame,
        VtFrame? baseline,
        string paneId,
        long generation,
        long baseGeneration,
        int occupantGeneration,
        long? inputAck = null,
        bool echoMode = false,
        bool rowShift = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        if (frame.Rows == 0)
            return null;

        var packed = VtCellPacker.Prepare(frame, baseline, dirtyRows: null, rowShift);
        if (packed is null)
            return null;

        var payload = VtCellPacker.ToPayload(
            packed.Value, paneId, generation, baseGeneration, occupantGeneration);
        if (inputAck is not null)
            payload = payload with { InputAck = inputAck };
        // A peer without the capability gets no new field. An unknown mode
        // writes no field either.
        if (echoMode && (frame.Modes.Echo is not null || frame.Modes.Canonical is not null))
            payload = payload with { Echo = frame.Modes.Echo, Canonical = frame.Modes.Canonical };
        var utf8 = VtCellPacker.SerializeUtf8(payload);
        return new LiveCellsEncoding(payload with { WireBytes = utf8.Length }, utf8);
    }
}
