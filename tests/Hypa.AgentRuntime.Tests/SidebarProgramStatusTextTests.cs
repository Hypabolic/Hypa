using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class SidebarProgramStatusTextTests
{
    private static ProgramStatusView View(
        ProgramStatusState state, string? app = null, ProgramStatusKind? kind = null, int? progress = null, string? msg = null) =>
        new()
        {
            State = state,
            App = app,
            Kind = kind,
            Progress = progress,
            Msg = msg,
            RecordCount = 1,
            Source = new ProgramStatusSourceLabel("w", "t", "p"),
        };

    [Fact]
    public void Line_shows_state_app_kind_progress_and_message()
    {
        var line = SidebarProgramStatusText.Line(
            View(ProgramStatusState.Blocked, "deploy", ProgramStatusKind.Permission, 40, "Approve?"), 60);
        Assert.Equal("blocked deploy permission 40% Approve?", line);
    }

    [Fact]
    public void Line_leaves_out_the_app_when_the_row_already_shows_it()
    {
        var view = View(ProgramStatusState.Working, "cargo", progress: 40, msg: "Building");
        Assert.Equal("working 40% Building", SidebarProgramStatusText.Line(view, 60, "cargo"));
        Assert.Equal("working cargo 40% Building", SidebarProgramStatusText.Line(view, 60, "other"));
    }

    [Fact]
    public void Line_shortens_the_message_to_fit()
    {
        var line = SidebarProgramStatusText.Line(
            View(ProgramStatusState.Working, "brew", progress: 5, msg: "Installing a very long package name"), 24);
        Assert.Equal(24, SafeDisplayText.Width(line));
        Assert.StartsWith("working brew 5% Insta", line, StringComparison.Ordinal);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_keeps_message_text_as_plain_text()
    {
        var line = SidebarProgramStatusText.Line(View(ProgramStatusState.Idle, msg: "{state_icon} $x"), 60);
        Assert.Equal("idle {state_icon} $x", line);
    }

    [Theory]
    [InlineData(ProgramStatusState.Working, 40, "● 40%")]
    [InlineData(ProgramStatusState.Working, null, "●")]
    [InlineData(ProgramStatusState.Blocked, null, "◆")]
    [InlineData(ProgramStatusState.Error, null, "✖")]
    public void Tab_badge_shows_the_state_and_the_percentage_for_working(
        ProgramStatusState state, int? progress, string expected)
    {
        var badge = SidebarProgramStatusText.TabBadge(
            new TabProgramStatusView { TabId = "t", State = state, Progress = progress }, StatusIndicatorStyle.Dots);
        Assert.Equal(expected, badge);
    }

    [Fact]
    public void Wire_round_trips_the_pane_and_tab_views()
    {
        var view = View(ProgramStatusState.Blocked, "deploy", ProgramStatusKind.Auth, 7, "m");
        var node = ProgramStatusViewWire.ToNode(view)!;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        Assert.Equal(view, ProgramStatusViewWire.TryReadPane(doc.RootElement));

        var tab = new TabProgramStatusView { TabId = "t1", State = ProgramStatusState.Working, Progress = 40 };
        using var tabDoc = JsonDocument.Parse(ProgramStatusViewWire.ToNode(tab)!.ToJsonString());
        Assert.Equal(tab, ProgramStatusViewWire.TryReadTab(tabDoc.RootElement));
    }

    [Fact]
    public void Wire_reader_rejects_a_bad_state()
    {
        using var doc = JsonDocument.Parse("""{"state":"bogus","record_count":1,"source":{}}""");
        Assert.Null(ProgramStatusViewWire.TryReadPane(doc.RootElement));
    }
}
