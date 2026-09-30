using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AgentDetectionPresenceTests
{
    [Fact]
    public void Transient_process_miss_keeps_current_agent()
    {
        var presence = new AgentDetectionPresence();
        Assert.True(presence.ObserveProcessProbe("pi"));
        Assert.False(presence.ObserveProcessProbe(null));
        Assert.Equal("pi", presence.CurrentAgent);
    }

    [Fact]
    public void Confirmed_misses_clear_the_agent()
    {
        var presence = new AgentDetectionPresence();
        Assert.True(presence.ObserveProcessProbe("pi"));
        for (var i = 1; i < AgentDetectionPresence.MissConfirmationAttempts; i++)
        {
            Assert.False(presence.ObserveProcessProbe(null));
            Assert.Equal("pi", presence.CurrentAgent);
        }

        Assert.True(presence.ObserveProcessProbe(null));
        Assert.Null(presence.CurrentAgent);
    }

    [Fact]
    public void Identified_agent_resets_miss_count()
    {
        var presence = new AgentDetectionPresence();
        presence.ObserveProcessProbe("claude");
        presence.ObserveProcessProbe(null);
        Assert.False(presence.ObserveProcessProbe("claude"));
        Assert.Equal(0, presence.ConsecutiveMisses);
        Assert.Equal("claude", presence.CurrentAgent);
    }

    [Fact]
    public void Pane_shell_reports_process_exit_before_clearing_agent()
    {
        Assert.Equal(
            ForegroundShellAgentAction.ReportProcessExit,
            ForegroundShellAgentActions.Resolve("codex", null, foregroundIsPaneShell: true, processExitReported: false));
        Assert.Equal(
            ForegroundShellAgentAction.ClearAgent,
            ForegroundShellAgentActions.Resolve("codex", null, foregroundIsPaneShell: true, processExitReported: true));
    }

    [Fact]
    public void Unknown_non_shell_foreground_job_is_not_immediate_clear()
    {
        Assert.Equal(
            ForegroundShellAgentAction.ObserveProbe,
            ForegroundShellAgentActions.Resolve("claude", null, foregroundIsPaneShell: false, processExitReported: false));
    }

    [Fact]
    public void Apply_pane_shell_exit_keeps_agent_then_clears()
    {
        var presence = new AgentDetectionPresence();
        presence.ObserveProcessProbe("claude");
        Assert.False(presence.ApplyForegroundShellAction(
            ForegroundShellAgentAction.ReportProcessExit, "claude", null));
        Assert.Equal("claude", presence.CurrentAgent);
        Assert.True(presence.PendingForegroundShellClear);

        presence.MarkForegroundShellExitReported();
        Assert.True(presence.ApplyForegroundShellAction(
            ForegroundShellAgentAction.ClearAgent, "claude", null));
        Assert.Null(presence.CurrentAgent);
    }

    [Fact]
    public void Replacement_process_is_always_a_change()
    {
        var presence = new AgentDetectionPresence();
        presence.ObserveProcessProbe("claude");
        Assert.True(presence.ApplyForegroundShellAction(
            ForegroundShellAgentAction.ReportReplacementProcess, "claude", "claude"));
        Assert.Equal("claude", presence.CurrentAgent);
        Assert.False(presence.PendingForegroundShellClear);
        Assert.False(presence.ForegroundShellExitReported);
    }
}
