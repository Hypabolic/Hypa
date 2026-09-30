using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public sealed class PaneVisibilityService : IPaneVisibilityService
{
    private readonly AppState _state;

    public PaneVisibilityService(AppState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PaneState RegisterHidden(PaneState pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        var hidden = pane.Placement == PanePlacement.Hidden
            ? pane
            : pane with { Placement = PanePlacement.Hidden };
        return _state.RegisterPane(hidden);
    }

    public bool IsHidden(PaneId paneId)
    {
        var pane = _state.GetPane(paneId);
        return pane is { Placement: PanePlacement.Hidden };
    }

    public Result<PaneVisibilityChange, PaneVisibilityError> ShowTiled(PaneShowTiledRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _state.TryShowTiled(
            request.PaneId,
            request.TabId,
            request.TargetPaneId,
            request.Direction,
            request.Ratio,
            request.Focus);
    }

    public Result<PaneVisibilityChange, PaneVisibilityError> HideTiled(PaneHideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _state.TryHideTiled(request.PaneId);
    }
}
