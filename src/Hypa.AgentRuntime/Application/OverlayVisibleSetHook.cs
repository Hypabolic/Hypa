using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Forwards overlay reservation changes to the real visible-set
/// publisher. Also stores the last overlay id per client for tests.
/// </summary>
public sealed class OverlayVisibleSetHook : IOverlayVisibleSetHook
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _overlayPaneIds = new(StringComparer.Ordinal);
    private readonly IVisibleSetPublication? _publication;

    public OverlayVisibleSetHook(IVisibleSetPublication? publication = null)
    {
        _publication = publication;
    }

    public Result<bool, string> PublishOverlay(string connectionId, string? overlayPaneId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<bool, string>.Fail("connection is required");

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(overlayPaneId))
                _overlayPaneIds.Remove(connectionId);
            else
                _overlayPaneIds[connectionId] = overlayPaneId;
        }

        if (_publication is not null)
        {
            var published = _publication.PublishOverlay(connectionId, overlayPaneId);
            if (!published.IsOk)
                return Result<bool, string>.Fail(published.Error.Message);
        }

        return Result<bool, string>.Ok(true);
    }

    public string? OverlayPaneId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return null;
        lock (_gate)
            return _overlayPaneIds.TryGetValue(connectionId, out var id) ? id : null;
    }

    public IReadOnlyList<string> UnionOverlayPaneIds()
    {
        lock (_gate)
            return _overlayPaneIds.Values.Distinct(StringComparer.Ordinal).ToArray();
    }
}
