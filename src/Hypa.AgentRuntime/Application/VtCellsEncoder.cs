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
        int occupantGeneration)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        if (frame.Rows == 0)
            return null;

        var packed = VtCellPacker.Prepare(frame, baseline, dirtyRows: null);
        if (packed is null)
            return null;

        var payload = VtCellPacker.ToPayload(
            packed.Value, paneId, generation, baseGeneration, occupantGeneration);
        var utf8 = VtCellPacker.SerializeUtf8(payload);
        return new LiveCellsEncoding(payload with { WireBytes = utf8.Length }, utf8);
    }
}
