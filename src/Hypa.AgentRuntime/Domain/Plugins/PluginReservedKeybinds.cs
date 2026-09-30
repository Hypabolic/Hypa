namespace Hypa.AgentRuntime.Domain.Plugins;

/// <summary>
/// Default core chords a manifest suggested key must not claim.
/// Mirrors <c>keys.json</c> defaults. Advisory keys never bind; this list
/// fails link when a plugin tries to reserve a core chord.
/// </summary>
public static class PluginReservedKeybinds
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "prefix+?",
        "prefix+s",
        "prefix+shift+n",
        "prefix+shift+g",
        "prefix+shift+w",
        "prefix+shift+d",
        "prefix+w",
        "prefix+g",
        "prefix+space",
        "prefix+q",
        "prefix+shift+r",
        "prefix+o",
        "prefix+shift+c",
        "prefix+b",
    };

    public static bool CollidesWithCoreDefault(string? suggestedKey)
    {
        if (string.IsNullOrWhiteSpace(suggestedKey))
            return false;
        return Reserved.Contains(suggestedKey.Trim());
    }
}
