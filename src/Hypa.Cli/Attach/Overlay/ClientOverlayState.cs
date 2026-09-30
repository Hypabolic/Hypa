using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Chrome;

namespace Hypa.Cli.Attach.Overlay;

/// <summary>
/// Per-client overlay ownership. A second attach client keeps its own
/// rectangle and must not inherit this state.
/// </summary>
internal sealed class ClientOverlayState
{
    public string? PaneId { get; private set; }

    public long Generation { get; private set; }

    public long AppliedGeneration { get; private set; }

    public PopupGeometryResult? Geometry { get; private set; }

    public bool OwnsModal => !string.IsNullOrWhiteSpace(PaneId);

    public bool ResizeBeforeFull { get; private set; }

    public bool RevealAccepted { get; private set; }

    public int RevealOccupant { get; private set; }

    public long RevealGeneration { get; private set; }

    public long RevealBarrierGeneration { get; private set; }

    public string? RecoverableCancelPaneId { get; private set; }

    public long RecoverableCancelGeneration { get; private set; }

    public bool HasRecoverableCancel => !string.IsNullOrWhiteSpace(RecoverableCancelPaneId);

    public bool ApplyServerShow(string paneId, long generation, PopupGeometryResult geometry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(geometry);
        if (generation <= AppliedGeneration)
            return false;
        PaneId = paneId;
        Generation = generation;
        AppliedGeneration = generation;
        Geometry = geometry;
        ResizeBeforeFull = false;
        RevealBarrierGeneration = 0;
        ClearRecoverableCancel();
        ClearReveal();
        return true;
    }

    public bool ApplyServerHide(string? paneId, long generation)
    {
        if (generation < AppliedGeneration)
            return false;
        ForgetRecoverableCancel(paneId, generation);
        if (!OwnsModal)
        {
            AppliedGeneration = Math.Max(AppliedGeneration, generation);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(paneId)
            && !string.Equals(PaneId, paneId, StringComparison.Ordinal))
        {
            return false;
        }

        ClearOwned();
        AppliedGeneration = Math.Max(AppliedGeneration, generation);
        return true;
    }

    public bool AcknowledgeLocalHide()
    {
        if (!OwnsModal)
            return false;
        ClearOwned();
        return true;
    }

    public void ResetConnectionFence()
    {
        ClearOwned();
        ClearRecoverableCancel();
        Generation = 0;
        AppliedGeneration = 0;
        RevealBarrierGeneration = 0;
    }

    public void NoteRecoverableCancel(string paneId, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        RecoverableCancelPaneId = paneId;
        RecoverableCancelGeneration = generation;
    }

    public bool MatchesRecoverableCancel(string? paneId, long generation) =>
        HasRecoverableCancel
        && string.Equals(RecoverableCancelPaneId, paneId, StringComparison.Ordinal)
        && RecoverableCancelGeneration == generation;

    public void NoteResized(long cachedGeneration = 0)
    {
        ResizeBeforeFull = true;
        RevealBarrierGeneration = Math.Max(0, cachedGeneration);
        ClearReveal();
    }

    public void UpdateGeometry(PopupGeometryResult geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (Geometry is { } current
            && current.InnerCols == geometry.InnerCols
            && current.InnerRows == geometry.InnerRows
            && current.OuterCol == geometry.OuterCol
            && current.OuterRow == geometry.OuterRow)
        {
            Geometry = geometry;
            return;
        }

        Geometry = geometry;
        ResizeBeforeFull = false;
        RevealBarrierGeneration = 0;
        ClearReveal();
    }

    public bool TryAcceptReveal(AssembledSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OwnsModal || !ResizeBeforeFull || !snapshot.IngestFull || Geometry is not { } geo)
            return false;
        if (!string.Equals(snapshot.PaneId, PaneId, StringComparison.Ordinal))
            return false;
        if (snapshot.Cols != geo.InnerCols || snapshot.Rows != geo.InnerRows)
            return false;
        if (RevealBarrierGeneration > 0
            && snapshot.Generation > 0
            && snapshot.Generation <= RevealBarrierGeneration)
        {
            return false;
        }

        if (RevealAccepted)
        {
            if (RevealOccupant > 0
                && snapshot.OccupantGeneration > 0
                && snapshot.OccupantGeneration != RevealOccupant)
            {
                return false;
            }

            if (RevealGeneration > 0
                && snapshot.Generation > 0
                && snapshot.Generation < RevealGeneration)
            {
                return false;
            }
        }

        RevealAccepted = true;
        RevealOccupant = snapshot.OccupantGeneration;
        RevealGeneration = snapshot.Generation;
        return true;
    }

    public bool CanStamp(AssembledSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OwnsModal || !RevealAccepted || Geometry is not { } geo)
            return false;
        if (!string.Equals(snapshot.PaneId, PaneId, StringComparison.Ordinal))
            return false;
        if (snapshot.Cols != geo.InnerCols || snapshot.Rows != geo.InnerRows)
            return false;
        if (RevealOccupant > 0
            && snapshot.OccupantGeneration > 0
            && snapshot.OccupantGeneration != RevealOccupant)
        {
            return false;
        }

        if (RevealGeneration > 0
            && snapshot.Generation > 0
            && snapshot.Generation < RevealGeneration)
        {
            return false;
        }

        return true;
    }

    public bool Matches(string? attachClientId, string? eventClientId) =>
        string.IsNullOrWhiteSpace(eventClientId)
        || string.Equals(attachClientId, eventClientId, StringComparison.Ordinal);

    public bool Admit(long generation) =>
        OwnsModal && generation == Generation;

    public PopupChromeFrame? Frame()
    {
        if (Geometry is not { } geo)
            return null;
        return new PopupChromeFrame(
            new CellRect(geo.OuterCol, geo.OuterRow, geo.OuterCols, geo.OuterRows),
            new CellRect(geo.InnerCol, geo.InnerRow, geo.InnerCols, geo.InnerRows));
    }

    public bool Contains(int col, int row) =>
        Frame()?.Outer.Contains(col, row) == true;

    public bool ContainsInner(int col, int row) =>
        Frame()?.Inner.Contains(col, row) == true;

    private void ClearOwned()
    {
        PaneId = null;
        Geometry = null;
        ResizeBeforeFull = false;
        RevealBarrierGeneration = 0;
        ClearReveal();
    }

    private void ClearReveal()
    {
        RevealAccepted = false;
        RevealOccupant = 0;
        RevealGeneration = 0;
    }

    private void ForgetRecoverableCancel(string? paneId, long generation)
    {
        if (!HasRecoverableCancel)
            return;
        if (!string.IsNullOrWhiteSpace(paneId)
            && !string.Equals(RecoverableCancelPaneId, paneId, StringComparison.Ordinal))
        {
            return;
        }

        if (generation < RecoverableCancelGeneration)
            return;
        ClearRecoverableCancel();
    }

    private void ClearRecoverableCancel()
    {
        RecoverableCancelPaneId = null;
        RecoverableCancelGeneration = 0;
    }
}
