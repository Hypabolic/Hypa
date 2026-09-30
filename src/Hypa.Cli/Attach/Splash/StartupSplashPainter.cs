using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using HypaCube;

namespace Hypa.Cli.Attach.Splash;

/// <summary>
// / Stamps cube and logo cells into the host frame.
/// <c>src/ui.rs:431-461</c> stamps overlays into the same <c>Frame</c>
/// after chrome. Do not call <see cref="CubeEngine.TickAnsi"/> as a host writer.
/// </summary>
internal static class StartupSplashPainter
{
    public static void Stamp(IHostCellSink sink, CubeSplashComposer composer, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(composer);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var engine = composer.Engine;
        var theme = engine.Options.Theme;
        var bg = ThemeColor.Rgb(theme.Bg.R, theme.Bg.G, theme.Bg.B);
        var cells = engine.Cells;
        var overlay = engine.Overlay;

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                if (overlay.TryGet(x, y, out var code, out var r, out var g, out var b))
                {
                    sink.Write(x, y, Glyph(code), ThemeColor.Rgb(r, g, b), bg);
                    continue;
                }

                if ((uint)x < (uint)cells.Cols && (uint)y < (uint)cells.Rows)
                {
                    var i = y * cells.Cols + x;
                    var fg = SplashPalette.Foreground(theme, cells.Ci[i]);
                    sink.Write(
                        x,
                        y,
                        Glyph(cells.Ch[i]),
                        ThemeColor.Rgb(fg.R, fg.G, fg.B),
                        bg);
                    continue;
                }

                sink.Write(x, y, " ", bg, bg);
            }
        }
    }

    private static string Glyph(ushort code) =>
        code == 0 ? " " : char.ConvertFromUtf32(code);
}
