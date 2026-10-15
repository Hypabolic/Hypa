namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// The one rule that ranks the sources of a pane agent status.
/// The order, from highest to lowest:
/// <list type="number">
/// <item><description>An explicit report (<c>pane.report_agent</c>). Its source is any name except the reserved one.</description></item>
/// <item><description>Program status (OSC 7501). Its source is <see cref="ProgramStatusSource"/>.</description></item>
/// <item><description>The detector. It has no source (null).</description></item>
/// </list>
/// A source can replace a lower source. It can not replace a higher source.
/// </summary>
public static class AgentSourcePrecedence
{
    /// <summary>Reserved source name for a status that comes from program status records.</summary>
    public const string ProgramStatusSource = "osc7501";

    public const int DetectorRank = 0;
    public const int ProgramStatusRank = 1;
    public const int ReportRank = 2;

    public static int Rank(string? source) =>
        source is null
            ? DetectorRank
            : string.Equals(source, ProgramStatusSource, StringComparison.Ordinal)
                ? ProgramStatusRank
                : ReportRank;

    /// <summary>True when <paramref name="candidate"/> ranks higher than <paramref name="held"/>.</summary>
    public static bool Outranks(string? candidate, string? held) => Rank(candidate) > Rank(held);
}
