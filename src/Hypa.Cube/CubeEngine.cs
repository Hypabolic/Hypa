using System;
using System.Buffers.Text;
using System.Text;

namespace HypaCube;

/// <summary>
/// Software 4D→2D terminal rasterizer. Pure: no Console I/O.
/// Tick into a <see cref="CellBuffer"/> or get delta-encoded ANSI bytes.
/// </summary>
public sealed class CubeEngine
{
    public const int Levels = 32;
    public const int Hues = 3;
    public const int PalHud = Hues * Levels;
    public const int PalDim = PalHud + 1;
    public const int PalAccent = PalHud + 2;
    public const int PalOk = PalHud + 3;
    public const int PalCount = PalHud + 4;

    private static readonly string[] Quad =
    [
        " ", "▘", "▝", "▀", "▖", "▌", "▞", "▛",
        "▗", "▚", "▐", "▜", "▄", "▙", "▟", "█",
    ];

    private static readonly string AsciiRamp = " .:-=+*#%@";

    private static readonly int[][] BrailleBits =
    [
        [0x01, 0x08],
        [0x02, 0x10],
        [0x04, 0x20],
        [0x40, 0x80],
    ];

    private static readonly string[] HelpLines =
    [
        "┌─ KEYS ────────────────────────────┐",
        "│ wasd / arrows   XW / YW           │",
        "│ q e             ZW                │",
        "│ z x             XY                │",
        "│ c v             YZ                │",
        "│ space           auto-rotate       │",
        "│ m               braille quad …    │",
        "│ t               theme             │",
        "│ f               faces on/off      │",
        "│ [ ]             explode           │",
        "│ - =             phosphor trails   │",
        "│ p               pause             │",
        "│ r               reset             │",
        "│ ?               help              │",
        "│ q / ctrl-c      quit              │",
        "└───────────────────────────────────┘",
    ];

    private readonly float[] _work = new float[Geometry.Verts * 4];
    private readonly float[] _px = new float[Geometry.Verts];
    private readonly float[] _py = new float[Geometry.Verts];
    private readonly float[] _depth = new float[Geometry.Verts];
    private readonly float[] _faceDepth = new float[Geometry.FaceCount];
    private readonly byte[] _faceOrder = new byte[Geometry.FaceCount];

    private readonly CellBuffer _cells = new();
    private readonly OverlayBuffer _overlay = new();
    private readonly OverlayBuffer _prevOverlay = new();
    private readonly PixBuffer _pix = new();
    private readonly byte[][] _sgr = new byte[PalCount][];
    private string _sgrTheme = "";
    private byte[] _outBuf = new byte[64 * 1024];
    private int _outLen;

    public CellBuffer Cells => _cells;

    /// <summary>
    /// Terminal-sized truecolor overlay. Host clears and fills each frame
    /// (e.g. Hypa-TTFX blit). Transparent cells leave the cube raster visible.
    /// </summary>
    public OverlayBuffer Overlay => _overlay;

    public Angles Angles { get; set; } = Angles.Default;
    public CubeOptions Options { get; } = new();

    public void Reset()
    {
        Angles = Angles.Default;
        Options.Explode = 0.85f;
        Options.Zoom = 1f;
        Options.Persist = 0.25f;
        Options.Paused = false;
        _cells.InvalidatePrev();
        _overlay.Clear();
        _prevOverlay.Clear();
    }

    public void Resize(int cols, int rows)
    {
        int c = Math.Max(1, cols);
        int r = Math.Max(1, rows);
        _cells.Ensure(c, r);
        _overlay.Ensure(c, r);
        _prevOverlay.Ensure(c, r);
        _cells.InvalidatePrev();
        _overlay.Clear();
        _prevOverlay.Clear();
    }

    /// <summary>Advance simulation and fill <see cref="Cells"/>.</summary>
    public void Tick(float dt)
    {
        if (!Options.Paused && Options.AutoRotate)
        {
            Angles a = Angles;
            a.Xw += 0.55f * dt;
            a.Yz += 0.41f * dt;
            a.Yw += 0.13f * dt;
            a.Zw += 0.09f * dt;
            a.Xy += 0.04f * dt;
            Angles = a;
        }

        RenderCells(Options.Paused ? 0f : dt);
    }

    /// <summary>
    /// Tick and return delta-encoded ANSI (or full frame when forced).
    /// Buffer is rented from the engine; valid until next call.
    /// </summary>
    public ReadOnlySpan<byte> TickAnsi(float dt, bool force = false)
    {
        Tick(dt);
        return EncodeAnsi(force);
    }

    public ReadOnlySpan<byte> FullClearSeq()
    {
        Theme t = Options.Theme;
        // \x1b[?1049h\x1b[?25l\x1b[?7l\x1b[48;2;R;G;Bm\x1b[2J\x1b[H
        _outLen = 0;
        AppendAscii("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[48;2;");
        AppendByteNum(t.Bg.R);
        AppendAscii(";");
        AppendByteNum(t.Bg.G);
        AppendAscii(";");
        AppendByteNum(t.Bg.B);
        AppendAscii("m\x1b[2J\x1b[H");
        return _outBuf.AsSpan(0, _outLen);
    }

    public static ReadOnlySpan<byte> RestoreSeqBytes => "\x1b[0m\x1b[?25h\x1b[?7h\x1b[?1049l"u8;

    public void RenderCells(float dt)
    {
        int cols = _cells.Cols;
        int rows = _cells.Rows;
        if (cols < 20 || rows < 8)
        {
            _cells.Clear(32, PalAccent);
            _cells.PutStr(0, 0, " HYPACUBE — enlarge the viewport", PalAccent);
            return;
        }

        EnsurePalette();
        _cells.Clear(32, PalDim);

        bool compact = cols < 86 || rows < 26;
        int ox;
        int oy;
        int innerW;
        int innerH;
        if (Options.Hud)
        {
            ox = compact ? 0 : 1;
            oy = 1;
            innerW = Math.Max(8, cols - ox * 2);
            innerH = Math.Max(6, rows - oy - (compact ? 1 : 3));
        }
        else
        {
            ox = 0;
            oy = 0;
            innerW = cols;
            innerH = rows;
        }

        CellScale(Options.Mode, out int sx, out int sy);
        _pix.Ensure(innerW * sx, innerH * sy);

        RasterScene(_pix, Options.Paused ? 0f : dt, Options.Zoom);
        if (Options.Hud)
        {
            DrawHud(compact);
        }

        PackFrame(ox, oy, innerW, innerH);
        if (Options.Help)
        {
            DrawHelp();
        }
    }

    public ReadOnlySpan<byte> EncodeAnsi(bool force)
    {
        EnsurePalette();
        _outLen = 0;
        AppendAscii("\x1b[?7l");

        int cols = _cells.Cols;
        int rows = _cells.Rows;
        int width = cols > 1 ? cols - 1 : cols;
        int lastPal = -1;
        int lastTr = -1, lastTg = -1, lastTb = -1;
        bool lastWasTrue = false;

        for (int y = 0; y < rows; y++)
        {
            int row = y * cols;
            bool rowDirty = force;
            if (!rowDirty)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    if (_cells.Ch[i] != _cells.PrevCh[i] || _cells.Ci[i] != _cells.PrevCi[i])
                    {
                        rowDirty = true;
                        break;
                    }

                    if (_overlay.Mask[i] != _prevOverlay.Mask[i]
                        || _overlay.Ch[i] != _prevOverlay.Ch[i]
                        || _overlay.R[i] != _prevOverlay.R[i]
                        || _overlay.G[i] != _prevOverlay.G[i]
                        || _overlay.B[i] != _prevOverlay.B[i])
                    {
                        rowDirty = true;
                        break;
                    }
                }
            }

            if (!rowDirty)
            {
                continue;
            }

            AppendAscii("\x1b[");
            AppendInt(y + 1);
            AppendAscii(";1H");
            lastPal = -1;
            lastWasTrue = false;

            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                if (_overlay.Mask[i] != 0)
                {
                    ushort oCode = _overlay.Ch[i];
                    byte or = _overlay.R[i], og = _overlay.G[i], ob = _overlay.B[i];
                    if (!lastWasTrue || or != lastTr || og != lastTg || ob != lastTb)
                    {
                        AppendTruecolor(or, og, ob);
                        lastTr = or;
                        lastTg = og;
                        lastTb = ob;
                        lastWasTrue = true;
                        lastPal = -1;
                    }

                    EmitChar(oCode);
                    continue;
                }

                ushort code = _cells.Ch[i];
                byte color = _cells.Ci[i];
                if (lastWasTrue || color != lastPal)
                {
                    byte[] seq = _sgr[color] ?? _sgr[PalDim]!;
                    AppendBytes(seq);
                    lastPal = color;
                    lastWasTrue = false;
                }

                EmitChar(code);
            }
        }

        _cells.CommitPrev();
        if (_overlay.Cols == _prevOverlay.Cols && _overlay.Rows == _prevOverlay.Rows)
        {
            _overlay.Ch.AsSpan().CopyTo(_prevOverlay.Ch);
            _overlay.R.AsSpan().CopyTo(_prevOverlay.R);
            _overlay.G.AsSpan().CopyTo(_prevOverlay.G);
            _overlay.B.AsSpan().CopyTo(_prevOverlay.B);
            _overlay.Mask.AsSpan().CopyTo(_prevOverlay.Mask);
        }

        return _outBuf.AsSpan(0, _outLen);
    }

    private void EmitChar(ushort code)
    {
        if (code == 32)
        {
            AppendByte((byte)' ');
        }
        else if (code < 128)
        {
            AppendByte((byte)code);
        }
        else
        {
            AppendUtf8(code);
        }
    }

    private void AppendTruecolor(byte r, byte g, byte b)
    {
        AppendAscii("\x1b[38;2;");
        AppendByteNum(r);
        AppendAscii(";");
        AppendByteNum(g);
        AppendAscii(";");
        AppendByteNum(b);
        AppendAscii("m");
    }

    private void EnsurePalette()
    {
        Theme theme = Options.Theme;
        if (_sgrTheme == theme.Id && _sgr[0] is not null)
        {
            return;
        }

        (Rgb far, Rgb near)[] ramps =
        [
            (theme.OuterFar, theme.OuterNear),
            (theme.InnerFar, theme.InnerNear),
            (theme.HyperFar, theme.HyperNear),
        ];

        for (int h = 0; h < Hues; h++)
        {
            Rgb far = ramps[h].far;
            Rgb near = ramps[h].near;
            for (int L = 0; L < Levels; L++)
            {
                float t = L / (float)(Levels - 1);
                float g = MathF.Pow(t, 0.72f);
                Rgb mid = Mix(Mix(theme.Bg, far, 0.55f), near, g);
                _sgr[h * Levels + L] = SgrFgBytes(mid);
            }
        }

        _sgr[PalHud] = SgrFgBytes(theme.Hud);
        _sgr[PalDim] = SgrFgBytes(theme.HudDim);
        _sgr[PalAccent] = SgrFgBytes(theme.HudAccent);
        _sgr[PalOk] = SgrFgBytes(theme.HyperNear);
        _sgrTheme = theme.Id;
        _cells.InvalidatePrev();
    }

    private static byte[] SgrFgBytes(Rgb c)
    {
        // \x1b[38;2;R;G;Bm
        string s = $"\x1b[38;2;{c.R};{c.G};{c.B}m";
        return Encoding.ASCII.GetBytes(s);
    }

    private static Rgb Mix(Rgb a, Rgb b, float t)
    {
        return new Rgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static void CellScale(RenderMode mode, out int sx, out int sy)
    {
        switch (mode)
        {
            case RenderMode.Braille:
                sx = 2;
                sy = 4;
                break;
            case RenderMode.Quad:
                sx = 2;
                sy = 2;
                break;
            case RenderMode.Block:
                sx = 1;
                sy = 2;
                break;
            default:
                sx = 1;
                sy = 1;
                break;
        }
    }

    private void RasterScene(PixBuffer p, float dt, float scaleMul)
    {
        Decay(p, Options.Persist, dt);
        RotateAll(Angles);
        float scale = Math.Min(p.W, p.H) * 0.15f * scaleMul;
        Project(scale, p.W * 0.5f, p.H * 0.5f, Options.Explode);

        float cs = MathF.Cos(Angles.Xy * 0.35f);
        float sn = MathF.Sin(Angles.Xy * 0.35f);
        for (int i = 0; i < Geometry.StarCount; i++)
        {
            float x0 = Geometry.Stars[i * 3];
            float y0 = Geometry.Stars[i * 3 + 1];
            float z0 = Geometry.Stars[i * 3 + 2];
            float x = x0 * cs - z0 * sn;
            float z = x0 * sn + z0 * cs;
            float f = 4.6f / (4.6f - z);
            float sx = x * f * scale * 1.55f + p.W * 0.5f;
            float sy = -y0 * f * scale * 1.55f + p.H * 0.5f;
            AddPix(p, sx, sy, 18 + (int)(f * 20), 2);
        }

        float dMin = float.PositiveInfinity;
        float dMax = float.NegativeInfinity;
        for (int i = 0; i < Geometry.Verts; i++)
        {
            float d = _depth[i];
            if (d < dMin)
            {
                dMin = d;
            }

            if (d > dMax)
            {
                dMax = d;
            }
        }

        float dSpan = dMax - dMin;
        if (dSpan == 0)
        {
            dSpan = 1;
        }

        if (Options.Faces)
        {
            for (int f = 0; f < Geometry.FaceCount; f++)
            {
                int o = f * 4;
                _faceDepth[f] =
                    (_depth[Geometry.FaceV[o]] +
                     _depth[Geometry.FaceV[o + 1]] +
                     _depth[Geometry.FaceV[o + 2]] +
                     _depth[Geometry.FaceV[o + 3]]) * 0.25f;
                _faceOrder[f] = (byte)f;
            }

            for (int i = 1; i < Geometry.FaceCount; i++)
            {
                byte key = _faceOrder[i];
                float kd = _faceDepth[key];
                int j = i - 1;
                while (j >= 0 && _faceDepth[_faceOrder[j]] > kd)
                {
                    _faceOrder[j + 1] = _faceOrder[j];
                    j--;
                }

                _faceOrder[j + 1] = key;
            }

            for (int i = 0; i < Geometry.FaceCount; i++)
            {
                int f = _faceOrder[i];
                int o = f * 4;
                int a = Geometry.FaceV[o];
                int b = Geometry.FaceV[o + 1];
                int c = Geometry.FaceV[o + 2];
                int d = Geometry.FaceV[o + 3];
                float near = (_faceDepth[f] - dMin) / dSpan;
                int add = (int)(6 + near * 10);
                byte hue = Geometry.FaceKind[f];
                FillTri(p, _px[a], _py[a], _px[b], _py[b], _px[c], _py[c], add, hue);
                FillTri(p, _px[a], _py[a], _px[c], _py[c], _px[d], _py[d], add, hue);
            }
        }

        for (int e = 0; e < Geometry.EdgeCount; e++)
        {
            int a = Geometry.EdgeA[e];
            int b = Geometry.EdgeB[e];
            float near = ((_depth[a] + _depth[b]) * 0.5f - dMin) / dSpan;
            float intensity = 70 + near * 185;
            float radius = 0.45f + near * 1.25f;
            DrawLine(p, _px[a], _py[a], _px[b], _py[b], intensity, Geometry.EdgeKind[e], radius);
        }

        for (int i = 0; i < Geometry.Verts; i++)
        {
            float near = (_depth[i] - dMin) / dSpan;
            byte hue = Geometry.Verts4[i * 4 + 3] < 0 ? (byte)0 : (byte)1;
            Splat(p, _px[i], _py[i], (int)(140 + near * 115), hue, 1.35f + near * 0.6f);
        }
    }

    private void RotateAll(Angles ang)
    {
        Geometry.Verts4.AsSpan().CopyTo(_work);
        ApplyPair(0, 1, ang.Xy);
        ApplyPair(0, 2, ang.Xz);
        ApplyPair(1, 2, ang.Yz);
        ApplyPair(0, 3, ang.Xw);
        ApplyPair(1, 3, ang.Yw);
        ApplyPair(2, 3, ang.Zw);
    }

    private void ApplyPair(int a, int b, float th)
    {
        if (th == 0)
        {
            return;
        }

        float c = MathF.Cos(th);
        float s = MathF.Sin(th);
        for (int i = 0; i < Geometry.Verts; i++)
        {
            int o = i * 4;
            float u = _work[o + a];
            float v = _work[o + b];
            _work[o + a] = u * c - v * s;
            _work[o + b] = u * s + v * c;
        }
    }

    private void Project(float scale, float cx, float cy, float explode)
    {
        const float d4 = 3.15f;
        const float d3 = 4.6f;
        float e = 1 + explode * 1.35f;
        for (int i = 0; i < Geometry.Verts; i++)
        {
            int o = i * 4;
            float x = _work[o];
            float y = _work[o + 1];
            float z = _work[o + 2];
            float w = _work[o + 3] * e;
            float f4 = d4 / (d4 - w);
            x *= f4;
            y *= f4;
            z *= f4;
            float f3 = d3 / (d3 - z);
            _px[i] = x * f3 * scale + cx;
            _py[i] = -y * f3 * scale + cy;
            _depth[i] = f3 * f4;
        }
    }

    private static void Decay(PixBuffer p, float persist, float dt)
    {
        float k = 1.4f + (1 - persist) * 7.5f;
        float mul = persist <= 0.02f ? 0f : MathF.Exp(-k * dt);
        byte[] lum = p.Lum;
        if (mul == 0)
        {
            lum.AsSpan().Clear();
            return;
        }

        for (int i = 0; i < lum.Length; i++)
        {
            lum[i] = (byte)(lum[i] * mul);
        }
    }

    private static void AddPix(PixBuffer p, float x, float y, int add, byte hue)
    {
        int xi = (int)x;
        int yi = (int)y;
        if ((uint)xi >= (uint)p.W || (uint)yi >= (uint)p.H)
        {
            return;
        }

        int i = yi * p.W + xi;
        int v = p.Lum[i] + add;
        p.Lum[i] = (byte)(v > 255 ? 255 : v);
        if (add > 18)
        {
            p.Hue[i] = hue;
        }
    }

    private static void Splat(PixBuffer p, float x, float y, int add, byte hue, float r)
    {
        AddPix(p, x, y, add, hue);
        if (r < 0.65f)
        {
            return;
        }

        int side = (int)(add * 0.42f);
        AddPix(p, x + 1, y, side, hue);
        AddPix(p, x - 1, y, side, hue);
        AddPix(p, x, y + 1, side, hue);
        AddPix(p, x, y - 1, side, hue);
        if (r < 1.15f)
        {
            return;
        }

        int diag = (int)(add * 0.22f);
        AddPix(p, x + 1, y + 1, diag, hue);
        AddPix(p, x - 1, y - 1, diag, hue);
        AddPix(p, x + 1, y - 1, diag, hue);
        AddPix(p, x - 1, y + 1, diag, hue);
    }

    private static void DrawLine(
        PixBuffer p,
        float x0,
        float y0,
        float x1,
        float y1,
        float intensity,
        byte hue,
        float radius)
    {
        int xa = (int)x0;
        int ya = (int)y0;
        int xb = (int)x1;
        int yb = (int)y1;
        int dx = Math.Abs(xb - xa);
        int dy = Math.Abs(yb - ya);
        int sx = xa < xb ? 1 : -1;
        int sy = ya < yb ? 1 : -1;
        int err = dx - dy;
        int add = (int)intensity;
        for (; ; )
        {
            Splat(p, xa, ya, add, hue, radius);
            if (xa == xb && ya == yb)
            {
                break;
            }

            int e2 = err << 1;
            if (e2 > -dy)
            {
                err -= dy;
                xa += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                ya += sy;
            }
        }
    }

    private static void FillTri(
        PixBuffer p,
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        int add,
        byte hue)
    {
        int minX = Math.Max(0, (int)Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(p.W - 1, (int)Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(0, (int)Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(p.H - 1, (int)Math.Max(y0, Math.Max(y1, y2)));
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 4)
        {
            return;
        }

        float inv = 1f / area;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float w0 = ((x1 - x0) * (y - y0) - (y1 - y0) * (x - x0)) * inv;
                float w1 = ((x2 - x1) * (y - y1) - (y2 - y1) * (x - x1)) * inv;
                float w2 = ((x0 - x2) * (y - y2) - (y0 - y2) * (x - x2)) * inv;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    continue;
                }

                AddPix(p, x, y, add, hue);
            }
        }
    }

    private void PackFrame(int ox, int oy, int innerW, int innerH)
    {
        CellScale(Options.Mode, out int sx, out int sy);
        int cw = Math.Min(innerW, _pix.W / sx);
        int ch = Math.Min(innerH, _pix.H / sy);
        for (int cy = 0; cy < ch; cy++)
        {
            for (int cx = 0; cx < cw; cx++)
            {
                PackCell(cx, cy, out ushort code, out int level, out byte hue);
                if (code == 32)
                {
                    continue;
                }

                int L = Math.Min(Levels - 1, level >> 3);
                byte ci = (byte)((hue % Hues) * Levels + L);
                _cells.Put(ox + cx, oy + cy, code, ci);
            }
        }
    }

    private void PackCell(int cx, int cy, out ushort code, out int level, out byte hue)
    {
        CellScale(Options.Mode, out int sx, out int sy);
        int ox = cx * sx;
        int oy = cy * sy;
        int bits = 0;
        int maxL = 0;
        byte bestHue = 0;

        int Sample(int lx, int ly)
        {
            int x = ox + lx;
            int y = oy + ly;
            if ((uint)x >= (uint)_pix.W || (uint)y >= (uint)_pix.H)
            {
                return 0;
            }

            int i = y * _pix.W + x;
            int L = _pix.Lum[i];
            if (L > maxL)
            {
                maxL = L;
                bestHue = _pix.Hue[i];
            }

            return L;
        }

        if (Options.Mode == RenderMode.Braille)
        {
            for (int ly = 0; ly < 4; ly++)
            {
                for (int lx = 0; lx < 2; lx++)
                {
                    if (Sample(lx, ly) >= 22)
                    {
                        bits |= BrailleBits[ly][lx];
                    }
                }
            }

            code = bits == 0 ? (ushort)32 : (ushort)(0x2800 + bits);
            level = maxL;
            hue = bestHue;
            return;
        }

        if (Options.Mode == RenderMode.Quad)
        {
            if (Sample(0, 0) >= 22)
            {
                bits |= 1;
            }

            if (Sample(1, 0) >= 22)
            {
                bits |= 2;
            }

            if (Sample(0, 1) >= 22)
            {
                bits |= 4;
            }

            if (Sample(1, 1) >= 22)
            {
                bits |= 8;
            }

            string ch = Quad[bits];
            code = bits == 0 ? (ushort)32 : ch[0];
            level = maxL;
            hue = bestHue;
            return;
        }

        if (Options.Mode == RenderMode.Block)
        {
            int up = Sample(0, 0);
            int dn = Sample(0, 1);
            maxL = Math.Max(up, dn);
            code = 32;
            if (up >= 28 && dn >= 28)
            {
                code = 0x2588;
            }
            else if (up >= 28)
            {
                code = 0x2580;
            }
            else if (dn >= 28)
            {
                code = 0x2584;
            }

            level = maxL;
            hue = bestHue;
            return;
        }

        int L0 = Sample(0, 0);
        int idx = Math.Min(AsciiRamp.Length - 1, (L0 * AsciiRamp.Length) >> 8);
        char chA = AsciiRamp[idx];
        code = chA == ' ' ? (ushort)32 : chA;
        level = L0;
        hue = bestHue;
    }

    private void DrawHud(bool compact)
    {
        int cols = _cells.Cols;
        int rows = _cells.Rows;
        int w = Math.Max(1, cols - 1);
        string fps = ((int)MathF.Round(Options.Fps)).ToString().PadLeft(3);
        string mode = RenderModes.Label(Options.Mode).ToUpperInvariant();
        string auto = Options.AutoRotate ? "AUTO" : "MAN";
        string pause = Options.Paused ? " PAUSE" : "";

        if (compact)
        {
            _cells.PutStr(0, 0, Clip($" HYPACUBE  {mode}  {fps}fps  {auto}{pause}", w), PalAccent);
            _cells.PutStr(
                0,
                rows - 1,
                Clip($" {Options.Theme.Label}  {(Options.Faces ? "faces" : "wire")}  wasd rotates  ? keys", w),
                PalDim);
            return;
        }

        string top = Clip(
            $"┌─ HYPACUBE · 4-space · 16v 32e · {mode} · {fps} FPS · {auto}{pause} ".PadRight(Math.Max(0, w - 1), '─') + "┐",
            w);
        _cells.PutStr(0, 0, top, PalAccent);

        string mid = "│" + new string(' ', Math.Max(0, w - 2)) + (w > 1 ? "│" : "");
        for (int y = 1; y < rows - 3; y++)
        {
            _cells.PutStr(0, y, Clip(mid, w), PalDim);
        }

        _cells.PutStr(0, rows - 3, Clip("├" + new string('─', Math.Max(0, w - 2)) + "┤", w), PalDim);

        int bw = cols >= 100 ? 8 : 6;
        string meters =
            $"│ XW {Bar(Angles.Xw, bw)}  YW {Bar(Angles.Yw, bw)}  ZW {Bar(Angles.Zw, bw)}  YZ {Bar(Angles.Yz, bw)}  {(Options.Faces ? "FACES" : "wire ")} ";
        _cells.PutStr(0, rows - 2, Clip(meters.PadRight(Math.Max(0, w - 1)) + "│", w), PalHud);

        string hint = "└ wasd · qe ZW · m mode · t theme · [ ] explode · ? ";
        _cells.PutStr(0, rows - 1, Clip(hint.PadRight(Math.Max(0, w - 1), '─') + "┘", w), PalDim);
    }

    private void DrawHelp()
    {
        int w = HelpLines[0].Length;
        int h = HelpLines.Length;
        int x = Math.Max(0, (_cells.Cols - w) / 2);
        int y = Math.Max(0, (_cells.Rows - h) / 2);
        for (int i = 0; i < h; i++)
        {
            _cells.PutStr(x, y + i, HelpLines[i], PalAccent);
        }
    }

    private static string Bar(float v, int n)
    {
        // Wrap with floor, not float %. AOT lowers float % to fmodf, and the
        // glibc 2.38 fmodf symbol does not load on Debian 12 mux hosts.
        const float tau = MathF.PI * 2;
        float t = v - (tau * MathF.Floor(v / tau));
        float f = Math.Clamp(t / tau, 0f, 1f);
        int filled = (int)MathF.Round(f * n);
        return new string('█', filled) + new string('░', Math.Max(0, n - filled));
    }

    private static string Clip(string s, int n)
    {
        if (s.Length == n)
        {
            return s;
        }

        if (s.Length > n)
        {
            return s[..n];
        }

        return s + new string(' ', n - s.Length);
    }

    private void AppendByte(byte b)
    {
        EnsureOut(1);
        _outBuf[_outLen++] = b;
    }

    private void AppendBytes(ReadOnlySpan<byte> s)
    {
        EnsureOut(s.Length);
        s.CopyTo(_outBuf.AsSpan(_outLen));
        _outLen += s.Length;
    }

    private void AppendAscii(string s)
    {
        EnsureOut(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            _outBuf[_outLen++] = (byte)s[i];
        }
    }

    private void AppendInt(int v)
    {
        EnsureOut(11);
        if (!Utf8Formatter.TryFormat(v, _outBuf.AsSpan(_outLen), out int written))
        {
            AppendAscii(v.ToString());
            return;
        }

        _outLen += written;
    }

    private void AppendByteNum(byte v) => AppendInt(v);

    private void AppendUtf8(ushort code)
    {
        EnsureOut(4);
        Rune r = new(code);
        Span<byte> tmp = stackalloc byte[4];
        int n = r.EncodeToUtf8(tmp);
        tmp[..n].CopyTo(_outBuf.AsSpan(_outLen));
        _outLen += n;
    }

    private void EnsureOut(int extra)
    {
        if (_outLen + extra <= _outBuf.Length)
        {
            return;
        }

        int n = Math.Max(_outBuf.Length * 2, _outLen + extra);
        Array.Resize(ref _outBuf, n);
    }
}
