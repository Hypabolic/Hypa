using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Hypa.ControlPlane;

namespace Hypa.App;

public sealed class MainWindow : Window
{
    private readonly Border _header;
    private readonly StackPanel _topTabs;
    private readonly StackPanel _sideSpaces;
    private readonly StackPanel _sideTabs;
    private readonly Border _sideRail;
    private readonly Border _pathPill;
    private readonly TextBlock _pathPillText;
    private readonly TextBlock _spacePillText;
    private readonly Border _spacePill;
    private readonly Button _placementToggle;
    private readonly TerminalPane _terminal;
    private readonly ScrollBar _scrollBar;
    private AppSession? _session;
    private CancellationTokenSource? _loopCts;
    private bool _scrollDrag;
    private bool _bindQueued;

    public MainWindow()
    {
        Title = "Hypa";
        Width = 1120;
        Height = 720;
        MinWidth = 720;
        MinHeight = 420;
        Background = Brush("#121212");
        SystemDecorations = SystemDecorations.Full;
        Focusable = true;
        CanResize = true;

        _topTabs = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _sideSpaces = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 6,
        };
        _sideTabs = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 6,
        };
        _sideRail = new Border
        {
            Width = 200,
            Background = Brush("#1A1A1C"),
            BorderBrush = Brush("#2A2A2E"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 12),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        SectionLabel("Spaces"),
                        _sideSpaces,
                        GhostButton("+ New space", async () => await NewSpaceAsync().ConfigureAwait(true)),
                        SectionLabel("Tabs"),
                        _sideTabs,
                        GhostButton("+ New tab", async () => await NewTabAsync().ConfigureAwait(true)),
                    },
                },
            },
            IsVisible = true,
        };
        (_pathPill, _pathPillText) = PillText("~/workspace");
        (_spacePill, _spacePillText) = PillText("Space");
        _placementToggle = new Button
        {
            Content = "Top tabs",
            Classes = { "pill" },
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(999),
            Background = Brush("#2A2A30"),
            Foreground = Brush("#E8E8EC"),
            BorderThickness = new Thickness(0),
        };
        _placementToggle.Click += async (_, _) => await TogglePlacementAsync().ConfigureAwait(true);

        var brandPill = PillButton("Hypa", async () => await NewSpaceAsync().ConfigureAwait(true));
        _header = new Border
        {
            Height = 48,
            Background = Brush("#161618"),
            BorderBrush = Brush("#2A2A2E"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 0),
            Child = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                Children =
                {
                    Col(new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Children = { brandPill, _spacePill, _topTabs },
                    }, 0),
                    Col(new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Children = { _pathPill, _placementToggle },
                    }, 2),
                },
            },
        };

        _terminal = new TerminalPane();
        _scrollBar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Width = 12,
            Margin = new Thickness(0, 8, 8, 8),
            Minimum = 0,
            Maximum = 0,
            ViewportSize = 1,
            Visibility = ScrollBarVisibility.Visible,
        };
        _scrollBar.PropertyChanged += async (_, e) =>
        {
            if (e.Property != ScrollBar.ValueProperty)
                return;
            if (_session is null || _scrollDrag)
                return;
            _scrollDrag = true;
            try
            {
                var value = e.NewValue is double d ? d : _scrollBar.Value;
                await _session.ScrollToAsync((int)Math.Round(value), CancellationToken.None)
                    .ConfigureAwait(true);
            }
            finally
            {
                _scrollDrag = false;
            }
        };

        var body = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Children =
            {
                Col(_sideRail, 0),
                Col(new Border
                {
                    Background = Brush("#0E0E10"),
                    Child = _terminal,
                    Margin = new Thickness(0),
                }, 1),
                Col(_scrollBar, 2),
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                DockAt(_header, Avalonia.Controls.Dock.Top),
                body,
            },
        };

        Opened += (_, _) =>
        {
            // First frame must not wait on mux RPC (Dock-bounce budget).
            _ = StartAsync();
        };
        Closed += (_, _) =>
        {
            _loopCts?.Cancel();
            _ = _session?.DisposeAsync();
        };
    }

    private async Task StartAsync()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var sessionName = "hypa-app";
            var cwd = Environment.CurrentDirectory;
            string? socket = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--session" && i + 1 < args.Length)
                    sessionName = args[++i];
                else if (args[i] == "--cwd" && i + 1 < args.Length)
                    cwd = args[++i];
                else if (args[i] == "--socket" && i + 1 < args.Length)
                    socket = args[++i];
            }

            if (!string.IsNullOrEmpty(socket))
                Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", socket);

            var socketPath = await MuxBootstrap.EnsureReadyAsync(sessionName, cwd, socket, CancellationToken.None)
                .ConfigureAwait(true);
            Console.Error.WriteLine($"hypa-app: socket={socketPath}");
            var client = new ControlPlaneClient(socketPath);
            await client.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            _session = new AppSession(client);
            _session.Changed += QueueBindSession;
            await _session.AttachAsync(CancellationToken.None).ConfigureAwait(true);
            BindSession();
            await ResizeTerminalAsync().ConfigureAwait(true);

            _loopCts = new CancellationTokenSource();
            _ = PumpLoopAsync(_loopCts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: start failed: {ex}");
            Title = "Hypa — start failed";
        }
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _session is not null)
        {
            try
            {
                await _session.PumpAsync(ct).ConfigureAwait(true);
                await Task.Delay(8, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hypa-app: pump: {ex.Message}");
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    private void QueueBindSession()
    {
        if (_bindQueued)
            return;
        _bindQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _bindQueued = false;
            BindSession();
        }, DispatcherPriority.Background);
    }

    private void BindSession()
    {
        if (_session is null)
            return;
        _terminal.Grid = _session.Grid;
        _pathPillText.Text = _session.CwdLabel;
        _spacePillText.Text = _session.WorkspaceLabel;
        RebuildSidebar();
        if (!_scrollDrag)
        {
            _scrollBar.Maximum = Math.Max(_session.ScrollMax, 0);
            _scrollBar.Value = Math.Clamp(_session.ScrollOffset, 0, _scrollBar.Maximum);
            _scrollBar.ViewportSize = Math.Max(1, _session.Grid.Rows);
        }

        ApplyPlacement();
        _terminal.InvalidateVisual();
    }

    private void RebuildSidebar()
    {
        if (_session is null)
            return;

        var workspaces = _session.Workspaces.ToArray();
        var tabs = _session.Tabs.ToArray();

        _sideSpaces.Children.Clear();
        foreach (var ws in workspaces)
        {
            var local = ws;
            _sideSpaces.Children.Add(SideRow(
                local.Label,
                local.IsFocused,
                async () =>
                {
                    if (_session is not null)
                        await _session.FocusWorkspaceAsync(local.WorkspaceId, CancellationToken.None)
                            .ConfigureAwait(true);
                }));
        }

        _topTabs.Children.Clear();
        _sideTabs.Children.Clear();
        foreach (var tab in tabs)
        {
            var local = tab;
            var top = new Button
            {
                Content = local.Label,
                Padding = new Thickness(12, 6),
                CornerRadius = new CornerRadius(999),
                BorderThickness = new Thickness(0),
                Background = local.IsFocused ? Brush("#3B82F6") : Brush("#2A2A30"),
                Foreground = Brush("#F4F4F5"),
            };
            top.Click += async (_, _) =>
            {
                if (_session is not null)
                    await _session.FocusTabAsync(local.TabId, CancellationToken.None).ConfigureAwait(true);
            };
            _topTabs.Children.Add(top);

            _sideTabs.Children.Add(SideRow(
                local.Label,
                local.IsFocused,
                async () =>
                {
                    if (_session is not null)
                        await _session.FocusTabAsync(local.TabId, CancellationToken.None).ConfigureAwait(true);
                }));
        }

        var add = new Button
        {
            Content = "+",
            Width = 32,
            Padding = new Thickness(0, 4),
            CornerRadius = new CornerRadius(999),
            Background = Brush("#2A2A30"),
            Foreground = Brush("#E8E8EC"),
            BorderThickness = new Thickness(0),
        };
        add.Click += async (_, _) => await NewTabAsync().ConfigureAwait(true);
        _topTabs.Children.Add(add);
    }

    private async Task NewTabAsync()
    {
        if (_session is null)
            return;
        try
        {
            await _session.CreateTabAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: create tab failed: {ex.Message}");
        }
    }

    private async Task NewSpaceAsync()
    {
        if (_session is null)
            return;
        try
        {
            await _session.CreateWorkspaceAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hypa-app: create space failed: {ex.Message}");
        }
    }

    private async Task TogglePlacementAsync()
    {
        var next = (_session?.TabPlacement ?? TabPlacement.Side) == TabPlacement.Top
            ? TabPlacement.Side
            : TabPlacement.Top;
        if (_session is not null)
            _session.TabPlacement = next;
        _placementToggle.Content = next == TabPlacement.Top ? "Side tabs" : "Top tabs";
        ApplyPlacement(next);
        await Task.CompletedTask;
    }

    private void ApplyPlacement(TabPlacement? forced = null)
    {
        // Spaces sidebar is always visible. Toggle only moves tabs top ↔ side.
        var sideTabs = (forced ?? _session?.TabPlacement ?? TabPlacement.Side) == TabPlacement.Side;
        _sideRail.IsVisible = true;
        _topTabs.IsVisible = !sideTabs;
        _sideTabs.IsVisible = sideTabs;
        // Keep the Tabs section label context via parent stack; rows hide with _sideTabs.
    }

    private async Task ResizeTerminalAsync()
    {
        if (_session is null)
            return;
        var bounds = _terminal.Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;
        var cols = Math.Max(20, (int)(bounds.Width / _terminal.CellWidth));
        var rows = Math.Max(8, (int)(bounds.Height / _terminal.CellHeight));
        await _session.SetSizeAsync(cols, rows, CancellationToken.None).ConfigureAwait(true);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null)
            return;

        if (e.Key == Key.T
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await TogglePlacementAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await NewSpaceAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.T
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await NewTabAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        byte[]? bytes = e.Key switch
        {
            Key.Enter => [0x0d],
            Key.Back => [0x7f],
            Key.Tab => [0x09],
            Key.Escape => [0x1b],
            Key.Up => [0x1b, (byte)'[', (byte)'A'],
            Key.Down => [0x1b, (byte)'[', (byte)'B'],
            Key.Right => [0x1b, (byte)'[', (byte)'C'],
            Key.Left => [0x1b, (byte)'[', (byte)'D'],
            Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) => [0x03],
            Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control) => [0x04],
            Key.L when e.KeyModifiers.HasFlag(KeyModifiers.Control) => [0x0c],
            _ => null,
        };
        if (bytes is not null)
        {
            await _session.SendKeysAsync(bytes, CancellationToken.None).ConfigureAwait(true);
            e.Handled = true;
        }
    }

    protected override async void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;
        await _session.SendKeysAsync(Encoding.UTF8.GetBytes(e.Text), CancellationToken.None)
            .ConfigureAwait(true);
        e.Handled = true;
    }

    protected override async void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        await ResizeTerminalAsync().ConfigureAwait(true);
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.55,
        Margin = new Thickness(4, 0, 0, 2),
    };

    private static Button SideRow(string text, bool focused, Func<Task> onClick)
    {
        var btn = new Button
        {
            Content = text,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = focused ? Brush("#3B82F6") : Brush("#222226"),
            Foreground = Brush("#F4F4F5"),
        };
        btn.Click += async (_, _) => await onClick().ConfigureAwait(true);
        return btn;
    }

    private static Button GhostButton(string text, Func<Task> onClick)
    {
        var btn = new Button
        {
            Content = text,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            Background = Brush("#2A2A30"),
            Foreground = Brush("#E8E8EC"),
            BorderThickness = new Thickness(0),
        };
        btn.Click += async (_, _) => await onClick().ConfigureAwait(true);
        return btn;
    }

    private static (Border Host, TextBlock Label) PillText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brush("#D4D4D8"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var host = new Border
        {
            Child = block,
            Padding = new Thickness(12, 6),
            Background = Brush("#2A2A30"),
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        return (host, block);
    }

    private static Button PillButton(string text, Func<Task> onClick)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(999),
            Background = Brush("#2A2A30"),
            Foreground = Brush("#F4F4F5"),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeight.SemiBold,
        };
        btn.Click += async (_, _) => await onClick().ConfigureAwait(true);
        return btn;
    }

    private static Control Col(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control DockAt(Control control, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush Brush(string hex) =>
        SolidColorBrush.Parse(hex);
}

internal sealed class TerminalPane : Control
{
    private static readonly Typeface Mono = new("DejaVu Sans Mono, Menlo, Consolas, monospace");
    private readonly Rgba _fg = new(230, 230, 230);
    private readonly Rgba _bg = new(14, 14, 16);

    public CellGrid? Grid { get; set; }
    public double CellWidth { get; private set; } = 9;
    public double CellHeight { get; private set; } = 18;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(_bg.R, _bg.G, _bg.B)), Bounds);

        var probe = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Mono,
            14,
            Brushes.White);
        CellWidth = Math.Max(7, probe.Width);
        CellHeight = Math.Max(14, probe.Height + 4);

        var grid = Grid;
        if (grid is null || grid.Cells.Length == 0)
            return;

        for (var r = 0; r < grid.Rows; r++)
        {
            for (var c = 0; c < grid.Cols; c++)
            {
                var cell = grid.At(c, r);
                if (cell.IsContinuation)
                    continue;
                var fg = ColorResolve.Resolve(cell.Style.Fg, _fg);
                var bg = ColorResolve.Resolve(cell.Style.Bg, _bg);
                if (cell.Style.Inverse)
                    (fg, bg) = (bg, fg);
                var rect = new Rect(c * CellWidth, r * CellHeight, CellWidth * Math.Max(1, cell.Width), CellHeight);
                if (bg.R != _bg.R || bg.G != _bg.G || bg.B != _bg.B)
                    context.FillRectangle(new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B)), rect);
                if (cell.Style.Invisible || string.IsNullOrEmpty(cell.Glyph) || cell.Glyph == " ")
                    continue;
                var text = new FormattedText(
                    cell.Glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    cell.Style.Bold ? new Typeface(Mono.FontFamily, FontStyle.Normal, FontWeight.Bold) : Mono,
                    14,
                    new SolidColorBrush(Color.FromRgb(fg.R, fg.G, fg.B)));
                context.DrawText(text, new Point(rect.X, rect.Y + 1));
            }
        }

        if (grid.Cursor is { HasCursor: true, Visible: true } cur
            && (uint)cur.Col < (uint)grid.Cols
            && (uint)cur.Row < (uint)grid.Rows)
        {
            var caret = new Rect(cur.Col * CellWidth, cur.Row * CellHeight, CellWidth, CellHeight);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 200, 200, 220)), caret);
        }
    }
}
