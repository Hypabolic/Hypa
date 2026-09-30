using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.ReleaseNotes;

public sealed record ReleaseNotesLayout(
    CellRect Panel,
    CellRect Title,
    CellRect Subtitle,
    CellRect Close,
    CellRect Body,
    CellRect? ScrollTrack,
    int MaxScroll,
    CellRect Footer);

public enum ReleaseNotesHitKind
{
    None = 0,
    Close,
    Body,
    ScrollTrack,
}

public sealed record ReleaseNotesHit(ReleaseNotesHitKind Kind, int ScrollOffset = 0);
