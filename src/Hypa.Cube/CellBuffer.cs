using System;

namespace HypaCube;

/// <summary>
/// Character + palette-index grid. Public so hosts (and eventually Hypa-TTFX)
/// can composite overlays without round-tripping ANSI strings.
/// </summary>
public sealed class CellBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public ushort[] Ch { get; private set; } = Array.Empty<ushort>();
    public byte[] Ci { get; private set; } = Array.Empty<byte>();
    public ushort[] PrevCh { get; private set; } = Array.Empty<ushort>();
    public byte[] PrevCi { get; private set; } = Array.Empty<byte>();

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
        Ci = new byte[n];
        PrevCh = new ushort[n];
        PrevCi = new byte[n];
        InvalidatePrev();
    }

    public void Clear(ushort fillCh, byte fillCi)
    {
        Ch.AsSpan().Fill(fillCh);
        Ci.AsSpan().Fill(fillCi);
    }

    public void InvalidatePrev() => PrevCh.AsSpan().Fill(0xFFFF);

    public void CommitPrev()
    {
        Ch.AsSpan().CopyTo(PrevCh);
        Ci.AsSpan().CopyTo(PrevCi);
    }

    public void Put(int x, int y, ushort code, byte ci)
    {
        if ((uint)x >= (uint)Cols || (uint)y >= (uint)Rows)
        {
            return;
        }

        int i = y * Cols + x;
        Ch[i] = code;
        Ci[i] = ci;
    }

    public void PutStr(int x, int y, string s, byte ci)
    {
        int cx = x;
        foreach (char ch in s)
        {
            Put(cx++, y, ch, ci);
        }
    }
}

internal sealed class PixBuffer
{
    public int W { get; private set; }
    public int H { get; private set; }
    public byte[] Lum { get; private set; } = Array.Empty<byte>();
    public byte[] Hue { get; private set; } = Array.Empty<byte>();

    public void Ensure(int w, int h)
    {
        if (W == w && H == h && Lum.Length == w * h)
        {
            return;
        }

        W = w;
        H = h;
        Lum = new byte[w * h];
        Hue = new byte[w * h];
    }
}
