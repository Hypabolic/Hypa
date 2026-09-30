using System;

namespace HypaCube;

/// <summary>Tesseract (16-cell) in R⁴ — vertices, edges, square faces, starfield.</summary>
public static class Geometry
{
    public const int Verts = 16;
    public const int EdgeCount = 32;
    public const int FaceCount = 24;
    public const int StarCount = 48;

    public static readonly float[] Verts4 = new float[Verts * 4];
    public static readonly byte[] EdgeA = new byte[EdgeCount];
    public static readonly byte[] EdgeB = new byte[EdgeCount];
    /// <summary>0 = cube W=-1, 1 = cube W=+1, 2 = 4-space connector.</summary>
    public static readonly byte[] EdgeKind = new byte[EdgeCount];
    public static readonly byte[] FaceV = new byte[FaceCount * 4];
    public static readonly byte[] FaceKind = new byte[FaceCount];
    public static readonly float[] Stars = new float[StarCount * 3];

    static Geometry()
    {
        for (int i = 0; i < Verts; i++)
        {
            Verts4[i * 4] = Coord(i, 0);
            Verts4[i * 4 + 1] = Coord(i, 1);
            Verts4[i * 4 + 2] = Coord(i, 2);
            Verts4[i * 4 + 3] = Coord(i, 3);
        }

        int e = 0;
        for (int i = 0; i < Verts; i++)
        {
            for (int j = i + 1; j < Verts; j++)
            {
                if (Hamming(i, j) != 1)
                {
                    continue;
                }

                EdgeA[e] = (byte)i;
                EdgeB[e] = (byte)j;
                float wi = Verts4[i * 4 + 3];
                float wj = Verts4[j * 4 + 3];
                EdgeKind[e] = wi != wj ? (byte)2 : wi < 0 ? (byte)0 : (byte)1;
                e++;
            }
        }

        Span<int> vs = stackalloc int[4];
        int f = 0;
        for (int fa = 0; fa < 4; fa++)
        {
            for (int fb = fa + 1; fb < 4; fb++)
            {
                foreach (int sa in new[] { -1, 1 })
                {
                    foreach (int sb in new[] { -1, 1 })
                    {
                        int n = 0;
                        for (int i = 0; i < Verts; i++)
                        {
                            if (Coord(i, fa) == sa && Coord(i, fb) == sb)
                            {
                                vs[n++] = i;
                            }
                        }

                        OrderSquare(vs, out int a, out int b, out int c, out int d);
                        int o = f * 4;
                        FaceV[o] = (byte)a;
                        FaceV[o + 1] = (byte)b;
                        FaceV[o + 2] = (byte)c;
                        FaceV[o + 3] = (byte)d;
                        float wa = Verts4[a * 4 + 3];
                        float wb = Verts4[b * 4 + 3];
                        float wc = Verts4[c * 4 + 3];
                        float wd = Verts4[d * 4 + 3];
                        FaceKind[f] =
                            wa < 0 && wb < 0 && wc < 0 && wd < 0 ? (byte)0
                            : wa > 0 && wb > 0 && wc > 0 && wd > 0 ? (byte)1
                            : (byte)2;
                        f++;
                    }
                }
            }
        }

        int seed = 16807;
        float Rnd()
        {
            seed = (int)((seed * 16807L) % 2147483647L);
            return (seed - 1) / 2147483646f;
        }

        for (int i = 0; i < StarCount; i++)
        {
            float x = Rnd() * 2 - 1;
            float y = Rnd() * 2 - 1;
            float z = Rnd() * 2 - 1;
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len == 0)
            {
                len = 1;
            }

            Stars[i * 3] = (x / len) * 2.8f;
            Stars[i * 3 + 1] = (y / len) * 2.8f;
            Stars[i * 3 + 2] = (z / len) * 2.8f;
        }
    }

    private static int Hamming(int a, int b)
    {
        int x = a ^ b;
        int n = 0;
        while (x != 0)
        {
            n += x & 1;
            x >>= 1;
        }

        return n;
    }

    private static float Coord(int i, int axis) => (i & (1 << axis)) != 0 ? 1f : -1f;

    private static void OrderSquare(Span<int> vs, out int a, out int b, out int c, out int d)
    {
        a = vs[0];
        b = vs[1];
        for (int i = 0; i < 4; i++)
        {
            int v = vs[i];
            if (v != a && Hamming(a, v) == 1)
            {
                b = v;
                break;
            }
        }

        c = vs[2];
        for (int i = 0; i < 4; i++)
        {
            int v = vs[i];
            if (v != a && v != b && Hamming(b, v) == 1)
            {
                c = v;
                break;
            }
        }

        d = vs[3];
        for (int i = 0; i < 4; i++)
        {
            int v = vs[i];
            if (v != a && v != b && v != c)
            {
                d = v;
            }
        }
    }
}
