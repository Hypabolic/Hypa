using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusPaneRuntimeTests
{
    private static PaneRuntime NewRuntime() =>
        new(
            new PaneSpawnOptions { Id = new PaneId("p_status"), Cwd = "/", Command = "/bin/true" },
            new StubVtEngine(20, 6));

    [Fact]
    public async Task Query_gets_the_fixed_reply_for_either_terminator()
    {
        await using var runtime = NewRuntime();
        var reply = Assert.Single(runtime.HandleChildBytesForTests("\u001b]7501;?\u001b\\"u8, out var signals));
        Assert.Equal("\u001b]7501;?\u001b\\"u8.ToArray(), reply);
        Assert.Empty(signals);
        reply = Assert.Single(runtime.HandleChildBytesForTests("\u001b]7501;?\u0007"u8, out _));
        Assert.Equal("\u001b]7501;?\u001b\\"u8.ToArray(), reply);
    }

    [Fact]
    public async Task Query_split_over_two_chunks_gets_one_reply()
    {
        await using var runtime = NewRuntime();
        Assert.Empty(runtime.HandleChildBytesForTests("\u001b]75"u8, out _));
        Assert.Single(runtime.HandleChildBytesForTests("01;?\u001b\\"u8, out _));
    }

    [Fact]
    public async Task Report_gives_a_signal_and_no_reply()
    {
        await using var runtime = NewRuntime();
        var replies = runtime.HandleChildBytesForTests(
            "\u001b]7501;state=working:app=brew:msg=SW5zdGFsbGluZyB1cGRhdGVz\u001b\\"u8,
            out var signals);
        Assert.Empty(replies);
        var signal = Assert.Single(signals);
        Assert.Equal("Installing updates", signal.Report!.Msg);
    }

    [Fact]
    public async Task Soft_reset_gives_no_signal_and_full_reset_gives_one()
    {
        await using var runtime = NewRuntime();
        _ = runtime.HandleChildBytesForTests("\u001b[!p"u8, out var soft);
        Assert.Empty(soft);
        _ = runtime.HandleChildBytesForTests("\u001bc"u8, out var full);
        Assert.Equal(ProgramStatusSignalKind.Reset, Assert.Single(full).Kind);
    }
}
