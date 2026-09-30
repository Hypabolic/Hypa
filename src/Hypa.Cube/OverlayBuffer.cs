using System;

namespace HypaCube;

/// <summary>
/// Truecolor glyph layer composited over the cube cell grid.
/// Mask 0 = transparent (show the cube); nonzero = paint this glyph.
/// </summary>
public sealed class OverlayBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public ushort[] Ch { get; private set; } = Array.Empty<ushort>();
    public byte[] R { get; private set; } = Array.Empty<byte>();
    public byte[] G { get; private set; } = Array.Empty<byte>();
    public byte[] B { get; private set; } = Array.Empty<byte>();
    public byte[] Mask { get; private set; } = Array.Empty<byte>();

    public void Ensure(int cols, int rows)
    {
        if (Cols == cols && Rows == rows && Ch.Length == cols * rows)
        {
            return;
        }

        Cols = cols;
        Rows = rows;
        int n = cols * rows;
        Ch = new ushort[n];
        R = new byte[n];
        G = new byte[n];
        B = new byte[n];
        Mask = new byte[n];
    }

    public void Clear()
    {
        Ch.AsSpan().Clear();
        R.AsSpan().Clear();
        G.AsSpan().Clear();
        B.AsSpan().Clear();
        Mask.AsSpan().Clear();
    }

    public void Put(int x, int y, ushort code, byte r, byte g, byte b)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return;
        }

        int i = y * Cols + x;
        Ch[i] = code;
        R[i] = r;
        G[i] = g;
        B[i] = b;
        Mask[i] = 1;
    }

    public bool TryGet(int x, int y, out ushort code, out byte r, out byte g, out byte b)
    {
        code = 32;
        r = g = b = 0;
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return false;
        }

        int i = y * Cols + x;
        if (Mask[i] == 0)
        {
            return false;
        }

        code = Ch[i];
        r = R[i];
        g = G[i];
        b = B[i];
        return true;
    }
}

public enum OverlayAnchor : byte
{
    Center = 0,
    TopCenter = 1,
    BottomCenter = 2,
}
