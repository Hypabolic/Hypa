namespace Hypa.AgentRuntime.Application.Sidebar;

public enum GotoTargetKind
{
    Workspace,
    Tab,
    Pane,
    Agent,
    Cube,
    PluginAction,
}

public sealed record GotoTarget(
    GotoTargetKind Kind,
    string Id,
    string Label,
    string State = SidebarTokenGrammar.Unknown,
    string KindText = "",
    string ReachabilityText = "");

public sealed record GotoMatch(
    GotoTarget Target,
    int Rank,
    bool StartsWith,
    bool Contains);

/// <summary>
/// Case-insensitive subsequence / starts-with rank, then contains.
/// Empty query lists all targets.
/// </summary>
public static class GotoTargetMatcher
{
    public static IReadOnlyList<GotoMatch> Match(IReadOnlyList<GotoTarget> catalog, string? query)
    {
        if (catalog is null || catalog.Count == 0)
            return [];

        var q = (query ?? "").Trim();
        if (q.Length == 0)
        {
            return catalog
                .Select(t => new GotoMatch(t, Rank: 0, StartsWith: false, Contains: false))
                .ToArray();
        }

        var matches = new List<GotoMatch>(catalog.Count);
        foreach (var target in catalog)
        {
            if (!TryRank(target, q, out var match))
                continue;
            matches.Add(match);
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Target.Kind)
            .ThenBy(m => m.Target.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Target.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryRank(GotoTarget target, string query, out GotoMatch match)
    {
        match = default!;
        var label = target.Label ?? "";
        var id = target.Id ?? "";
        if (StartsWith(label, query) || StartsWith(id, query))
        {
            match = new GotoMatch(target, Rank: 0, StartsWith: true, Contains: true);
            return true;
        }

        if (IsSubsequence(label, query) || IsSubsequence(id, query))
        {
            match = new GotoMatch(target, Rank: 1, StartsWith: false, Contains: true);
            return true;
        }

        if (Contains(label, query) || Contains(id, query))
        {
            match = new GotoMatch(target, Rank: 2, StartsWith: false, Contains: true);
            return true;
        }

        return false;
    }

    private static bool StartsWith(string text, string query) =>
        text.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool IsSubsequence(string text, string query)
    {
        if (query.Length == 0)
            return true;
        var i = 0;
        foreach (var ch in text)
        {
            if (char.ToLowerInvariant(ch) != char.ToLowerInvariant(query[i]))
                continue;
            i++;
            if (i >= query.Length)
                return true;
        }

        return false;
    }
}
