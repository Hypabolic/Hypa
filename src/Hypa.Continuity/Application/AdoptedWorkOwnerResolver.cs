using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Live pane used to find the adopted occupant. A tab or workspace
/// must not guess. WorkspaceId scopes Spaces row Transfer.
/// </summary>
public sealed record OccupantPaneSnapshot
{
    public required string PaneId { get; init; }
    public string? TabId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? WorkId { get; init; }
    public string? Cwd { get; init; }
    public string? Home { get; init; }
    public string? AgentStatus { get; init; }
}

/// <summary>Pane that holds the adopted occupant for Move Work.</summary>
public sealed record AdoptedWorkOwner
{
    public required WorkId WorkId { get; init; }
    public required string HarnessAdapterId { get; init; }
    public required RunRecord Run { get; init; }
    public required string PaneId { get; init; }
    public required bool OccupantStreaming { get; init; }
}

/// <summary>Result of adopted-occupant lookup. Missing Work fails closed.</summary>
public sealed record AdoptedWorkOwnerResult
{
    public required bool Ok { get; init; }
    public AdoptedWorkOwner? Owner { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    public static AdoptedWorkOwnerResult Pass(AdoptedWorkOwner owner) =>
        new() { Ok = true, Owner = owner };

    public static AdoptedWorkOwnerResult Fail(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}

/// <summary>
/// Finds the pane that holds the adopted occupant. A tab or workspace
/// with many panes does not guess.
/// </summary>
public static class AdoptedWorkOwnerResolver
{
    public const string AdoptRequiredDetail =
        "no Work is adopted. Adopt the occupant first.";

    public const string PaneNotOwnerDetail =
        "this pane does not hold adopted Work. Adopt the occupant first.";

    public const string MultiplePanesDetail =
        "multiple panes hold adopted Work. A tab must not guess.";

    public const string MultipleWorkspacePanesDetail =
        "multiple panes hold adopted Work. Use pane Transfer.";

    public static AdoptedWorkOwnerResult Resolve(
        IReadOnlyList<ActiveWorkRun> active,
        IReadOnlyList<OccupantPaneSnapshot> panes,
        string muxEndpoint,
        string? requestedPaneId,
        string? requestedWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(panes);

        IReadOnlyList<OccupantPaneSnapshot> scoped = panes;
        if (!string.IsNullOrWhiteSpace(requestedWorkspaceId))
        {
            scoped = panes
                .Where(pane => string.Equals(
                    pane.WorkspaceId,
                    requestedWorkspaceId,
                    StringComparison.Ordinal))
                .ToList();
        }

        var onMux = new List<(ActiveWorkRun Work, OccupantPaneSnapshot Pane)>();
        foreach (var work in active)
        {
            if (work.Run.Status != RunStatus.Active)
                continue;
            if (!SameMux(work.Run.MuxEndpoint, muxEndpoint))
                continue;

            foreach (var pane in scoped)
            {
                if (HoldsAdoptedOccupant(pane, work))
                    onMux.Add((work, pane));
            }
        }

        if (onMux.Count == 0)
        {
            return AdoptedWorkOwnerResult.Fail(
                ContinuityReasons.WorkNotAdopted,
                AdoptRequiredDetail);
        }

        if (!string.IsNullOrWhiteSpace(requestedPaneId))
        {
            var forPane = onMux
                .Where(row => string.Equals(row.Pane.PaneId, requestedPaneId, StringComparison.Ordinal))
                .ToList();
            if (forPane.Count == 0)
            {
                return AdoptedWorkOwnerResult.Fail(
                    ContinuityReasons.WorkNotAdopted,
                    PaneNotOwnerDetail);
            }

            if (forPane.Select(row => row.Work.Work.Id.Value).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                return AdoptedWorkOwnerResult.Fail(
                    ContinuityReasons.WorkNotAdopted,
                    MultiplePanesDetail);
            }

            return Pass(forPane[0]);
        }

        var paneIds = onMux.Select(row => row.Pane.PaneId).Distinct(StringComparer.Ordinal).ToList();
        var workIds = onMux.Select(row => row.Work.Work.Id.Value).Distinct(StringComparer.Ordinal).ToList();
        if (paneIds.Count != 1 || workIds.Count != 1)
        {
            return AdoptedWorkOwnerResult.Fail(
                ContinuityReasons.WorkNotAdopted,
                !string.IsNullOrWhiteSpace(requestedWorkspaceId)
                    ? MultipleWorkspacePanesDetail
                    : MultiplePanesDetail);
        }

        return Pass(onMux[0]);
    }

    public static IReadOnlyList<string> AdoptedPaneIdsInWorkspace(
        IReadOnlyList<OccupantPaneSnapshot> panes,
        string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(panes);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return [];

        return panes
            .Where(pane =>
                string.Equals(pane.WorkspaceId, workspaceId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(pane.WorkId)
                && !string.IsNullOrWhiteSpace(pane.PaneId))
            .Select(pane => pane.PaneId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Work id for the selected pane or unique workspace owner.
    /// Snapshot order is not a guess.
    /// </summary>
    public static string? WorkIdForOwner(
        IReadOnlyList<OccupantPaneSnapshot> panes,
        string? paneId,
        string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(panes);

        var selectedPaneId = paneId;
        if (string.IsNullOrWhiteSpace(selectedPaneId)
            && !string.IsNullOrWhiteSpace(workspaceId))
        {
            var adopted = AdoptedPaneIdsInWorkspace(panes, workspaceId);
            if (adopted.Count != 1)
                return null;
            selectedPaneId = adopted[0];
        }

        if (string.IsNullOrWhiteSpace(selectedPaneId))
            return null;

        foreach (var pane in panes)
        {
            if (!string.Equals(pane.PaneId, selectedPaneId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(workspaceId)
                && !string.Equals(pane.WorkspaceId, workspaceId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(pane.WorkId))
                return pane.WorkId;
        }

        return null;
    }

    private static AdoptedWorkOwnerResult Pass((ActiveWorkRun Work, OccupantPaneSnapshot Pane) row)
    {
        var status = row.Pane.AgentStatus ?? "";
        var streaming = string.Equals(status, "working", StringComparison.OrdinalIgnoreCase);
        return AdoptedWorkOwnerResult.Pass(new AdoptedWorkOwner
        {
            WorkId = row.Work.Work.Id,
            HarnessAdapterId = row.Work.Work.HarnessAdapterId,
            Run = row.Work.Run,
            PaneId = row.Pane.PaneId,
            OccupantStreaming = streaming,
        });
    }

    private static bool HoldsAdoptedOccupant(OccupantPaneSnapshot pane, ActiveWorkRun work)
    {
        if (!string.IsNullOrWhiteSpace(pane.WorkId))
        {
            return string.Equals(pane.WorkId, work.Work.Id.Value, StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(work.Run.Cwd) || string.IsNullOrWhiteSpace(pane.Cwd))
            return false;
        if (!SamePath(work.Run.Cwd, pane.Cwd))
            return false;
        if (!string.IsNullOrWhiteSpace(work.Run.Home))
        {
            if (string.IsNullOrWhiteSpace(pane.Home))
                return false;
            return SamePath(work.Run.Home, pane.Home);
        }

        return true;
    }

    internal static bool SameMux(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(NormalizeMux(left), NormalizeMux(right), StringComparison.Ordinal);
    }

    private static string NormalizeMux(string value)
    {
        var trimmed = value.Trim();
        const string unix = "unix:";
        if (trimmed.StartsWith(unix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[unix.Length..];
        return NormalizePath(trimmed);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);

    private static string NormalizePath(string value)
    {
        var trimmed = value.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return trimmed;
        }
    }
}
