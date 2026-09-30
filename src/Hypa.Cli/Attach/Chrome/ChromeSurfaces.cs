using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>
// / Named chrome rects.
/// the host into sidebar and main siblings. Tab and pane area sit on main.
/// Desktop <see cref="StatusRow"/> stays height 0. Mobile uses the header.
/// <see cref="HostCage"/>, <see cref="ExtraTop"/>, and
/// <see cref="ExtraBottom"/> stay height 0 and unpainted.
/// </summary>
public sealed record ChromeSurfaces(
    CellRect Sidebar,
    CellRect Main,
    CellRect TabRow,
    CellRect StatusRow,
    CellRect PaneArea,
    CellRect HostCage,
    CellRect ExtraTop,
    CellRect ExtraBottom)
{
    public static ChromeSurfaces Unpainted(int cols, int rows)
    {
        cols = Math.Max(0, cols);
        rows = Math.Max(0, rows);
        var empty = new CellRect(0, 0, cols, 0);
        var host = new CellRect(0, 0, cols, rows);
        return new(
            new CellRect(0, 0, 0, rows),
            host,
            empty,
            empty,
            host,
            empty,
            empty,
            empty);
    }
}
