using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;

namespace Hypa.Cli.Attach.Keys;

/// <summary>
/// One row for Prefix, Navigate, Copy, Resize, and Terminal+error.
/// tab row only when the desktop tab bar is at the bottom.
/// </summary>
public sealed record ModeBarModel(
    bool Visible,
    AttachClientMode Mode,
    ModeBarSlot Slot,
    string Text)
{
    public const string TabRowPort = "tab_row";

    public static ModeBarSlot DefaultSlot { get; } = ModeBarSlot.Top;

    public static bool IsChromeMode(AttachClientMode mode) => KeyEngine.IsChrome(mode);

    public static bool ReplacesTabRow(AttachClientMode mode) =>
        ReplacesTabRow(mode, DefaultSlot);

    public static bool ReplacesTabRow(AttachClientMode mode, ModeBarSlot slot) =>
        ReplacesTabRow(mode, slot, hasEndpointError: false);

    public static bool ReplacesTabRow(
        AttachClientMode mode,
        ModeBarSlot slot,
        bool hasEndpointError)
    {
        if (slot is not ModeBarSlot.Bottom)
            return false;
        return ShouldPaint(mode, hasEndpointError);
    }

    public static bool ShouldPaint(AttachClientMode mode, bool hasEndpointError) =>
        IsChromeMode(mode)
        || (mode is AttachClientMode.Terminal && hasEndpointError);

    /// <summary>
    /// <c>src/client/shell/render.rs</c> 45-50. Bottom desktop tabs use the
    /// tab row. Top tabs and a hidden bar use the last pane row.
    /// </summary>
    public static CellRect OverlayRow(LayoutChromeGeometry? chrome, int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (chrome is { IsNarrow: true, TabRow: { Rows: > 0 } reserved })
            return reserved;
        if (chrome is
            {
                IsNarrow: false,
                TabBarVisible: true,
                TabSlot: ModeBarSlot.Bottom,
                TabRow: { Rows: > 0 } tab,
            })
        {
            return tab;
        }

        if (chrome is { Content.Rows: > 0, Content.Cols: > 0 } desktop)
        {
            return new CellRect(
                desktop.Content.Col,
                desktop.Content.EndRow - 1,
                desktop.Content.Cols,
                1);
        }

        return new CellRect(0, rows - 1, cols, 1);
    }

    public static ModeBarSlot SlotFor(TabBarPosition? position) =>
        position is TabBarPosition.Bottom ? ModeBarSlot.Bottom : ModeBarSlot.Top;

    public static ModeBarModel For(
        AttachClientMode mode,
        KeyBindingTable? table = null,
        ModeBarSlot slot = ModeBarSlot.Bottom,
        CopyModeSession? copy = null)
    {
        if (!IsChromeMode(mode))
            return new ModeBarModel(false, mode, slot, "");
        var text = mode is AttachClientMode.Copy
            ? FormatCopy(table, copy)
            : Format(mode, table);
        return new ModeBarModel(true, mode, slot, text);
    }

    public static string Format(AttachClientMode mode, KeyBindingTable? table)
    {
        var prefix = table?.PrefixChord.Format() ?? "ctrl+b";
        return mode switch
        {
            AttachClientMode.Prefix =>
                $"PREFIX ({prefix})  q detach  ? help  c new-tab  w workspaces  space navigate  [ copy  r resize  {KeybindHelpModel.EqualPathsLine}",
            AttachClientMode.Navigate =>
                "NAVIGATE  h/j/k/l panes  arrows panes/workspaces  esc leave",
            AttachClientMode.Copy =>
                FormatCopy(table, copy: null),
            AttachClientMode.Resize =>
                "RESIZE  h/j/k/l or arrows  esc or prefix+r leave",
            _ => "",
        };
    }

    public static string FormatCopy(KeyBindingTable? table, CopyModeSession? copy)
    {
        var prefix = table?.PrefixChord.Format() ?? "ctrl+b";
        var parts = new List<string> { "COPY" };
        if (copy is { IsAltScreen: true })
            parts.Add("visible-grid-only");
        if (copy is { HasNewOutput: true })
            parts.Add("new-output");
        if (copy is { SearchPromptActive: true })
            parts.Add((copy.SearchForward ? "/" : "?") + copy.SearchQuery);
        parts.Add($"prefix still works ({prefix} is not page-up)");
        parts.Add("q leave");
        parts.Add("esc clear");
        return string.Join("  ", parts);
    }
}
