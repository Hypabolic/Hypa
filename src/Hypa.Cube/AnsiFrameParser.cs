using System;
using System.Globalization;

namespace HypaCube;

/// <summary>
/// Parses a newline-separated ANSI frame (Hypa-TTFX / ttfx style) into an
/// <see cref="OverlayBuffer"/>. Handles SGR truecolor fg and reset; ignores
/// other CSI. One codepoint = one cell (matches Hypa-TTFX Rune width).
/// </summary>
public static class AnsiFrameParser
{
    public static OverlayBuffer Parse(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var overlay = new OverlayBuffer();
        ParseInto(frame, overlay);
        return overlay;
    }

    public static void ParseInto(string frame, OverlayBuffer dest)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(dest);

        // Measure extents first (ignore empty trailing newline).
        int cols = 0;
        int rows = 0;
        int col = 0;
        for (int i = 0; i < frame.Length;)
        {
            char c = frame[i];
            if (c == '\n')
            {
                cols = Math.Max(cols, col);
                rows++;
                col = 0;
                i++;
                continue;
            }

            if (c == '\r')
            {
                i++;
                continue;
            }

            if (c == '\x1b')
            {
                i = SkipCsi(frame, i);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                i += char.IsHighSurrogate(c) && i + 1 < frame.Length ? 2 : 1;
                col++;
                continue;
            }

            col++;
            i++;
        }

        if (col > 0 || rows == 0)
        {
            cols = Math.Max(cols, col);
            rows++;
        }

        // Trailing newline-only row: Hypa-TTFX frames often end without a final
        // orphan row; if the last char was \n we already counted. If frame is
        // empty, keep 0×0.
        if (frame.Length == 0)
        {
            dest.Ensure(0, 0);
            dest.Clear();
            return;
        }

        dest.Ensure(Math.Max(1, cols), Math.Max(1, rows));
        dest.Clear();

        byte fr = 180, fg = 230, fb = 196;
        bool haveColor = false;
        int x = 0, y = 0;
        for (int i = 0; i < frame.Length;)
        {
            char c = frame[i];
            if (c == '\n')
            {
                x = 0;
                y++;
                i++;
                continue;
            }

            if (c == '\r')
            {
                i++;
                continue;
            }

            if (c == '\x1b')
            {
                if (TryParseSgr(frame, ref i, out byte pr, out byte pg, out byte pb, out bool reset))
                {
                    if (reset)
                    {
                        haveColor = false;
                    }
                    else
                    {
                        fr = pr;
                        fg = pg;
                        fb = pb;
                        haveColor = true;
                    }

                    continue;
                }

                i = SkipCsi(frame, i);
                continue;
            }

            ushort code;
            if (char.IsHighSurrogate(c) && i + 1 < frame.Length && char.IsLowSurrogate(frame[i + 1]))
            {
                code = (ushort)char.ConvertToUtf32(c, frame[i + 1]);
                i += 2;
            }
            else
            {
                code = c;
                i++;
            }

            if (code != 32)
            {
                byte r = haveColor ? fr : (byte)180;
                byte g = haveColor ? fg : (byte)230;
                byte b = haveColor ? fb : (byte)196;
                dest.Put(x, y, code, r, g, b);
            }

            x++;
        }
    }

    /// <summary>
    /// Blit <paramref name="src"/> onto <paramref name="dest"/> at the given
    /// terminal origin (top-left of overlay).
    /// </summary>
    public static void Blit(OverlayBuffer src, OverlayBuffer dest, int originX, int originY)
    {
        for (int y = 0; y < src.Rows; y++)
        {
            for (int x = 0; x < src.Cols; x++)
            {
                if (!src.TryGet(x, y, out ushort code, out byte r, out byte g, out byte b))
                {
                    continue;
                }

                dest.Put(originX + x, originY + y, code, r, g, b);
            }
        }
    }

    public static (int X, int Y) AnchorOrigin(
        OverlayAnchor anchor,
        int terminalCols,
        int terminalRows,
        int overlayCols,
        int overlayRows,
        int hudTop = 1,
        int hudBottom = 3)
    {
        int usableTop = hudTop;
        int usableBottom = Math.Max(usableTop + 1, terminalRows - hudBottom);
        int usableH = Math.Max(1, usableBottom - usableTop);
        int x = Math.Max(0, (terminalCols - overlayCols) / 2);
        int y = anchor switch
        {
            OverlayAnchor.TopCenter => usableTop + 1,
            OverlayAnchor.BottomCenter => Math.Max(usableTop, usableBottom - overlayRows - 1),
            _ => usableTop + Math.Max(0, (usableH - overlayRows) / 2),
        };
        return (x, y);
    }

    private static bool TryParseSgr(
        string s,
        ref int i,
        out byte r,
        out byte g,
        out byte b,
        out bool reset)
    {
        r = g = b = 0;
        reset = false;
        // ESC [
        if (i + 1 >= s.Length || s[i] != '\x1b' || s[i + 1] != '[')
        {
            return false;
        }

        int start = i + 2;
        int j = start;
        while (j < s.Length)
        {
            char c = s[j];
            if (c is >= '0' and <= '9' or ';')
            {
                j++;
                continue;
            }

            break;
        }

        if (j >= s.Length || s[j] != 'm')
        {
            return false;
        }

        string body = s[start..j];
        i = j + 1;
        if (body.Length == 0 || body == "0")
        {
            reset = true;
            return true;
        }

        // 38;2;R;G;B
        string[] parts = body.Split(';');
        if (parts.Length >= 5
            && parts[0] == "38"
            && parts[1] == "2"
            && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
        {
            return true;
        }

        // Unknown SGR — treat as handled (consumed) but no color change.
        return true;
    }

    private static int SkipCsi(string s, int i)
    {
        // ESC ... letter finalizer
        if (i + 1 >= s.Length)
        {
            return i + 1;
        }

        if (s[i + 1] != '[')
        {
            // ESC + one more (e.g. ESC 7) — skip 2
            return i + 2;
        }

        int j = i + 2;
        while (j < s.Length)
        {
            char c = s[j++];
            if (c is >= (char)0x40 and <= (char)0x7E)
            {
                break;
            }
        }

        return j;
    }
}
