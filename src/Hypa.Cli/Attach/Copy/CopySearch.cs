namespace Hypa.Cli.Attach.Copy;

/// <summary>Smart-case search over a copy grid. Lower-case ignores case.</summary>
public static class CopySearch
{
    public static bool IsSmartIgnoreCase(string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;
        foreach (var ch in query)
        {
            if (char.IsUpper(ch))
                return false;
        }

        return true;
    }

    public static bool Find(
        CopyModeBuffer buf,
        string query,
        bool forward,
        bool fromNext,
        out int row,
        out int col)
    {
        row = 0;
        col = 0;
        ArgumentNullException.ThrowIfNull(buf);
        if (string.IsNullOrEmpty(query) || buf.IsEmpty)
            return false;

        var ignoreCase = IsSmartIgnoreCase(query);
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var startRow = buf.CursorRow;
        var startCol = buf.CursorCol;
        var rows = buf.RowCount;
        if (rows == 0)
            return false;

        if (forward)
        {
            for (var n = 0; n <= rows; n++)
            {
                var r = (startRow + n) % rows;
                var text = buf.LineText(r, out var map);
                var from = 0;
                if (n == 0)
                {
                    from = IndexFromCol(map, startCol);
                    if (fromNext)
                        from++;
                }

                var at = from <= text.Length ? text.IndexOf(query, Math.Max(0, from), comparison) : -1;
                if (at >= 0)
                {
                    row = r;
                    col = at < map.Count ? map[at] : buf.FirstPrimaryCol(r);
                    return true;
                }
            }

            return false;
        }

        for (var n = 0; n <= rows; n++)
        {
            var r = startRow - n;
            if (r < 0)
                r += rows;
            var text = buf.LineText(r, out var map);
            var end = text.Length;
            if (n == 0)
            {
                end = IndexFromCol(map, startCol);
                if (fromNext)
                    end--;
            }

            if (end < 0)
                continue;
            var at = LastIndexOf(text, query, Math.Min(end, text.Length - 1), comparison);
            if (at >= 0)
            {
                row = r;
                col = at < map.Count ? map[at] : buf.FirstPrimaryCol(r);
                return true;
            }
        }

        return false;
    }

    private static int IndexFromCol(List<int> map, int col)
    {
        for (var i = 0; i < map.Count; i++)
        {
            if (map[i] >= col)
                return i;
        }

        return map.Count;
    }

    private static int LastIndexOf(string text, string query, int maxStart, StringComparison comparison)
    {
        if (query.Length == 0 || text.Length == 0)
            return -1;
        var last = -1;
        var start = 0;
        while (start <= maxStart && start < text.Length)
        {
            var at = text.IndexOf(query, start, comparison);
            if (at < 0 || at > maxStart)
                break;
            last = at;
            start = at + 1;
        }

        return last;
    }
}
