using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class ClientShellAttachSessionTests
{
    [Fact]
    public void New_attach_client_ids_are_distinct()
    {
        var first = AttachSession.NewAttachClientId();
        var second = AttachSession.NewAttachClientId();
        Assert.StartsWith("cli_", first, StringComparison.Ordinal);
        Assert.StartsWith("cli_", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Pane_send_keys_omits_empty_lease()
    {
        var withoutLease = AttachSession.BuildPaneSendKeys("p1", [0x61], leaseId: null);
        Assert.Null(withoutLease["lease_id"]);
        var withLease = AttachSession.BuildPaneSendKeys("p1", [0x61], "lease_a");
        Assert.Equal("lease_a", withLease["lease_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Focus_move_does_not_claim_exclusive_leases()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ControlSub = "sub_focus";
        live.InputLease = "";
        live.ResizeLease = "";
        live.Dispatcher.FocusPane("p2");

        await AttachSession.SyncFocusAsync(port, live, CancellationToken.None);

        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseClaim);
        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalControl);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalObserve
                && call.Params?["pane_id"]?.GetValue<string>() == "p2");
        Assert.Equal("p2", live.PaneId);
        Assert.Equal("", live.InputLease);
    }
}
