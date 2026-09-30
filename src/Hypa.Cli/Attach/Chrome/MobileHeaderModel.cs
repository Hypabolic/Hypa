using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Chrome;

public sealed record MobileHeaderModel(
    CellRect Rect,
    CellRect Status,
    CellRect Switch,
    string WorkspaceLabel,
    string TabStatus,
    string AgentSummary,
    string Line1,
    string Line2)
{
    public static MobileHeaderModel Build(
        int cols,
        int rows,
        string? workspaceLabel,
        string? tabLabel,
        int tabIndex,
        int tabCount,
        string? agentName,
        string? sessionName = null,
        string? paneLabel = null,
        string? paneId = null,
        string? agentState = null,
        StatusIndicatorStyle indicators = StatusIndicatorStyle.Dots)
    {
        cols = Math.Max(1, cols);
        var headerRows = NarrowLayout.HeaderRowsFor(rows);
        var rect = new CellRect(0, 0, cols, headerRows);
        var switchW = NarrowLayout.SwitchWidth(cols);
        var switchRect = new CellRect(cols - switchW, 0, switchW, headerRows);
        var statusCols = Math.Max(0, cols - switchW);
        var status = new CellRect(0, 0, statusCols, headerRows);
        var workspace = string.IsNullOrWhiteSpace(workspaceLabel) ? "workspace" : workspaceLabel.Trim();
        var occupant = OccupantName(agentName);
        var tab = TabStatusText(tabLabel, tabIndex, tabCount);
        var chips = StatusChipComposer.Compose(new StatusChipComposeInput
        {
            SessionName = sessionName,
            WorkspaceLabel = workspace,
            PaneLabel = paneLabel,
            PaneId = paneId,
            AgentName = occupant,
            AgentState = agentState,
            StatusIndicators = indicators,
            MaxCols = Math.Max(1, statusCols),
        });
        var session = StatusChipComposer.SessionText(sessionName);
        var nameBudget = Math.Max(1, statusCols - SafeDisplayText.Width(tab) - 1);
        var left = SafeDisplayText.Clip(session + " " + workspace, nameBudget);
        var line1 = left;
        if (statusCols > SafeDisplayText.Width(line1) + 1)
        {
            var pad = statusCols - SafeDisplayText.Width(line1) - SafeDisplayText.Width(tab);
            if (pad < 1)
                pad = 1;
            line1 = line1 + new string(' ', pad) + tab;
        }

        var line2 = "";
        if (headerRows > 1)
        {
            var pane = StatusChipComposer.PaneText(paneLabel, paneId);
            var agent = StatusChipComposer.AgentText(new StatusChipComposeInput
            {
                AgentName = occupant,
                AgentState = agentState,
                StatusIndicators = indicators,
            });
            var second = string.Join(' ', new[] { pane, agent }.Where(static s => s.Length > 0));
            if (second.Length == 0)
                second = chips.Text;
            line2 = SafeDisplayText.Clip(second, statusCols);
        }

        return new MobileHeaderModel(
            rect,
            status,
            switchRect,
            workspace,
            tab,
            occupant ?? "",
            line1,
            line2);
    }

    public MobileHeaderModel WithChips(StatusChipComposeInput chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        var occupant = OccupantName(chips.AgentName);
        var input = chips with { AgentName = occupant };
        var statusCols = Math.Max(0, Status.Cols);
        var workspace = string.IsNullOrWhiteSpace(input.WorkspaceLabel)
            ? WorkspaceLabel
            : input.WorkspaceLabel.Trim();
        var session = StatusChipComposer.SessionText(input.SessionName);
        var nameBudget = Math.Max(1, statusCols - SafeDisplayText.Width(TabStatus) - 1);
        var left = SafeDisplayText.Clip(session + " " + workspace, nameBudget);
        var line1 = left;
        if (statusCols > SafeDisplayText.Width(line1) + 1)
        {
            var pad = statusCols - SafeDisplayText.Width(line1) - SafeDisplayText.Width(TabStatus);
            if (pad < 1)
                pad = 1;
            line1 = line1 + new string(' ', pad) + TabStatus;
        }

        var line2 = "";
        if (Rect.Rows > 1)
        {
            var pane = StatusChipComposer.PaneText(input.PaneLabel, input.PaneId);
            var agent = StatusChipComposer.AgentText(input);
            var second = string.Join(' ', new[] { pane, agent }.Where(static s => s.Length > 0));
            if (second.Length == 0)
                second = StatusChipComposer.Compose(input with { MaxCols = Math.Max(1, statusCols) }).Text;
            line2 = SafeDisplayText.Clip(second, statusCols);
        }

        return this with
        {
            WorkspaceLabel = workspace,
            AgentSummary = occupant ?? "",
            Line1 = line1,
            Line2 = line2,
        };
    }

    private static string? OccupantName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public static string TabStatusText(string? tabLabel, int tabIndex, int tabCount)
    {
        var label = string.IsNullOrWhiteSpace(tabLabel)
            ? Math.Max(1, tabIndex + 1).ToString()
            : tabLabel.Trim();
        if (tabCount <= 1)
            return "tab " + label;
        return $"tab {label} · {Math.Max(1, tabIndex + 1)}/{tabCount}";
    }
}
