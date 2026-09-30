using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Turbo Vision–style solid drop shadow (+2 col, +1 row).</summary>
internal static class ChromeShadow
{
    public const int Dx = 2;
    public const int Dy = 1;

    public static void Stamp(
        IHostCellSink sink,
        CellRect rect,
        ThemePalette theme,
        int hostCols,
        int hostRows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(theme);
        if (!theme.Chrome.DropShadow || rect.Cols < 1 || rect.Rows < 1)
            return;

        var color = theme.ResolveShadow();
        for (var x = 0; x < Dx; x++)
        {
            var col = rect.EndCol + x;
            if ((uint)col >= (uint)hostCols)
                continue;
            for (var row = rect.Row + Dy; row < rect.EndRow + Dy; row++)
            {
                if ((uint)row >= (uint)hostRows)
                    continue;
                sink.Write(col, row, " ", color, color, 1);
            }
        }

        var bottom = rect.EndRow;
        if ((uint)bottom < (uint)hostRows)
        {
            for (var col = rect.Col + Dx; col < rect.EndCol + Dx; col++)
            {
                if ((uint)col >= (uint)hostCols)
                    continue;
                sink.Write(col, bottom, " ", color, color, 1);
            }
        }
    }
}
