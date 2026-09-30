using Hypa.Runtime.Domain.Common;
using Xunit;

namespace Hypa.UnitTests.Domain;

public sealed class ImageMimeSnifferTests
{
    // Minimal valid static PNG (1x1) with IHDR + IDAT + IEND
    private static readonly byte[] MiniPng = Convert.FromHexString(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082");

    private static readonly byte[] MiniJpeg =
        [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0xff, 0xd9];

    private static readonly byte[] MiniGif =
        "GIF89a\x01\x00\x01\x00\x00\x00\x00;"u8.ToArray();

    private static readonly byte[] MiniWebp =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F',
        0x0a, 0x00, 0x00, 0x00,
        (byte)'W', (byte)'E', (byte)'B', (byte)'P',
        (byte)'X', (byte)'X', (byte)'X', (byte)'X',
    ];

    [Fact]
    public void Detect_RecognizesPngJpegGifWebp()
    {
        Assert.Equal("image/png", ImageMimeSniffer.Detect(MiniPng));
        Assert.Equal("image/jpeg", ImageMimeSniffer.Detect(MiniJpeg));
        Assert.Equal("image/gif", ImageMimeSniffer.Detect(MiniGif));
        Assert.Equal("image/webp", ImageMimeSniffer.Detect(MiniWebp));
    }

    [Fact]
    public void Detect_ReturnsNullForTextAndOpaqueBinary()
    {
        Assert.Null(ImageMimeSniffer.Detect("hello world"u8));
        Assert.Null(ImageMimeSniffer.Detect([0x00, 0x01, 0x02, 0xff]));
    }

    [Fact]
    public void LooksLikeOpaqueBinary_DetectsNulButNotTextOrImages()
    {
        Assert.False(ImageMimeSniffer.LooksLikeOpaqueBinary("plain text\n"u8));
        Assert.False(ImageMimeSniffer.LooksLikeOpaqueBinary(MiniPng));
        Assert.True(ImageMimeSniffer.LooksLikeOpaqueBinary([0x7f, 0x45, 0x4c, 0x46, 0x00, 0x01]));
    }
}
