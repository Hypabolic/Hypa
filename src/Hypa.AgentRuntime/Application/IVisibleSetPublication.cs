using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Per-client visible-set publication and capture admission.
/// Overlay show calls <see cref="PublishOverlay"/>. Overlay hide passes a
/// null overlay id. Disconnect calls <see cref="Remove"/>.
/// </summary>
public interface IVisibleSetPublication
{
    Result<VisibleSetSnapshot, VisibleSetError> NoteAttached(string connectionId);

    Result<VisibleSetSnapshot, VisibleSetError> Publish(VisibleSetPublishRequest request);

    Result<VisibleSetSnapshot, VisibleSetError> PublishOverlay(
        string connectionId,
        string? overlayPaneId);

    Result<VisibleSetSnapshot, VisibleSetError> MarkFailed(string connectionId);

    Result<VisibleSetSnapshot, VisibleSetError> Remove(string connectionId);

    Result<VisibleSetSnapshot, VisibleSetError> Renew(string connectionId);

    /// <summary>
    /// Last non-empty pane set published for this connection. Surface-off
    /// may publish an empty set; surface-on restores this list.
    /// </summary>
    IReadOnlyList<string> RetainedPaneIds(string connectionId);

    bool MayCapture(string paneId, DateTimeOffset now);

    CaptureAdmission AdmitCapture(string paneId, DateTimeOffset now);

    void CommitCapture(string paneId, long generation);
}
