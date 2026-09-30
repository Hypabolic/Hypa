using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Server-owned attach views for connected clients.
/// <c>src/server/headless/client_views.rs:65-162</c>.
/// </summary>
public interface IAttachClientViewPublication
{
    Result<AttachClientView, AttachClientViewError> Bind(
        AttachClientViewBindRequest request,
        AttachClientTopology topology);

    void Unbind(string connectionId);

    Result<AttachClientViewApplyResult, AttachClientViewError> ApplyFocus(
        AttachClientViewFocusRequest request,
        AttachClientTopology topology);

    void ReconcileAll(AttachClientTopology topology);

    AttachClientView? Get(string connectionId);

    IReadOnlyList<string> ListConnectionIds();

    IReadOnlyList<string> CollectActivePaneIds(
        Func<string, bool> isSurfaceActive);
}
