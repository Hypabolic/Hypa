namespace Hypa.AgentRuntime.Application;

/// <summary>
/// The scrollbar thumb of a pane: its first row from the top of the track
/// and its length in rows.
/// </summary>
public readonly record struct ScrollbarThumbSpan(int Top, int Length);

/// <summary>
/// The thumb math of the pane scrollbar (herdr <c>src/ui/scrollbar.rs</c>).
/// The attach paints the thumb with it. The mux uses it to find a scroll
/// change that moves no thumb cell.
/// </summary>
public static class ScrollbarThumbMath
{
    /// <summary>
    /// The thumb for <paramref name="offset"/> rows from the bottom of
    /// <paramref name="maxOffset"/> history rows, on a track of
    /// <paramref name="trackRows"/> rows. Null when there is no thumb.
    /// </summary>
    public static ScrollbarThumbSpan? Compute(int offset, int maxOffset, int viewportRows, int trackRows)
    {
        if (maxOffset <= 0 || trackRows <= 0)
            return null;

        var totalRows = maxOffset + viewportRows;
        if (totalRows <= 0)
            return null;

        var thumbLen = RoundHerdr(viewportRows * (float)trackRows / totalRows);
        thumbLen = Math.Clamp(Math.Max(1, thumbLen), 1, trackRows);
        var maxThumbTop = trackRows - thumbLen;
        var scrolledFromTop = maxOffset - offset;
        if (scrolledFromTop < 0)
            scrolledFromTop = 0;
        var thumbTop = 0;
        if (maxThumbTop > 0)
        {
            thumbTop = RoundHerdr(scrolledFromTop * (float)maxThumbTop / maxOffset);
            thumbTop = Math.Clamp(thumbTop, 0, maxThumbTop);
        }

        return new ScrollbarThumbSpan(thumbTop, thumbLen);
    }

    /// <summary>herdr rounds half away from zero.</summary>
    public static int RoundHerdr(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
