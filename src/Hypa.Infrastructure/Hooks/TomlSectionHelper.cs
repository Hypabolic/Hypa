namespace Hypa.Infrastructure.Hooks;

internal static class TomlSectionHelper
{
    internal static int FindNextNonDescendantSection(List<string> lines, int startIdx, string sectionPath)
    {
        for (var i = startIdx; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith('[')) continue;
            if (!IsDescendantSectionHeader(lines[i], sectionPath)) return i;
        }
        return -1;
    }

    internal static bool IsDescendantSectionHeader(string line, string sectionPath)
    {
        if (!TryParseHeaderPath(line, out var headerPath)) return false;
        return headerPath == sectionPath ||
               headerPath.StartsWith(sectionPath + ".", StringComparison.Ordinal);
    }

    internal static bool TryParseHeaderPath(string line, out string headerPath)
    {
        var trimmed = line.TrimEnd('\r').Trim();
        if (trimmed.StartsWith("[["))
        {
            var close = trimmed.IndexOf("]]", 2, StringComparison.Ordinal);
            if (close >= 0) { headerPath = trimmed[2..close].Trim(); return true; }
        }
        else if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']', 1);
            if (close >= 0) { headerPath = trimmed[1..close].Trim(); return true; }
        }
        headerPath = string.Empty;
        return false;
    }
}
