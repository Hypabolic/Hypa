using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Overlay owner and busy fence. Overlay paint stays in a later lane.
/// </summary>
public sealed class OverlayPlacementFence : IOverlayPlacementFence
{
    private readonly object _gate = new();
    private readonly HashSet<string> _busyClients = new(StringComparer.Ordinal);

    public void MarkBusy(string attachClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachClientId);
        lock (_gate)
            _busyClients.Add(attachClientId.Trim());
    }

    public void ClearBusy(string attachClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachClientId);
        lock (_gate)
            _busyClients.Remove(attachClientId.Trim());
    }

    public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId)
    {
        if (!string.Equals(mode, "overlay", StringComparison.Ordinal))
            return Result<bool, PlacementAuthorityError>.Ok(true);

        if (string.IsNullOrWhiteSpace(attachClientId))
            return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.MissingAttachClient);

        lock (_gate)
        {
            if (_busyClients.Contains(attachClientId.Trim()))
                return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.OverlayBusy);
        }

        return Result<bool, PlacementAuthorityError>.Ok(true);
    }
}
