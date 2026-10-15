using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Formats a program status view as plain chrome text.
/// The view text is already safe display text. This class only joins and shortens it.
/// Record text is never a token name and never markup.
/// </summary>
public static class SidebarProgramStatusText
{
    /// <summary>
    /// Token id of the status row. A program cannot name it: the row is not in the token grammar.
    /// </summary>
    public const string TokenId = "status";

    private const string Ellipsis = "…";

    /// <summary>
    /// One line: state, app, kind, progress, then the message shortened to fit.
    /// The app is left out when the row already shows the same name (<paramref name="shownName"/>).
    /// </summary>
    public static string Line(ProgramStatusView view, int maxCols, string? shownName = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (maxCols <= 0)
            return "";

        var head = new List<string> { view.State.ToWire() };
        if (!string.IsNullOrEmpty(view.App)
            && !string.Equals(view.App, shownName, StringComparison.OrdinalIgnoreCase))
            head.Add(view.App);
        if (view.Kind is { } kind)
            head.Add(kind.ToWire());
        if (view.Progress is { } progress)
            head.Add(Percent(progress));

        var line = string.Join(' ', head);
        if (string.IsNullOrEmpty(view.Msg))
            return SafeDisplayText.Clip(line, maxCols);

        var room = maxCols - SafeDisplayText.Width(line) - 1;
        if (room <= 0)
            return SafeDisplayText.Clip(line, maxCols);

        var msg = view.Msg;
        if (SafeDisplayText.Width(msg) > room)
        {
            msg = room == 1
                ? Ellipsis
                : SafeDisplayText.Clip(msg, room - 1) + Ellipsis;
        }

        return line + " " + msg;
    }

    /// <summary>
    /// A short mark for a tab that is not in view: the state icon,
    /// and the percentage for a working tab that reports progress.
    /// </summary>
    public static string TabBadge(TabProgramStatusView view, StatusIndicatorStyle style)
    {
        ArgumentNullException.ThrowIfNull(view);
        var icon = SidebarTokenGrammar.StateIcon(view.State.ToWire(), style);
        return view is { State: ProgramStatusState.Working, Progress: { } progress }
            ? icon + " " + Percent(progress)
            : icon;
    }

    private static string Percent(int progress) =>
        progress.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}
