namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Semantic identity for one attach frame, compared before pack.
/// committed for that subscriber after a successful writer admission.
/// </summary>
public static class VtFrameIdentity
{
    public static bool Equal(VtFrame? left, VtFrame? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return left.CellsVisuallyEqual(right)
            && left.Cursor.Equals(right.Cursor)
            && left.Cols == right.Cols
            && left.Rows == right.Rows
            && left.ViewportOrigin == right.ViewportOrigin
            && left.OccupantGeneration == right.OccupantGeneration;
    }

    /// <summary>
    /// Test helper for hub identity maps. Production skip uses
    /// <see cref="Equal"/> on a retained <see cref="VtFrame"/>.
    /// </summary>
    public static string Compute(
        string paneId,
        int occupantGeneration,
        int scrollOrigin,
        string snapshotJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        return string.Join(
            '\n',
            paneId,
            occupantGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scrollOrigin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            snapshotJson);
    }

    public static string Token(VtFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return string.Join(
            '\n',
            frame.PaneId,
            frame.OccupantGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Cols.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.ViewportOrigin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Cursor.Col.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Cursor.Row.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Cursor.HasCursor ? (frame.Cursor.Visible ? "1" : "0") : "none");
    }
}
