using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public interface IPanePlacementAuthorityService
{
    string IssueOccupant(PaneId paneId, int generation);

    string IssueParentCapability(PaneId childPaneId);

    Result<PaneId?, PlacementAuthorityError> ValidateCreateParent(PlacementParentProof proof);

    Result<PlacementAuthorityGrant, PlacementAuthorityError> Authorize(
        PlacementAuthorityRequest request,
        IOverlayPlacementFence? fence = null);

    Result<PlacementApplyResult<T, E>, PlacementAuthorityError> Apply<T, E>(
        PlacementAuthorityRequest request,
        Func<Result<T, E>> mutate,
        IOverlayPlacementFence? fence = null,
        bool commitSequence = true);

    void CommitSequence(PlacementAuthorityGrant grant, long sequence);

    void RevokePane(PaneId paneId);

    void RevokeOccupant(PaneId paneId);

    bool HasOccupant(PaneId paneId);

    bool HasParentCapability(PaneId paneId);
}
