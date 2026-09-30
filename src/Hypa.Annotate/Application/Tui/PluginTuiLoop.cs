using System.Runtime.InteropServices;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
/// Draw and read keys on the plugin TTY. Restore raw mode on every exit.
/// Missing TTY returns nonzero. There is no line-input fallback.
/// </summary>
public static class PluginTuiLoop
{
    public static int RunEditor(AnnotateEditorApp app, string stateDirectory, bool saveToStore)
    {
        using var tty = PluginRawTerminal.TryOpen();
        if (tty is null)
            return 1;

        try
        {
            tty.EnterRaw();
            using var signals = new PluginTuiSignals(() => app.Quit = true);
            while (!app.Quit)
            {
                var (cols, rows) = tty.Size(AnnotateEditorApp.InnerCols, AnnotateEditorApp.InnerRows);
                tty.Write(app.RenderGrid(cols, rows).PaintAnsi());
                var key = tty.ReadKey();
                if (key is null)
                {
                    app.Quit = true;
                    break;
                }

                if (!app.HandleKey(key.Value))
                    continue;

                if (saveToStore)
                {
                    if (!app.TrySave(stateDirectory))
                        continue;

                    tty.Write(app.RenderGrid(cols, rows).PaintAnsi());
                    Thread.Sleep(250);
                    app.Quit = true;
                }
                else if (app.TryAcceptComment(out _))
                {
                    app.Quit = true;
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return 1;
        }
        finally
        {
            tty.Restore();
        }
    }

    public static int RunManager(AnnotateManagerApp app)
    {
        using var tty = PluginRawTerminal.TryOpen();
        if (tty is null)
            return 1;

        try
        {
            tty.EnterRaw();
            using var signals = new PluginTuiSignals(() => app.Quit = true);
            while (!app.Quit)
            {
                var (cols, rows) = tty.Size(AnnotateManagerApp.InnerCols, AnnotateManagerApp.InnerRows);
                Paint(tty, app.Render(cols, rows), cols, rows);
                var key = tty.ReadKey();
                if (key is null)
                {
                    app.Quit = true;
                    break;
                }

                app.HandleKey(key.Value);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return 1;
        }
        finally
        {
            tty.Restore();
        }
    }

    private static void Paint(PluginRawTerminal tty, IReadOnlyList<string> lines, int cols, int rows)
    {
        var grid = CellGrid.Blank(cols, rows);
        for (var y = 0; y < lines.Count && y < rows; y++)
            grid.Write(0, y, lines[y].TrimEnd(), cols);
        tty.Write(grid.PaintAnsi());
    }

    /// <summary>
    /// Cancel default interrupt, terminate, and hangup so raw mode can restore.
    /// and terminate. Hypa also cancels interrupt so Ctrl+C can restore.
    /// </summary>
    internal sealed class PluginTuiSignals : IDisposable
    {
        internal static readonly PosixSignal[] Handled =
        [
            PosixSignal.SIGINT,
            PosixSignal.SIGTERM,
            PosixSignal.SIGHUP,
        ];

        private readonly List<PosixSignalRegistration> _registrations = [];

        public PluginTuiSignals(Action onQuit)
        {
            ArgumentNullException.ThrowIfNull(onQuit);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                return;

            foreach (var signal in Handled)
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    onQuit();
                    ctx.Cancel = true;
                }));
            }
        }

        internal int RegistrationCount => _registrations.Count;

        public void Dispose()
        {
            foreach (var registration in _registrations)
                registration.Dispose();
            _registrations.Clear();
        }
    }
}
