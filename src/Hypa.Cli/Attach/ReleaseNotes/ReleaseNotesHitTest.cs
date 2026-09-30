namespace Hypa.Cli.Attach.ReleaseNotes;

public static class ReleaseNotesHitTest
{
    public static ReleaseNotesHit Hit(ReleaseNotesLayout layout, int col, int row)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Close.Contains(col, row))
            return new ReleaseNotesHit(ReleaseNotesHitKind.Close);

        if (layout.ScrollTrack is { } track && track.Contains(col, row))
        {
            var ratio = track.Rows <= 1
                ? 0d
                : (double)(row - track.Row) / Math.Max(1, track.Rows - 1);
            var offset = (int)Math.Round(ratio * layout.MaxScroll);
            return new ReleaseNotesHit(ReleaseNotesHitKind.ScrollTrack, offset);
        }

        if (layout.Body.Contains(col, row))
            return new ReleaseNotesHit(ReleaseNotesHitKind.Body);

        return new ReleaseNotesHit(ReleaseNotesHitKind.None);
    }
}
