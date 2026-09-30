using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application.Sidebar;

public static class HiddenPaneActionPlanner
{
    public static Result<HiddenPaneActionPlan, HiddenPaneActionError> Plan(
        HiddenPaneActionRequest request,
        IReadOnlyList<HiddenPaneRecord> catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        HiddenPaneRecord? record = null;
        foreach (var item in catalog)
        {
            if (string.Equals(item.PaneId, request.PaneId, StringComparison.Ordinal))
            {
                record = item;
                break;
            }
        }

        if (record is null)
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.NotFound($"pane not found: {request.PaneId}"));

        return request.Action switch
        {
            HiddenPaneAction.ShowNewTab => PlanShowNewTab(request, record),
            HiddenPaneAction.SplitRight => PlanSplit(request, record, "right"),
            HiddenPaneAction.SplitBelow => PlanSplit(request, record, "down"),
            HiddenPaneAction.ShowModal => PlanModal(request, record),
            HiddenPaneAction.Hide => PlanHide(request, record),
            HiddenPaneAction.Close => PlanClose(request, record),
            _ => Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.InvalidAction("unknown action")),
        };
    }

    private static Result<HiddenPaneActionPlan, HiddenPaneActionError> PlanShowNewTab(
        HiddenPaneActionRequest request,
        HiddenPaneRecord record)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId))
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease("lease_id is required"));

        var workspaceId = request.WorkspaceId ?? record.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.NotFound("workspace_id is required"));
        }

        return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Ok(new HiddenPaneActionPlan
        {
            PaneId = record.PaneId,
            CreateEmptyTab = true,
            CleanupCreatedTabOnFailure = true,
            FocusCreatedTab = true,
            RestoreFocusOnFailure = true,
            PriorTabId = request.PriorTabId ?? request.CurrentTabId,
            PriorWorkspaceId = request.PriorWorkspaceId,
            Calls =
            [
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.TabCreate,
                    WorkspaceId = workspaceId,
                    CreatePane = false,
                },
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.PaneShow,
                    PaneId = record.PaneId,
                    Mode = PanePlacementWire.Tiled,
                    LeaseId = request.LeaseId,
                },
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.TabClose,
                    IsCleanup = true,
                },
            ],
        });
    }

    private static Result<HiddenPaneActionPlan, HiddenPaneActionError> PlanSplit(
        HiddenPaneActionRequest request,
        HiddenPaneRecord record,
        string direction)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId))
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease("lease_id is required"));

        var target = request.VisibleTargetPaneId;
        if (string.IsNullOrWhiteSpace(target))
        {
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.NoVisibleTarget("no visible pane in the current tab"));
        }

        return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Ok(new HiddenPaneActionPlan
        {
            PaneId = record.PaneId,
            Calls =
            [
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.PaneShow,
                    PaneId = record.PaneId,
                    TabId = request.CurrentTabId ?? record.TabId,
                    TargetPaneId = target,
                    Direction = direction,
                    Mode = PanePlacementWire.Tiled,
                    LeaseId = request.LeaseId,
                },
            ],
        });
    }

    private static Result<HiddenPaneActionPlan, HiddenPaneActionError> PlanModal(
        HiddenPaneActionRequest request,
        HiddenPaneRecord record)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId))
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease("lease_id is required"));
        if (string.IsNullOrWhiteSpace(request.AttachClientId))
        {
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingAttachClient("attach client id is required"));
        }

        return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Ok(new HiddenPaneActionPlan
        {
            PaneId = record.PaneId,
            Calls =
            [
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.PaneShow,
                    PaneId = record.PaneId,
                    Mode = "overlay",
                    LeaseId = request.LeaseId,
                    AttachClientId = request.AttachClientId,
                },
            ],
        });
    }

    private static Result<HiddenPaneActionPlan, HiddenPaneActionError> PlanHide(
        HiddenPaneActionRequest request,
        HiddenPaneRecord record)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId))
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease("lease_id is required"));
        if (record.Hidden && !request.OverlayVisible)
        {
            return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.InvalidAction("pane is already hidden"));
        }

        return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Ok(new HiddenPaneActionPlan
        {
            PaneId = record.PaneId,
            Calls =
            [
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.PaneHide,
                    PaneId = record.PaneId,
                    LeaseId = request.LeaseId,
                    AttachClientId = request.OverlayVisible ? request.AttachClientId : null,
                },
            ],
        });
    }

    private static Result<HiddenPaneActionPlan, HiddenPaneActionError> PlanClose(
        HiddenPaneActionRequest request,
        HiddenPaneRecord record)
    {
        _ = request;
        return Result<HiddenPaneActionPlan, HiddenPaneActionError>.Ok(new HiddenPaneActionPlan
        {
            PaneId = record.PaneId,
            ConfirmClose = true,
            Calls =
            [
                new HiddenPanePlannedCall
                {
                    Method = ProtocolMethods.PaneClose,
                    PaneId = record.PaneId,
                },
            ],
        });
    }
}
