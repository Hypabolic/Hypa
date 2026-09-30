using System.IO.Compression;
using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.History;

/// <summary>
/// In-box history codec. Uses <c>System.IO.Compression.BrotliEncoder</c>
/// at a low quality. AOT safe. No extra package.
/// </summary>
public sealed class BrotliPaneHistoryCodec : IPaneHistoryCodec
{
    public const int Quality = 1;
    public const int WindowBits = 22;

    public PaneHistoryResult<byte[]> Compress(ReadOnlySpan<VtFrameCell> cells)
    {
        if (cells.IsEmpty)
            return PaneHistoryResult<byte[]>.Ok([]);

        var source = MemoryMarshal.AsBytes(cells);
        var dest = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, dest, out var written, Quality, WindowBits))
            return PaneHistoryResult<byte[]>.Fail(PaneHistoryError.CompressFailed);
        if (written < dest.Length)
            Array.Resize(ref dest, written);
        return PaneHistoryResult<byte[]>.Ok(dest);
    }

    public PaneHistoryResult<VtFrameCell[]> Decompress(ReadOnlySpan<byte> compressed, int cellCount)
    {
        if (cellCount < 0)
            return PaneHistoryResult<VtFrameCell[]>.Fail(PaneHistoryError.DecodeFailed);
        if (cellCount == 0)
            return PaneHistoryResult<VtFrameCell[]>.Ok([]);
        if (compressed.IsEmpty)
            return PaneHistoryResult<VtFrameCell[]>.Fail(PaneHistoryError.DecodeFailed);

        var cells = new VtFrameCell[cellCount];
        var dest = MemoryMarshal.AsBytes(cells.AsSpan());
        if (!BrotliDecoder.TryDecompress(compressed, dest, out var written) || written != dest.Length)
            return PaneHistoryResult<VtFrameCell[]>.Fail(PaneHistoryError.DecodeFailed);
        return PaneHistoryResult<VtFrameCell[]>.Ok(cells);
    }
}
