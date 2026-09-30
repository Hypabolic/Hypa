using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public sealed class PaneOverlayService : IPaneOverlayService, IOverlayPlacementFence
{
    private readonly AppState _state;
    private readonly object _gate = new();
    private readonly Dictionary<string, PaneOverlayOwner> _byClient = new(StringComparer.Ordinal);
    private long _generation;

    public PaneOverlayService(AppState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public Result<PaneOverlayChange, PaneOverlayError> Show(PaneShowOverlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AttachClientId))
            return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                PaneOverlayError.MissingOwner("overlay mode requires attach_client_id"));

        var pane = _state.GetPane(request.PaneId);
        if (pane is null)
            return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                PaneOverlayError.NotFound($"Pane not found: {request.PaneId.Value}"));

        if (pane.Placement == PanePlacement.Tiled)
        {
            return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                PaneOverlayError.Tiled("overlay mode requires a hidden pane"));
        }

        var inner = ResolveInner(request.AreaCols, request.AreaRows, pane);
        lock (_gate)
        {
            if (_byClient.TryGetValue(request.AttachClientId, out var existing)
                && existing.PaneId == request.PaneId)
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
                {
                    Pane = pane,
                    AttachClientId = existing.AttachClientId,
                    Generation = existing.Generation,
                    Changed = false,
                    Mode = PaneOverlayWire.Overlay,
                });
            }

            if (_byClient.ContainsKey(request.AttachClientId))
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                    PaneOverlayError.Busy("ui_busy"));
            }

            foreach (var held in _byClient.Values)
            {
                if (held.PaneId == request.PaneId)
                {
                    return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                        PaneOverlayError.Busy("ui_busy"));
                }
            }

            var generation = ++_generation;
            var owner = new PaneOverlayOwner
            {
                PaneId = request.PaneId,
                AttachClientId = request.AttachClientId,
                Generation = generation,
                InnerCols = inner.Cols,
                InnerRows = inner.Rows,
            };
            _byClient[request.AttachClientId] = owner;
            return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
            {
                Pane = pane,
                AttachClientId = owner.AttachClientId,
                Generation = generation,
                Changed = true,
                Mode = PaneOverlayWire.Overlay,
            });
        }
    }

    public Result<PaneOverlayChange, PaneOverlayError> Hide(PaneHideOverlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pane = _state.GetPane(request.PaneId);
        if (pane is null)
            return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                PaneOverlayError.NotFound($"Pane not found: {request.PaneId.Value}"));

        lock (_gate)
        {
            if (!TryFindOwner(request.PaneId, request.AttachClientId, out var owner))
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
                {
                    Pane = pane,
                    AttachClientId = request.AttachClientId,
                    Generation = owner?.Generation ?? 0,
                    Changed = false,
                    Mode = PaneOverlayWire.Hidden,
                });
            }

            if (request.Generation is { } generation && generation != owner!.Generation)
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                    PaneOverlayError.StaleGeneration("stale overlay generation"));
            }

            return OkRelease(owner!, pane);
        }
    }

    public Result<PaneOverlayChange, PaneOverlayError> ReleaseOwner(string attachClientId)
    {
        if (string.IsNullOrWhiteSpace(attachClientId))
            return Result<PaneOverlayChange, PaneOverlayError>.Fail(
                PaneOverlayError.MissingOwner("overlay mode requires attach_client_id"));

        lock (_gate)
        {
            if (!_byClient.TryGetValue(attachClientId, out var owner))
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
                {
                    Pane = MissingPane(attachClientId),
                    AttachClientId = attachClientId,
                    Generation = 0,
                    Changed = false,
                    Mode = PaneOverlayWire.Hidden,
                });
            }

            var pane = _state.GetPane(owner.PaneId);
            if (pane is null)
            {
                _byClient.Remove(attachClientId);
                return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
                {
                    Pane = MissingPane(attachClientId),
                    AttachClientId = attachClientId,
                    Generation = owner.Generation,
                    Changed = true,
                    Mode = PaneOverlayWire.Hidden,
                });
            }

            return OkRelease(owner, pane);
        }
    }

    public Result<PaneOverlayChange, PaneOverlayError> ReleasePane(PaneId paneId)
    {
        var pane = _state.GetPane(paneId);
        lock (_gate)
        {
            PaneOverlayOwner? owner = null;
            foreach (var item in _byClient.Values)
            {
                if (item.PaneId == paneId)
                {
                    owner = item;
                    break;
                }
            }

            if (owner is null)
            {
                return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
                {
                    Pane = pane ?? MissingPane(paneId.Value),
                    Generation = 0,
                    Changed = false,
                    Mode = PaneOverlayWire.Hidden,
                });
            }

            return OkRelease(owner, pane ?? MissingPane(paneId.Value));
        }
    }

    public PaneOverlayOwner? TryGet(string attachClientId)
    {
        if (string.IsNullOrWhiteSpace(attachClientId))
            return null;
        lock (_gate)
            return _byClient.TryGetValue(attachClientId, out var owner) ? owner : null;
    }

    public PaneOverlayOwner? OwnerOf(PaneId paneId)
    {
        lock (_gate)
        {
            foreach (var owner in _byClient.Values)
            {
                if (owner.PaneId == paneId)
                    return owner;
            }
        }

        return null;
    }

    public bool HasReservation(string attachClientId)
    {
        if (string.IsNullOrWhiteSpace(attachClientId))
            return false;
        lock (_gate)
            return _byClient.ContainsKey(attachClientId);
    }

    public bool AdmitInput(PaneOverlayInputAdmit admit)
    {
        ArgumentNullException.ThrowIfNull(admit);
        lock (_gate)
        {
            return _byClient.TryGetValue(admit.AttachClientId, out var owner)
                && owner.PaneId == admit.PaneId
                && owner.Generation == admit.Generation;
        }
    }

    public IReadOnlyList<PaneOverlayOwner> ListOwners()
    {
        lock (_gate)
            return _byClient.Values.ToArray();
    }

    public void RestoreOwner(PaneOverlayOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.AttachClientId);
        lock (_gate)
            _byClient[owner.AttachClientId] = owner;
    }

    public bool TryRevertOwner(
        string attachClientId,
        PaneOverlayOwner? priorOwner,
        PaneId operationPaneId,
        long operationGeneration,
        bool operationReleased)
    {
        if (string.IsNullOrWhiteSpace(attachClientId))
            return false;

        lock (_gate)
        {
            _byClient.TryGetValue(attachClientId, out var current);
            if (operationReleased)
            {
                if (current is not null)
                    return false;
                foreach (var held in _byClient.Values)
                {
                    if (held.PaneId == operationPaneId)
                        return false;
                }

                if (priorOwner is null)
                    return true;
                _byClient[attachClientId] = priorOwner;
                return true;
            }

            if (current is null
                || current.PaneId != operationPaneId
                || current.Generation != operationGeneration
                || !string.Equals(current.AttachClientId, attachClientId, StringComparison.Ordinal))
            {
                return false;
            }

            if (priorOwner is null)
                _byClient.Remove(attachClientId);
            else
                _byClient[attachClientId] = priorOwner;
            return true;
        }
    }

    public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId) =>
        Check(mode, attachClientId, paneId: null);

    public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId, string? paneId)
    {
        if (!string.Equals(mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase))
            return Result<bool, PlacementAuthorityError>.Ok(true);

        if (string.IsNullOrWhiteSpace(attachClientId))
            return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.MissingAttachClient);

        lock (_gate)
        {
            if (!_byClient.TryGetValue(attachClientId.Trim(), out var existing))
                return Result<bool, PlacementAuthorityError>.Ok(true);
            if (!string.IsNullOrWhiteSpace(paneId)
                && string.Equals(existing.PaneId.Value, paneId, StringComparison.Ordinal))
            {
                return Result<bool, PlacementAuthorityError>.Ok(true);
            }

            return Result<bool, PlacementAuthorityError>.Fail(PlacementAuthorityError.OverlayBusy);
        }
    }

    private Result<PaneOverlayChange, PaneOverlayError> OkRelease(PaneOverlayOwner owner, PaneState pane)
    {
        _byClient.Remove(owner.AttachClientId);
        var generation = ++_generation;
        return Result<PaneOverlayChange, PaneOverlayError>.Ok(new PaneOverlayChange
        {
            Pane = pane,
            AttachClientId = owner.AttachClientId,
            Generation = generation,
            Changed = true,
            Mode = PaneOverlayWire.Hidden,
        });
    }

    private bool TryFindOwner(PaneId paneId, string? attachClientId, out PaneOverlayOwner? owner)
    {
        if (!string.IsNullOrWhiteSpace(attachClientId)
            && _byClient.TryGetValue(attachClientId, out var byClient)
            && byClient.PaneId == paneId)
        {
            owner = byClient;
            return true;
        }

        foreach (var item in _byClient.Values)
        {
            if (item.PaneId == paneId)
            {
                owner = item;
                return string.IsNullOrWhiteSpace(attachClientId);
            }
        }

        owner = null;
        return false;
    }

    private static (int Cols, int Rows) ResolveInner(int? areaCols, int? areaRows, PaneState pane)
    {
        if (areaCols is > 0 && areaRows is > 0)
        {
            var geometry = PopupGeometry.TryResolve(areaCols.Value, areaRows.Value);
            if (geometry is not null)
                return (geometry.InnerCols, geometry.InnerRows);
        }

        return (Math.Max(1, pane.Cols), Math.Max(1, pane.Rows));
    }

    private static PaneState MissingPane(string id) =>
        new()
        {
            Id = new PaneId(id),
            TabId = new TabId("tab_none"),
            WorkspaceId = new WorkspaceId("ws_none"),
            Placement = PanePlacement.Hidden,
        };
}

public static class PaneOverlayWire
{
    public const string Overlay = "overlay";
    public const string Hidden = "hidden";
}
