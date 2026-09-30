using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Hidden occupancy and tiled show/hide. Does not own PTY or Ghostty.
/// Overlay show stays off this port until a later stage.
/// </summary>
public interface IPaneVisibilityService
{
    PaneState RegisterHidden(PaneState pane);

    bool IsHidden(PaneId paneId);

    Result<PaneVisibilityChange, PaneVisibilityError> ShowTiled(PaneShowTiledRequest request);

    Result<PaneVisibilityChange, PaneVisibilityError> HideTiled(PaneHideRequest request);
}
