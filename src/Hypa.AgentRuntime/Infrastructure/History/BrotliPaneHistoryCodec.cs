using System.Buffers;
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

        // The work buffer is rented. Only the compressed bytes are kept.
        var source = MemoryMarshal.AsBytes(cells);
        var dest = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(source.Length));
        try
        {
            if (!BrotliEncoder.TryCompress(source, dest, out var written, Quality, WindowBits))
                return PaneHistoryResult<byte[]>.Fail(PaneHistoryError.CompressFailed);
            return PaneHistoryResult<byte[]>.Ok(dest.AsSpan(0, written).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dest);
        }
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
