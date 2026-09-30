using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Capture-lane hook. Changes only that client overlay id.
/// A missing hook is a no-op.
/// </summary>
public interface IOverlayVisibleSetHook
{
    Result<bool, string> PublishOverlay(string connectionId, string? overlayPaneId);
}
