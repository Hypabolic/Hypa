namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Wire values for <c>pane.read</c> / <c>agent.read</c> <c>source</c>.
/// CLI flag for unwrap is <c>recent-unwrapped</c>; the wire enum is <c>recent_unwrapped</c>.
/// </summary>
public static class ProtocolPaneReadSources
{
    public const string Visible = "visible";
    public const string Recent = "recent";
    public const string RecentUnwrapped = "recent_unwrapped";
    public const string Detection = "detection";

    /// <summary>Legacy hyphen spelling accepted on the wire and on the CLI.</summary>
    public const string RecentUnwrappedLegacy = "recent-unwrapped";

    /// <summary>Map legacy hyphen <c>recent-unwrapped</c> to the wire enum.</summary>
    public static string Normalize(string source) =>
        source == RecentUnwrappedLegacy ? RecentUnwrapped : source;

    /// <summary>CLI <c>--source</c>: both unwrap spellings serialize as <c>recent_unwrapped</c>.</summary>
    public static string NormalizeCli(string source) =>
        source is RecentUnwrappedLegacy or RecentUnwrapped ? RecentUnwrapped : source;
}
