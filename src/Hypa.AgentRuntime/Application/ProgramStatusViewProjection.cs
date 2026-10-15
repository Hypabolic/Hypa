using System.Globalization;
using System.Text;
using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Pure projection from program status records to the views that frontends show.
/// The attach, the CLI, and any other frontend read the result from the protocol.
/// They do not repeat the roll-up or the text rules.
/// </summary>
public static class ProgramStatusViewProjection
{
    /// <summary>
    /// The view of one pane. <paramref name="records"/> must have <c>app</c> resolved
    /// (<see cref="ProgramStatusStore.Resolved"/>). Returns null when there are no records.
    /// <paramref name="changedAt"/> is when the records last changed. It only breaks ties between panes.
    /// <paramref name="seen"/> is the seen state of the pane. The view keeps it only for a done or error top state.
    /// </summary>
    public static ProgramStatusView? ForPane(
        IReadOnlyList<ProgramStatusRecord> records,
        ProgramStatusSourceLabel source,
        DateTimeOffset? changedAt = null,
        bool seen = false)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(source);
        var top = ProgramStatusRollup.Top(records);
        if (top is null)
            return null;

        return new ProgramStatusView
        {
            State = top.State,
            Kind = top.Kind,
            Progress = top.Progress,
            App = Sanitize(top.App),
            Title = Sanitize(top.Title),
            Msg = Sanitize(top.Msg),
            RecordCount = records.Count,
            Source = new ProgramStatusSourceLabel(
                Sanitize(source.Workspace) ?? "",
                Sanitize(source.Tab) ?? "",
                Sanitize(source.Pane) ?? ""),
            Seen = seen && top.State is ProgramStatusState.Done or ProgramStatusState.Error,
            ChangedAt = changedAt,
        };
    }

    /// <summary>
    /// The view of one tab: the most urgent state of its panes.
    /// A pane with a seen done or error does not count (<see cref="ProgramStatusView.CountsForSummary"/>).
    /// The most recent change wins a tie (<see cref="ProgramStatusRollup.Outranks"/>). When the times are
    /// equal or unknown, the first pane wins, so the caller sorts the panes. Returns null when no pane has a view.
    /// </summary>
    public static TabProgramStatusView? ForTab(string tabId, IEnumerable<ProgramStatusView> paneViews)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        ArgumentNullException.ThrowIfNull(paneViews);
        ProgramStatusView? top = null;
        foreach (var view in paneViews)
        {
            if (!view.CountsForSummary)
                continue;
            if (top is null || Outranks(view, top))
                top = view;
        }

        return top is null
            ? null
            : new TabProgramStatusView
            {
                TabId = tabId,
                State = top.State,
                Progress = top.State is ProgramStatusState.Working ? top.Progress : null,
            };
    }

    private static bool Outranks(ProgramStatusView view, ProgramStatusView other) =>
        ProgramStatusRollup.Outranks(view.State, view.ChangedAt, other.State, other.ChangedAt);

    /// <summary>Longest session message in terminal columns.</summary>
    public const int SessionMsgMaxCols = 200;

    /// <summary>
    /// The view of the session: the most urgent pane, by the urgency order of the tab view.
    /// A pane with a seen done or error does not count, as in the tab view.
    /// The most recent change wins a tie (<see cref="ProgramStatusRollup.Outranks"/>). When the times are
    /// equal or unknown, the first pane wins, so the caller sorts the panes. Returns null when no pane has a view.
    /// The message names the pane, because text that a frontend shows outside the pane
    /// must say which pane it came from.
    /// </summary>
    public static SessionProgramStatusView? ForSession(
        IEnumerable<(string PaneId, ProgramStatusView View)> paneViews)
    {
        ArgumentNullException.ThrowIfNull(paneViews);
        (string PaneId, ProgramStatusView View)? top = null;
        foreach (var pane in paneViews)
        {
            if (!pane.View.CountsForSummary)
                continue;
            if (top is null || Outranks(pane.View, top.Value.View))
                top = pane;
        }

        if (top is not { } best)
            return null;

        var view = best.View;
        var label = string.IsNullOrEmpty(view.Source.Pane) ? best.PaneId : view.Source.Pane;
        var text = string.IsNullOrEmpty(view.Msg) ? label : label + ": " + view.Msg;
        return new SessionProgramStatusView
        {
            State = view.State,
            Kind = view.State is ProgramStatusState.Blocked ? view.Kind : null,
            Progress = view.State is ProgramStatusState.Working ? view.Progress : null,
            App = view.App,
            PaneId = best.PaneId,
            Msg = SafeDisplayText.Clip(Sanitize(text), SessionMsgMaxCols),
            Source = view.Source,
        };
    }

    /// <summary>
    /// Safe display text. Removes text-direction overrides and isolates and every other
    /// invisible format character (Unicode category Cf), and the terminal control characters.
    /// Returns null when nothing is left.
    /// </summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format)
                continue;
            sb.Append(rune.ToString());
        }

        var safe = SafeDisplayText.Encode(sb.ToString());
        return safe.Length == 0 ? null : safe;
    }
}
