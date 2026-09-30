using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PrefixAsciiInputCoordinatorTests
{
    [Fact]
    public void Disabled_flag_is_a_no_op()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var coordinator = new PrefixAsciiInputCoordinator { Enabled = false };
        coordinator.ReconcileAndApply(wantsAscii: true, source);
        Assert.Empty(source.Calls);
        Assert.False(coordinator.AsciiActive);
    }

    [Fact]
    public void Enabled_prefix_switches_and_restore_returns()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var coordinator = new PrefixAsciiInputCoordinator { Enabled = true };
        coordinator.ReconcileAndApply(wantsAscii: true, source);
        Assert.Equal(["switch"], source.Calls);
        coordinator.ReconcileAndApply(wantsAscii: false, source);
        Assert.Equal(["switch", "restore"], source.Calls);
        Assert.False(coordinator.AsciiActive);
    }

    [Fact]
    public void Lost_host_focus_keeps_the_restore_token()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var coordinator = new PrefixAsciiInputCoordinator { Enabled = true };
        coordinator.ReconcileAndApply(wantsAscii: true, source);
        coordinator.OuterFocused = false;
        coordinator.ReconcileAndApply(wantsAscii: false, source);
        Assert.Equal(["switch"], source.Calls);
        Assert.True(coordinator.AsciiActive);
        coordinator.OuterFocused = true;
        coordinator.ReconcileAndApply(wantsAscii: false, source);
        Assert.Equal(["switch", "restore"], source.Calls);
    }

    [Fact]
    public void Cleanup_restore_runs_while_prefix_is_still_active()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var coordinator = new PrefixAsciiInputCoordinator { Enabled = true };
        coordinator.ReconcileAndApply(wantsAscii: true, source);
        coordinator.OuterFocused = false;
        coordinator.ReconcileAndApply(wantsAscii: false, source);
        Assert.Equal(["switch"], source.Calls);
        Assert.True(coordinator.AsciiActive);
        source.Restore();
        Assert.Equal(["switch", "restore"], source.Calls);
    }

    private sealed class RecordingPrefixAsciiInputSource : IPrefixAsciiInputSource
    {
        public List<string> Calls { get; } = [];

        public void SwitchToAscii() => Calls.Add("switch");

        public void Restore() => Calls.Add("restore");
    }
}
