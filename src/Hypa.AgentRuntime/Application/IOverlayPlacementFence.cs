using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public interface IOverlayPlacementFence
{
    Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId);

    Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId, string? paneId) =>
        Check(mode, attachClientId);
}
