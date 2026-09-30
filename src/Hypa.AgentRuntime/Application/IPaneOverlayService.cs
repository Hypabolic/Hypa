using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Per-client overlay ownership. Does not own PTY or Ghostty.
/// The tab tree stays untouched.
/// </summary>
public interface IPaneOverlayService
{
    Result<PaneOverlayChange, PaneOverlayError> Show(PaneShowOverlayRequest request);

    Result<PaneOverlayChange, PaneOverlayError> Hide(PaneHideOverlayRequest request);

    Result<PaneOverlayChange, PaneOverlayError> ReleaseOwner(string attachClientId);

    Result<PaneOverlayChange, PaneOverlayError> ReleasePane(PaneId paneId);

    void RestoreOwner(PaneOverlayOwner owner);

    /// <summary>
    /// Restore or release only the reservation this operation owned.
    /// A later owner for the same client or pane stays in place.
    /// </summary>
    bool TryRevertOwner(
        string attachClientId,
        PaneOverlayOwner? priorOwner,
        PaneId operationPaneId,
        long operationGeneration,
        bool operationReleased);

    PaneOverlayOwner? TryGet(string attachClientId);

    PaneOverlayOwner? OwnerOf(PaneId paneId);

    bool HasReservation(string attachClientId);

    bool AdmitInput(PaneOverlayInputAdmit admit);

    IReadOnlyList<PaneOverlayOwner> ListOwners();
}
