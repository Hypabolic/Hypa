namespace Hypa.Cli.Attach.Copy;

/// <summary>Local copy-mode motions. Does not send keys to the pane.</summary>
public static class CopyMotions
{
    public enum CharClass
    {
        Blank = 0,
        Word,
        Punct,
    }

    public static void Left(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        var prev = buf.PrevPrimaryCol(buf.CursorRow, buf.CursorCol);
        if (prev >= 0)
        {
            buf.SetCursor(buf.CursorRow, prev);
            return;
        }

        if (buf.CursorRow > 0)
            buf.SetCursor(buf.CursorRow - 1, buf.LastPrimaryCol(buf.CursorRow - 1));
    }

    public static void Right(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        var next = buf.NextPrimaryCol(buf.CursorRow, buf.CursorCol);
        if (next >= 0)
        {
            buf.SetCursor(buf.CursorRow, next);
            return;
        }

        if (buf.CursorRow + 1 < buf.RowCount)
            buf.SetCursor(buf.CursorRow + 1, buf.FirstPrimaryCol(buf.CursorRow + 1));
    }

    public static void Up(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.CursorRow > 0)
            buf.SetCursor(buf.CursorRow - 1, buf.CursorCol);
    }

    public static void Down(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.CursorRow + 1 < buf.RowCount)
            buf.SetCursor(buf.CursorRow + 1, buf.CursorCol);
    }

    public static void WordForward(CopyModeBuffer buf, bool big)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.IsEmpty)
            return;
        var startRow = buf.CursorRow;
        var startCol = buf.CursorCol;
        var cls = ClassifyAt(buf, startRow, startCol, big);
        if (cls is not CharClass.Blank)
            SkipClass(buf, cls, big);
        SkipClass(buf, CharClass.Blank, big);
        if (buf.CursorRow == startRow && buf.CursorCol == startCol)
            Right(buf);
    }

    public static void WordBack(CopyModeBuffer buf, bool big)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.IsEmpty)
            return;
        var startRow = buf.CursorRow;
        var startCol = buf.CursorCol;
        Left(buf);
        SkipClassBack(buf, CharClass.Blank, big);
        var cls = ClassifyAt(buf, buf.CursorRow, buf.CursorCol, big);
        if (cls is not CharClass.Blank)
            SkipToClassStart(buf, cls, big);
        if (buf.CursorRow == startRow && buf.CursorCol == startCol)
            Left(buf);
    }

    public static void WordEnd(CopyModeBuffer buf, bool big)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.IsEmpty)
            return;
        var startRow = buf.CursorRow;
        var startCol = buf.CursorCol;
        if (AtWordEnd(buf, big))
            Right(buf);
        SkipClass(buf, CharClass.Blank, big);
        var cls = ClassifyAt(buf, buf.CursorRow, buf.CursorCol, big);
        if (cls is not CharClass.Blank)
            SkipToClassEnd(buf, cls, big);
        if (buf.CursorRow == startRow && buf.CursorCol == startCol)
            Right(buf);
    }

    public static void ParagraphForward(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.IsEmpty)
            return;
        var row = buf.CursorRow;
        while (row + 1 < buf.RowCount && buf.IsBlankLine(row))
            row++;
        while (row + 1 < buf.RowCount && !buf.IsBlankLine(row))
            row++;
        buf.SetCursor(row, buf.FirstPrimaryCol(row));
    }

    public static void ParagraphBack(CopyModeBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.IsEmpty)
            return;
        var row = buf.CursorRow;
        if (row > 0)
            row--;
        while (row > 0 && !buf.IsBlankLine(row))
            row--;
        while (row > 0 && buf.IsBlankLine(row) && buf.IsBlankLine(row - 1))
            row--;
        buf.SetCursor(row, buf.FirstPrimaryCol(row));
    }

    public static void Page(CopyModeBuffer buf, int deltaRows)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (deltaRows == 0 || buf.IsEmpty)
            return;
        buf.SetCursor(buf.CursorRow + deltaRows, buf.CursorCol);
    }

    public static void PageDown(CopyModeBuffer buf) =>
        Page(buf, Math.Max(1, buf.ViewportRows));

    public static void PageUp(CopyModeBuffer buf) =>
        Page(buf, -Math.Max(1, buf.ViewportRows));

    public static void HalfDown(CopyModeBuffer buf) =>
        Page(buf, Math.Max(1, buf.ViewportRows / 2));

    public static void HalfUp(CopyModeBuffer buf) =>
        Page(buf, -Math.Max(1, buf.ViewportRows / 2));

    public static CharClass Classify(char ch, bool big)
    {
        if (char.IsWhiteSpace(ch))
            return CharClass.Blank;
        if (big)
            return CharClass.Word;
        if (char.IsAsciiLetterOrDigit(ch) || ch == '_')
            return CharClass.Word;
        return CharClass.Punct;
    }

    public static bool TryWordBounds(
        CopyModeBuffer buf,
        int row,
        int col,
        out int startCol,
        out int endCol)
    {
        ArgumentNullException.ThrowIfNull(buf);
        startCol = col;
        endCol = col;
        if (buf.IsEmpty)
            return false;
        var cls = ClassifyAt(buf, row, col, big: false);
        if (cls is CharClass.Blank)
            return false;
        while (true)
        {
            var prev = buf.PrevPrimaryCol(row, startCol);
            if (prev < 0 || ClassifyAt(buf, row, prev, big: false) != cls)
                break;
            startCol = prev;
        }

        while (true)
        {
            var next = buf.NextPrimaryCol(row, endCol);
            if (next < 0 || ClassifyAt(buf, row, next, big: false) != cls)
                break;
            endCol = next;
        }

        return true;
    }

    public static CharClass ClassifyAt(CopyModeBuffer buf, int row, int col, bool big)
    {
        var cell = buf.CellAt(row, col);
        if (cell.IsContinuation || cell.Text.Length == 0)
            return CharClass.Blank;
        return Classify(cell.Text[0], big);
    }

    private static bool AtEnd(CopyModeBuffer buf) =>
        buf.CursorRow >= buf.RowCount - 1
        && buf.NextPrimaryCol(buf.CursorRow, buf.CursorCol) < 0;

    private static bool AtStart(CopyModeBuffer buf) =>
        buf.CursorRow <= 0 && buf.PrevPrimaryCol(buf.CursorRow, buf.CursorCol) < 0;

    private static bool AtWordEnd(CopyModeBuffer buf, bool big)
    {
        var cls = ClassifyAt(buf, buf.CursorRow, buf.CursorCol, big);
        if (cls is CharClass.Blank)
            return true;
        var nextCol = buf.NextPrimaryCol(buf.CursorRow, buf.CursorCol);
        if (nextCol < 0)
            return true;
        return ClassifyAt(buf, buf.CursorRow, nextCol, big) != cls;
    }

    private static void SkipClass(CopyModeBuffer buf, CharClass cls, bool big)
    {
        var guard = 0;
        var limit = Math.Max(8, buf.RowCount * Math.Max(1, buf.Cols) + 8);
        var startRow = buf.CursorRow;
        while (!AtEnd(buf) && ClassifyAt(buf, buf.CursorRow, buf.CursorCol, big) == cls)
        {
            var row = buf.CursorRow;
            var col = buf.CursorCol;
            Right(buf);
            if (buf.CursorRow == row && buf.CursorCol == col)
                break;
            if (cls is not CharClass.Blank && buf.CursorRow != startRow)
                break;
            if (++guard > limit)
                break;
        }
    }

    private static void SkipClassBack(CopyModeBuffer buf, CharClass cls, bool big)
    {
        var guard = 0;
        var limit = Math.Max(8, buf.RowCount * Math.Max(1, buf.Cols) + 8);
        var startRow = buf.CursorRow;
        while (!AtStart(buf) && ClassifyAt(buf, buf.CursorRow, buf.CursorCol, big) == cls)
        {
            var row = buf.CursorRow;
            var col = buf.CursorCol;
            Left(buf);
            if (buf.CursorRow == row && buf.CursorCol == col)
                break;
            if (cls is not CharClass.Blank && buf.CursorRow != startRow)
                break;
            if (++guard > limit)
                break;
        }
    }

    private static void SkipToClassStart(CopyModeBuffer buf, CharClass cls, bool big)
    {
        var guard = 0;
        var limit = Math.Max(8, buf.RowCount * Math.Max(1, buf.Cols) + 8);
        while (!AtStart(buf))
        {
            var prevCol = buf.PrevPrimaryCol(buf.CursorRow, buf.CursorCol);
            if (prevCol < 0)
                break;
            if (ClassifyAt(buf, buf.CursorRow, prevCol, big) != cls)
                break;
            var row = buf.CursorRow;
            var col = buf.CursorCol;
            Left(buf);
            if (buf.CursorRow == row && buf.CursorCol == col)
                break;
            if (++guard > limit)
                break;
        }
    }

    private static void SkipToClassEnd(CopyModeBuffer buf, CharClass cls, bool big)
    {
        var guard = 0;
        var limit = Math.Max(8, buf.RowCount * Math.Max(1, buf.Cols) + 8);
        while (!AtEnd(buf))
        {
            var nextCol = buf.NextPrimaryCol(buf.CursorRow, buf.CursorCol);
            if (nextCol < 0)
                break;
            if (ClassifyAt(buf, buf.CursorRow, nextCol, big) != cls)
                break;
            var row = buf.CursorRow;
            var col = buf.CursorCol;
            Right(buf);
            if (buf.CursorRow == row && buf.CursorCol == col)
                break;
            if (++guard > limit)
                break;
        }
    }
}
