using Hypa.Cli.Attach;
using Hypa.AgentRuntime.Application.Attach.Status;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach status board. Each owner has one entry. A clear removes only
/// the entry of its owner. An error wins over a notice.
/// </summary>
public sealed class AttachStatusBoardTests
{
    [Fact]
    public void SetStoresTheEntryOfItsOwner()
    {
        var board = new AttachStatusBoard();

        board.Set(StatusOwner.Command, StatusSeverity.Error, "bad key");

        var entry = board.Entry(StatusOwner.Command);
        Assert.NotNull(entry);
        Assert.Equal(StatusSeverity.Error, entry.Severity);
        Assert.Equal("bad key", entry.Text);
        Assert.Null(board.Entry(StatusOwner.Connection));
    }

    [Fact]
    public void SetOnAnOwnerReplacesItsEntry()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Command, StatusSeverity.Error, "first");

        board.Set(StatusOwner.Command, StatusSeverity.Notice, "second");

        Assert.Equal("second", board.Entry(StatusOwner.Command)?.Text);
        Assert.Equal(StatusSeverity.Notice, board.Entry(StatusOwner.Command)?.Severity);
        Assert.Null(board.TopError);
    }

    [Fact]
    public void ClearRemovesOnlyTheEntryOfThatOwner()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Connection, StatusSeverity.Error, "connect failed");
        board.Set(StatusOwner.Command, StatusSeverity.Error, "bad key");

        board.Clear(StatusOwner.Command);

        Assert.Null(board.Entry(StatusOwner.Command));
        Assert.Equal("connect failed", board.TopError?.Text);
        Assert.Equal(StatusOwner.Connection, board.TopError?.Owner);
    }

    [Fact]
    public void ClearOfAnOwnerWithoutAnEntryKeepsTheOthers()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Reconnect, StatusSeverity.Notice, "Reconnecting to docker…");

        board.Clear(StatusOwner.MoveWork);

        Assert.Equal("Reconnecting to docker…", board.TopNotice?.Text);
    }

    [Fact]
    public void BlankTextClearsTheEntryOfTheOwner()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Settings, StatusSeverity.Error, "bad value");

        board.Set(StatusOwner.Settings, StatusSeverity.Error, null);
        Assert.Null(board.Entry(StatusOwner.Settings));

        board.Set(StatusOwner.Settings, StatusSeverity.Error, "bad value");
        board.Set(StatusOwner.Settings, StatusSeverity.Error, "   ");
        Assert.Null(board.Entry(StatusOwner.Settings));
    }

    [Fact]
    public void TopPicksAnErrorOverANoticeEvenWhenTheNoticeIsNewer()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.MoveWork, StatusSeverity.Error, "placement is not in the Cubes list");
        board.Set(StatusOwner.Reconnect, StatusSeverity.Notice, "Reconnecting to docker…");

        Assert.Equal(StatusSeverity.Error, board.Top?.Severity);
        Assert.Equal("placement is not in the Cubes list", board.Top?.Text);
        Assert.Equal("Reconnecting to docker…", board.TopNotice?.Text);
    }

    [Fact]
    public void TopPicksTheMostRecentEntryWithinASeverity()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Connection, StatusSeverity.Error, "older error");
        board.Set(StatusOwner.Command, StatusSeverity.Error, "newer error");
        board.Set(StatusOwner.Reconnect, StatusSeverity.Notice, "older notice");
        board.Set(StatusOwner.Transport, StatusSeverity.Notice, "newer notice");

        Assert.Equal("newer error", board.TopError?.Text);
        Assert.Equal("newer error", board.Top?.Text);
        Assert.Equal("newer notice", board.TopNotice?.Text);
    }

    [Fact]
    public void ReSettingAnOwnerMakesItsEntryTheNewest()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Connection, StatusSeverity.Error, "connect failed");
        board.Set(StatusOwner.Command, StatusSeverity.Error, "bad key");

        board.Set(StatusOwner.Connection, StatusSeverity.Error, "connect failed");

        Assert.Equal("connect failed", board.TopError?.Text);
    }

    [Fact]
    public void EmptyBoardHasNoTop()
    {
        var board = new AttachStatusBoard();

        Assert.Null(board.Top);
        Assert.Null(board.TopError);
        Assert.Null(board.TopNotice);
    }

    [Fact]
    public void ClearAllRemovesEveryEntry()
    {
        var board = new AttachStatusBoard();
        board.Set(StatusOwner.Connection, StatusSeverity.Error, "connect failed");
        board.Set(StatusOwner.Reconnect, StatusSeverity.Notice, "Reconnecting to docker…");

        board.ClearAll();

        Assert.Null(board.Top);
        Assert.Null(board.Entry(StatusOwner.Connection));
        Assert.Null(board.Entry(StatusOwner.Reconnect));
    }

    [Fact]
    public void CommandClearKeepsAConnectionError()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, "connect failed");

        AttachSession.ClearTransientStatusError(live, StatusOwner.Command);

        Assert.Equal("connect failed", live.StatusError);
    }

    [Fact]
    public void KeyInputDismissesTheVisibleErrorOnly()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.Status.Set(StatusOwner.Reconnect, StatusSeverity.Notice, "Reconnecting to docker…");
        live.Status.Set(StatusOwner.Transport, StatusSeverity.Error, "input sender fault");

        Assert.True(AttachSession.DismissStatusErrorOnInput(live));

        Assert.Null(live.StatusError);
        Assert.Equal("Reconnecting to docker…", live.StatusNotice);
    }

    [Fact]
    public void DismissReportsTheOlderErrorThatShowsAfterTheClear()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.Status.Set(StatusOwner.Transport, StatusSeverity.Error, "older fault");
        live.Status.Set(StatusOwner.Connection, StatusSeverity.Error, "newer fault");

        Assert.True(AttachSession.DismissStatusErrorOnInput(live));

        Assert.Equal("older fault", live.StatusError);
    }
}
