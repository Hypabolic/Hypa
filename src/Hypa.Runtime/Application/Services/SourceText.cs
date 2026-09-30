using System.Text;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class SourceText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly int[] _charToByte;

    private SourceText(string text, byte[] rawBytes, int[] charToByte)
    {
        Text = text;
        RawBytes = rawBytes;
        _charToByte = charToByte;
    }

    public string Text { get; }

    public byte[] RawBytes { get; }

    public long SizeBytes => RawBytes.LongLength;

    public string Sha256Hex => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(RawBytes)).ToLowerInvariant();

    public static async Task<SourceText> ReadUtf8Async(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return FromUtf8Bytes(bytes);
    }

    public static SourceText FromUtf8Bytes(byte[] bytes)
    {
        var bomLength = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = StrictUtf8.GetString(bytes, bomLength, bytes.Length - bomLength);
        return new SourceText(text, bytes, BuildCharToByteMap(text, bomLength));
    }

    public static SourceText FromString(string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        return new SourceText(text, bytes, BuildCharToByteMap(text, 0));
    }

    public SourceSpan SpanFor(int startChar, int lengthChars)
    {
        var clampedStart = Math.Clamp(startChar, 0, Text.Length);
        var clampedEnd = Math.Clamp(startChar + lengthChars, clampedStart, Text.Length);
        var startPos = LineColumn(clampedStart);
        var endPos = LineColumn(clampedEnd);
        return new SourceSpan
        {
            StartLine = startPos.Line,
            StartColumn = startPos.Column,
            EndLine = endPos.Line,
            EndColumn = endPos.Column,
            StartByte = _charToByte[clampedStart],
            EndByte = _charToByte[clampedEnd],
        };
    }

    public int ByteOffsetForChar(int charOffset)
    {
        var clamped = Math.Clamp(charOffset, 0, Text.Length);
        return _charToByte[clamped];
    }

    public int CharOffsetForByte(int byteOffset)
    {
        var clamped = Math.Clamp(byteOffset, 0, RawBytes.Length);
        var index = Array.BinarySearch(_charToByte, clamped);
        if (index >= 0)
            return index;

        var next = ~index;
        return Math.Clamp(next - 1, 0, Text.Length);
    }

    private static int[] BuildCharToByteMap(string text, int initialByteOffset)
    {
        var map = new int[text.Length + 1];
        var byteOffset = initialByteOffset;

        for (var i = 0; i < text.Length; i++)
        {
            map[i] = byteOffset;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                byteOffset += StrictUtf8.GetByteCount(text.AsSpan(i, 2));
                i++;
                map[i] = byteOffset;
                continue;
            }

            byteOffset += StrictUtf8.GetByteCount(text.AsSpan(i, 1));
        }

        map[text.Length] = byteOffset;
        return map;
    }

    private (int Line, int Column) LineColumn(int offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }
}
