namespace Hypa.Runtime.Domain.Common;

/// <summary>
/// Magic-byte image MIME detection for hypa_read (parity with Pi coding-agent mime sniff).
/// Detection is content-based — file extensions are never consulted.
/// </summary>
public static class ImageMimeSniffer
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>
    /// Returns a supported raster MIME type (image/png|jpeg|gif|webp), or null when not recognized.
    /// Animated PNG (acTL before IDAT) is rejected as unsupported for inline vision attachment.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> buffer)
    {
        if (StartsWith(buffer, [0xff, 0xd8, 0xff]))
        {
            // JPEG-LS (0xF7) is not a standard vision attachment.
            return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        }

        if (StartsWith(buffer, PngSignature))
            return IsStaticPng(buffer) ? "image/png" : null;

        if (StartsWithAscii(buffer, 0, "GIF87a") || StartsWithAscii(buffer, 0, "GIF89a"))
            return "image/gif";

        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP"))
            return "image/webp";

        return null;
    }

    /// <summary>
    /// Heuristic: non-image buffers containing a NUL in the leading window are treated as opaque binary.
    /// </summary>
    public static bool LooksLikeOpaqueBinary(ReadOnlySpan<byte> buffer)
    {
        if (Detect(buffer) is not null)
            return false;

        var sampleLen = Math.Min(buffer.Length, 4100);
        for (var i = 0; i < sampleLen; i++)
        {
            if (buffer[i] == 0)
                return true;
        }

        return false;
    }

    private static bool IsStaticPng(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 24)
            return false;

        // IHDR length must be 13 and type "IHDR"
        if (ReadUInt32BE(buffer, PngSignature.Length) != 13)
            return false;
        if (!StartsWithAscii(buffer, 12, "IHDR"))
            return false;

        var offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = (int)ReadUInt32BE(buffer, offset);
            var typeOffset = offset + 4;
            if (StartsWithAscii(buffer, typeOffset, "acTL"))
                return false;
            if (StartsWithAscii(buffer, typeOffset, "IDAT"))
                return true;

            var next = offset + 8 + chunkLength + 4;
            if (next <= offset || next > buffer.Length)
                return true; // incomplete sniff → allow
            offset = next;
        }

        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> prefix)
    {
        if (buffer.Length < prefix.Length)
            return false;
        return buffer[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length)
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != (byte)text[i])
                return false;
        }

        return true;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> buffer, int offset) =>
        ((uint)buffer[offset] << 24) |
        ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) |
        buffer[offset + 3];
}
