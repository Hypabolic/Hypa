using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;
using Hypa.Connectivity.Domain;

namespace Hypa.Cli.Attach.Cubes;

public static class CubePairingDialogPainter
{
    public const string ShareTitle = "share this cube";
    public const string AddTitle = "add cube";
    public const string StartLabel = " ↵ start ";
    public const string StopLabel = " ↵ stop ";
    public const string RedeemLabel = " ↵ redeem ";
    public const string CopyLabel = " copy ";
    public const string CopiedLabel = " copied ";
    public const string CancelLabel = " esc cancel ";
    public const string AdvancedLabel = " advanced ";
    public const string SimpleLabel = " simple ";
    public const string CodeHint = "full hypa-invite: code · drag to select";
    public const string StartHint = "start to show a share code";
    public const string ReachPrefix = "peers dial ";
    public const string AllAddressesText = "all addresses";
    public const string LimitedReachText = "reach is this address";
    public const int ReachPageRows = 5;
    public const string PagingHint = "PageUp and PageDown show more";
    public const string PairedLabel = "paired";
    public const string PairedHint = "this cube is paired";

    public static CubePairingDialogLayout Measure(CubePairingDialogModel model, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var width = Math.Min(72, Math.Max(24, cols - 4));
        var inner = Math.Max(1, width - 4);
        var paired = model.Kind is CubePairingDialogKind.Share && model.Share?.Paired == true;
        var inviteLines = paired ? 0 : InviteLineCount(InviteText(model), inner);
        var errorLines = paired ? 0 : InviteLineCount(model.Share?.Error ?? "", inner);
        var advanced = !paired && model.Kind is CubePairingDialogKind.Share && model.Share?.Advanced == true;
        var extra = ReachExtraRows(model.Share, advanced, paired);
        var shareChrome = paired ? 7 : advanced ? 11 + extra : 8 + extra;
        if (!paired && errorLines > 1)
            shareChrome += errorLines - 1;
        var height = model.Kind is CubePairingDialogKind.Share
            ? shareChrome + (inviteLines == 0 ? 0 : inviteLines + 1)
            : 10 + (inviteLines == 0 ? 0 : Math.Min(inviteLines, 4));
        height = Math.Min(height, Math.Max(6, rows - 2));
        var col = Math.Max(0, (cols - width) / 2);
        var row = Math.Max(0, (rows - height) / 2);
        var panel = new CellRect(col, row, width, height);
        var primary = paired
            ? default
            : new CellRect(col + 2, panel.EndRow - 2, 14, 1);
        var showCopy = !paired
            && model.Kind is CubePairingDialogKind.Share
            && !string.IsNullOrWhiteSpace(model.Share?.Invite);
        var copy = showCopy
            ? new CellRect(col + 17, panel.EndRow - 2, 12, 1)
            : default;
        var cancel = paired
            ? new CellRect(col + 2, panel.EndRow - 2, 14, 1)
            : new CellRect(col + 30, panel.EndRow - 2, 14, 1);
        var advancedHit = !paired && model.Kind is CubePairingDialogKind.Share
            ? new CellRect(col + 45, panel.EndRow - 2, 14, 1)
            : default;
        var inviteRow = panel.Row + (advanced ? 6 + extra : 4 + extra);
        var inviteRows = inviteLines == 0
            ? 0
            : Math.Max(0, Math.Min(inviteLines, panel.EndRow - 3 - inviteRow));
        var invite = inviteRows == 0
            ? default
            : new CellRect(col + 2, inviteRow, inner, inviteRows);
        return new CubePairingDialogLayout(panel, primary, copy, cancel, advancedHit, invite);
    }

    internal static void Stamp(
        IHostCellSink sink,
        CubePairingDialogModel model,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsOpen)
            return;
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        if (layout.Panel.Cols <= 0 || layout.Panel.Rows <= 0)
            return;
        for (var r = layout.Panel.Row; r < layout.Panel.EndRow; r++)
            Write(sink, model, layout.Panel, layout.Panel.Col, r, new string(' ', layout.Panel.Cols), layout.Panel.Cols, theme.Text, theme.PanelBg);

        var title = model.Kind is CubePairingDialogKind.Share ? ShareTitle : AddTitle;
        Write(sink, model, layout.Panel, layout.Panel.Col + 2, layout.Panel.Row + 1, title, layout.Panel.Cols - 4, theme.Text, theme.PanelBg, bold: true);
        if (model.Kind is CubePairingDialogKind.Share && model.Share is { } share)
            StampShare(sink, model, share, layout, theme);
        else if (model.Kind is CubePairingDialogKind.Add && model.Add is { } add)
            StampAdd(sink, model, add, layout, theme);

        var paired = model.Kind is CubePairingDialogKind.Share && model.Share?.Paired == true;
        if (!paired)
        {
            var primary = model.Kind is CubePairingDialogKind.Share
                ? (model.Share?.Running == true ? StopLabel : StartLabel)
                : RedeemLabel;
            Write(sink, model, layout.Panel, layout.Primary.Col, layout.Primary.Row, primary, layout.Primary.Cols, theme.Text, theme.Surface1);
            if (model.Kind is CubePairingDialogKind.Share && !string.IsNullOrWhiteSpace(model.Share?.Invite))
            {
                var copy = model.Share!.Copied ? CopiedLabel : CopyLabel;
                Write(sink, model, layout.Panel, layout.Copy.Col, layout.Copy.Row, copy, layout.Copy.Cols, theme.Text, theme.Surface1);
            }
        }

        Write(sink, model, layout.Panel, layout.Cancel.Col, layout.Cancel.Row, CancelLabel, layout.Cancel.Cols, theme.Subtext0, theme.Surface0);
        if (!paired && model.Kind is CubePairingDialogKind.Share && layout.Advanced.Cols > 0)
        {
            var advanced = model.Share?.Advanced == true ? SimpleLabel : AdvancedLabel;
            Write(sink, model, layout.Panel, layout.Advanced.Col, layout.Advanced.Row, advanced, layout.Advanced.Cols, theme.Subtext0, theme.Surface0);
        }
    }

    public static string Paint(CubePairingDialogModel model, int cols, int rows, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var host = new HostFrame();
        host.Resize(Math.Max(1, cols), Math.Max(1, rows));
        Stamp(new HostFrameCellSink(host), model, cols, rows, theme);
        var sb = new StringBuilder();
        for (var r = 0; r < host.Rows; r++)
        {
            for (var c = 0; c < host.Cols; c++)
                sb.Append(host.CellAt(c, r).Text);
            if (r + 1 < host.Rows)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    internal static IReadOnlyList<string> ReachLines(CubeShareState share)
    {
        ArgumentNullException.ThrowIfNull(share);
        var portText = string.IsNullOrWhiteSpace(share.Port) ? CubeShareState.DefaultPort : share.Port.Trim();
        var hosts = ReachHosts(share);
        var lines = new List<string>(hosts.Count);
        foreach (var host in hosts)
            lines.Add(ReachPrefix + FormatReachHost(host) + ":" + portText);
        return lines;
    }

    /// <summary>
    /// Paint the address lines above the primary button. When the list does
    /// not fit, the last row names the range and the paging keys. The person
    /// scrolls with PageUp and PageDown.
    /// </summary>
    private static int WriteReachLines(
        IHostCellSink sink,
        CubePairingDialogModel model,
        CubePairingDialogLayout layout,
        int col,
        int body,
        int width,
        CubeShareState share,
        IReadOnlyList<string> lines,
        ThemePalette theme)
    {
        var rows = Math.Max(0, layout.Primary.Row - body);
        if (lines.Count <= rows)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                Write(sink, model, layout.Panel, col, body, lines[i], width, theme.Subtext0, theme.PanelBg);
                body++;
            }

            return body;
        }

        var page = Math.Max(1, rows - 1);
        var first = Math.Clamp(share.ReachScroll, 0, Math.Max(0, lines.Count - page));
        share.ReachScroll = first;
        var last = Math.Min(lines.Count, first + page);
        for (var i = first; i < last; i++)
        {
            Write(sink, model, layout.Panel, col, body, lines[i], width, theme.Subtext0, theme.PanelBg);
            body++;
        }

        if (body < layout.Primary.Row)
        {
            Write(
                sink,
                model,
                layout.Panel,
                col,
                body,
                (first + 1) + "-" + last + " of " + lines.Count + ". " + PagingHint,
                width,
                theme.Subtext0,
                theme.PanelBg);
            body++;
        }

        return body;
    }

    internal static int InviteLineCount(string invite, int width)
    {
        if (string.IsNullOrEmpty(invite) || width <= 0)
            return 0;
        return (invite.Length + width - 1) / width;
    }

    private static void StampShare(
        IHostCellSink sink,
        CubePairingDialogModel model,
        CubeShareState share,
        CubePairingDialogLayout layout,
        ThemePalette theme)
    {
        var col = layout.Panel.Col + 2;
        var width = layout.Panel.Cols - 4;
        var body = layout.Panel.Row + 3;
        if (share.Paired)
        {
            Write(sink, model, layout.Panel, col, body, PairedLabel, width, theme.Teal, theme.PanelBg, bold: true);
            if (body + 1 < layout.Cancel.Row)
                Write(sink, model, layout.Panel, col, body + 1, PairedHint, width, theme.Subtext0, theme.PanelBg);
            return;
        }

        if (share.Advanced)
        {
            var hostText = string.IsNullOrWhiteSpace(share.AdvertiseHost)
                ? AllAddressesText
                : share.AdvertiseHost;
            Write(sink, model, layout.Panel, col, body, "bind: " + share.BindHost, width, FieldFg(share.Focus == 0, theme), theme.PanelBg);
            Write(sink, model, layout.Panel, col, body + 1, "host: " + hostText, width, FieldFg(share.Focus == 1, theme), theme.PanelBg);
            Write(sink, model, layout.Panel, col, body + 2, "port: " + share.Port, width, FieldFg(share.Focus == 2, theme), theme.PanelBg);
            body += 3;
            if (!string.IsNullOrWhiteSpace(share.AdvertiseHost))
            {
                Write(sink, model, layout.Panel, col, body, LimitedReachText, width, theme.Subtext0, theme.PanelBg);
                body++;
            }

            body = WriteReachLines(sink, model, layout, col, body, width, share, ReachLines(share), theme);
        }

        if (!share.ShareEnabled)
            Write(sink, model, layout.Panel, col, body, "share is disabled while painting a peer", width, theme.Yellow, theme.PanelBg);
        else if (!string.IsNullOrWhiteSpace(share.Error))
        {
            var errorRows = Math.Max(1, InviteLineCount(share.Error, width));
            WriteWrapped(
                sink,
                model,
                layout.Panel,
                share.Error,
                new CellRect(col, body, width, errorRows),
                theme.Red,
                theme.PanelBg);
        }
        else if (!string.IsNullOrWhiteSpace(share.Invite))
        {
            if (!share.Advanced)
            {
                WriteReachLines(sink, model, layout, col, body, width, share, ReachLines(share), theme);
            }

            WriteWrapped(sink, model, layout.Panel, share.Invite, layout.Invite, theme.Teal, theme.PanelBg);
            var hintRow = layout.Invite.Rows == 0 ? body + 1 : layout.Invite.EndRow;
            if (hintRow < layout.Primary.Row)
                Write(sink, model, layout.Panel, col, hintRow, CodeHint, width, theme.Subtext0, theme.PanelBg);
        }
        else if (!share.Advanced)
            Write(sink, model, layout.Panel, col, body, StartHint, width, theme.Subtext0, theme.PanelBg);
    }

    private static void StampAdd(
        IHostCellSink sink,
        CubePairingDialogModel model,
        CubeAddState add,
        CubePairingDialogLayout layout,
        ThemePalette theme)
    {
        var col = layout.Panel.Col + 2;
        var width = layout.Panel.Cols - 4;
        Write(sink, model, layout.Panel, col, layout.Panel.Row + 3, "code: " + add.Invite, width, FieldFg(add.Focus == 0, theme), theme.PanelBg);
        Write(sink, model, layout.Panel, col, layout.Panel.Row + 4, "label: " + add.Label, width, FieldFg(add.Focus == 1, theme), theme.PanelBg);
        Write(sink, model, layout.Panel, col, layout.Panel.Row + 5, "paste the full hypa-invite: code", width, theme.Subtext0, theme.PanelBg);
        if (!string.IsNullOrWhiteSpace(add.Error))
            Write(sink, model, layout.Panel, col, layout.Panel.Row + 6, add.Error, width, theme.Red, theme.PanelBg);
    }

    private static int ReachExtraRows(CubeShareState? share, bool advanced, bool paired)
    {
        if (paired || share is null)
            return 0;
        // Only one page shows, so the layout reserves one page at most.
        var reachCount = Math.Min(ReachLines(share).Count, ReachPageRows);
        if (advanced)
            return (string.IsNullOrWhiteSpace(share.AdvertiseHost) ? 0 : 1) + reachCount;
        return reachCount > 1 ? reachCount - 1 : 0;
    }

    private static IReadOnlyList<string> ReachHosts(CubeShareState share)
    {
        if (!string.IsNullOrWhiteSpace(share.AdvertiseHost))
            return [share.AdvertiseHost.Trim()];
        if (!string.IsNullOrWhiteSpace(share.BindHost) && !HostInviteReach.IsWildcard(share.BindHost))
            return [share.BindHost.Trim()];
        return share.AdvertisedHosts;
    }

    private static string FormatReachHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;

    private static string InviteText(CubePairingDialogModel model) =>
        model.Kind is CubePairingDialogKind.Share
            ? model.Share?.Invite ?? ""
            : model.Add?.Invite ?? "";

    private static ThemeColor FieldFg(bool focused, ThemePalette theme) =>
        focused ? theme.Text : theme.Subtext0;

    private static void WriteWrapped(
        IHostCellSink sink,
        CubePairingDialogModel model,
        CellRect panel,
        string text,
        CellRect rect,
        ThemeColor fg,
        ThemeColor bg)
    {
        if (rect.Cols <= 0 || rect.Rows <= 0)
            return;
        for (var i = 0; i < rect.Rows; i++)
        {
            var start = i * rect.Cols;
            if (start >= text.Length)
                break;
            var take = Math.Min(rect.Cols, text.Length - start);
            Write(sink, model, panel, rect.Col, rect.Row + i, text.Substring(start, take), rect.Cols, fg, bg);
        }
    }

    private static void Write(
        IHostCellSink sink,
        CubePairingDialogModel model,
        CellRect panel,
        int col,
        int row,
        string text,
        int cols,
        ThemeColor fg,
        ThemeColor bg,
        bool bold = false)
    {
        if (cols <= 0)
            return;
        var clipped = text.Length <= cols ? text.PadRight(cols) : text[..cols];
        model.CaptureGlyphs(panel, col, row, clipped);
        for (var i = 0; i < clipped.Length && i < cols; i++)
        {
            var selected = model.Selection.Contains(col + i, row);
            sink.Write(
                col + i,
                row,
                clipped[i].ToString(),
                selected ? bg : fg,
                selected ? fg : bg,
                1,
                inverse: selected,
                bold: bold);
        }
    }
}
