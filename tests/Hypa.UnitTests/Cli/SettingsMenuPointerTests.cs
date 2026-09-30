using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Settings;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SettingsMenuPointerTests
{
    [Fact]
    public async Task Settings_menu_release_keeps_settings_open()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        var menu = ContextMenuModel.ForGlobal(0, 0, 80, 24);
        live.Engine.EnterGlobalMenu();
        live.Mouse.AdoptOpenMenu(menu);
        var click = new MouseEvent(MouseButton.Left, MouseAction.Press, menu.Rect.Col, menu.Rect.Row);
        var release = click with { Action = MouseAction.Release };
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);

        await AttachSession.DispatchMouseAttachAsync(tty, live, click, port, gate, linked, CancellationToken.None);
        await AttachSession.DispatchMouseAttachAsync(tty, live, release, port, gate, linked, CancellationToken.None);

        Assert.Equal(AttachClientMode.Settings, live.Engine.Mode);
        Assert.True(live.Engine.Settings.IsOpen);
    }

    [Fact]
    public async Task Settings_outside_release_still_closes()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings();
        var release = new MouseEvent(MouseButton.Left, MouseAction.Release, 0, 0);
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);

        await AttachSession.DispatchMouseAttachAsync(tty, live, release, port, gate, linked, CancellationToken.None);

        Assert.NotEqual(AttachClientMode.Settings, live.Engine.Mode);
        Assert.False(live.Engine.Settings.IsOpen);
    }

    [Fact]
    public void Theme_move_queues_a_patch_without_closing()
    {
        var model = new SettingsOverlayModel();
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.Open();
        Assert.True(model.MoveList(1));
        Assert.True(model.IsOpen);
        Assert.False(model.PendingCloseAfterPatch);
        Assert.Contains(model.PendingPatch!, assignment => assignment.Path == "theme.name");
    }

    [Fact]
    public void Settings_stamp_dims_the_host_and_draws_a_box()
    {
        var host = new HostFrame();
        host.Resize(80, 24);
        host.Stamp(0, 0, new AssembledCell("T", 1, false, AssembledStyle.Default));
        var model = new SettingsOverlayModel();
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.Open();

        SettingsPainter.Stamp(new HostFrameCellSink(host), model, 80, 24);

        Assert.Equal("T", host.CellAt(0, 0).Text);
        Assert.True(host.CellAt(0, 0).Style.Dim);
        var panel = model.Layout!.Panel;
        Assert.Equal("┌", host.CellAt(panel.Col, panel.Row).Text);
        Assert.Equal("┐", host.CellAt(panel.EndCol - 1, panel.Row).Text);
        Assert.Equal("└", host.CellAt(panel.Col, panel.EndRow - 1).Text);
        Assert.Equal("┘", host.CellAt(panel.EndCol - 1, panel.EndRow - 1).Text);
        Assert.Contains("settings", RowText(host, panel.Row + 1, panel.Col + 1, panel.Cols - 2), StringComparison.Ordinal);
    }

    [Fact]
    public void Borland_settings_stamp_draws_a_drop_shadow()
    {
        var host = new HostFrame();
        host.Resize(80, 24);
        var model = new SettingsOverlayModel();
        model.Bind(ThemeRuntime.Default, AttachUiConfig.Default);
        model.Open();

        SettingsPainter.Stamp(new HostFrameCellSink(host), model, 80, 24, ThemePalette.Borland);

        var panel = model.Layout!.Panel;
        var shadow = host.CellAt(panel.EndCol, panel.Row + ChromeShadow.Dy);
        Assert.Equal(" ", shadow.Text);
        Assert.False(shadow.Style.Dim);
        var bottom = host.CellAt(panel.Col + ChromeShadow.Dx, panel.EndRow);
        Assert.Equal(" ", bottom.Text);
    }

    private static string RowText(HostFrame host, int row, int col, int cols)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < cols; i++)
            sb.Append(host.CellAt(col + i, row).Text);
        return sb.ToString();
    }
}
