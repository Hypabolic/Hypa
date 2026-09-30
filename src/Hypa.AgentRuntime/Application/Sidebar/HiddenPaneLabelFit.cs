namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Fits tree row text to terminal cell width. Uses grapheme width.
/// </summary>
public static class HiddenPaneLabelFit
{
    public static string Fit(string? label, string? agentKind, string? state, int maxCols)
    {
        var parts = new List<string>();
        var name = SafeDisplayText.Encode(label);
        if (name.Length > 0)
            parts.Add(name);
        var kind = SafeDisplayText.Encode(agentKind);
        if (kind.Length > 0)
            parts.Add(kind);
        var attention = SafeDisplayText.Encode(state);
        if (attention.Length > 0
            && !string.Equals(attention, SidebarTokenGrammar.Unknown, StringComparison.Ordinal))
        {
            parts.Add(attention);
        }

        var text = string.Join(" ", parts);
        return SafeDisplayText.Clip(text, maxCols);
    }

    public static string Fit(string? text, int maxCols) =>
        SafeDisplayText.Clip(text, maxCols);
}
