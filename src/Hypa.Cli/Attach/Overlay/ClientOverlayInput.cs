using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Overlay;

/// <summary>
/// Overlay input lease and fence. Keyboard and mouse go to the real pane.
/// An outside click is swallowed. Escape hides.
/// </summary>
internal static class ClientOverlayInput
{
    public static bool IsEscape(KeyChord chord) =>
        chord.IsEscape && !chord.Ctrl && !chord.Alt && !chord.Prefix;

    public static bool SwallowOutsideClick(ClientOverlayState overlay, MouseEvent ev)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(ev);
        if (!overlay.OwnsModal)
            return false;
        if (ev.IsWheel)
            return !overlay.ContainsInner(ev.Col, ev.Row);
        if (ev.Action is MouseAction.Press or MouseAction.Release or MouseAction.Drag)
            return !overlay.ContainsInner(ev.Col, ev.Row);
        return false;
    }

    public static MouseEngineResult? ForwardInner(ClientOverlayState overlay, MouseEvent ev)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(ev);
        if (!overlay.OwnsModal || overlay.Frame() is not { } frame)
            return null;
        if (!frame.Inner.Contains(ev.Col, ev.Row) && ev.Action is MouseAction.Press)
            return null;
        if (string.IsNullOrWhiteSpace(overlay.PaneId))
            return null;
        var maxCol = Math.Max(frame.Inner.Col, frame.Inner.EndCol - 1);
        var maxRow = Math.Max(frame.Inner.Row, frame.Inner.EndRow - 1);
        var col = Math.Clamp(ev.Col, frame.Inner.Col, maxCol) - frame.Inner.Col + 1;
        var row = Math.Clamp(ev.Row, frame.Inner.Row, maxRow) - frame.Inner.Row + 1;
        return new MouseEngineResult(
            MouseCommandKind.ForwardSgr,
            PaneId: overlay.PaneId,
            ForwardKeys: MouseDecoder.EncodeSgr(ev, col, row),
            State: MouseEngineState.Idle);
    }

    public static PopupGeometryResult? ResolveForContent(CellRect content)
    {
        var resolved = PopupGeometry.TryResolve(content.Cols, content.Rows);
        if (resolved is null)
            return null;
        return resolved with
        {
            OuterCol = resolved.OuterCol + content.Col,
            OuterRow = resolved.OuterRow + content.Row,
            InnerCol = resolved.InnerCol + content.Col,
            InnerRow = resolved.InnerRow + content.Row,
        };
    }
}
