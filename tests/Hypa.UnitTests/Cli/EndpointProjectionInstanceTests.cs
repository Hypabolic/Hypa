using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The commit of a cube connection and the pane state of the attach. A new
/// connection to the same cube, for example after a restart of its mux,
/// starts from no pane state. The pane state of a connection stays while the
/// same connection is active.
/// </summary>
public sealed class EndpointProjectionInstanceTests
{
    private const string Cube = "plc_cube";

    [Fact]
    public void A_commit_of_a_restarted_mux_on_the_same_cube_drops_the_pane_state_of_the_earlier_mux()
    {
        var live = Committed("boot-a", 1);
        HoldPaneState(live);

        Commit(live, "boot-b", 1);

        AssertNoPaneState(live);
        Assert.Equal(Cube, live.ActiveProjectionEndpointId);
        Assert.Equal(new EndpointMuxInstance("boot-b", 1), live.ActiveProjectionInstance);
    }

    [Fact]
    public void A_commit_of_a_new_connection_to_the_same_mux_keeps_the_frames_and_drops_the_connection_state()
    {
        var live = Committed("boot-a", 1);
        HoldPaneState(live);

        Commit(live, "boot-a", 2);

        // The frame generations of the same mux go on, so the frames stay.
        Assert.True(live.TryGetPaneFrame("p1", out var frame));
        Assert.Equal(7, frame!.Generation);
        Assert.Equal(7, live.LastAppliedBlit("p1").Generation);

        // The new connection has no observed panes, no pane sizes of this
        // client and no reanchor request.
        lock (live.ChromeStateGate)
        {
            Assert.Empty(live.ObservedPaneIds);
            Assert.Empty(live.LastSentPaneSizes);
            Assert.Empty(live.OwnPaneResizeSentAt);
        }

        Assert.False(live.ChainReanchors.IsOpen("p2"));
        Assert.Equal(new EndpointMuxInstance("boot-a", 2), live.ActiveProjectionInstance);
    }

    [Fact]
    public void A_commit_that_names_no_boot_id_starts_from_no_pane_state()
    {
        var live = Committed("", 1);
        HoldPaneState(live);

        Commit(live, "", 1);

        AssertNoPaneState(live);
    }

    [Fact]
    public void A_second_activation_of_the_same_connection_keeps_the_pane_state()
    {
        var live = Committed("boot-a", 1);
        HoldPaneState(live);

        Commit(live, "boot-a", 1);
        AttachSession.SelectCube(live, live.Cubes.Single());
        _ = AttachSession.TryActivateEndpointProjection(live, Cube);

        Assert.True(live.TryGetPaneFrame("p1", out var frame));
        Assert.NotNull(frame);
        lock (live.ChromeStateGate)
        {
            Assert.Contains("p1", live.ObservedPaneIds);
            Assert.Equal((40, 23), live.LastSentPaneSizes["p1"]);
        }

        Assert.Equal(7, live.LastAppliedBlit("p1").Generation);
    }

    private static AttachLiveState Committed(string bootId, ulong generation)
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = Cube,
                Name = "cube",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        Commit(live, bootId, generation);
        return live;
    }

    private static void Commit(AttachLiveState live, string bootId, ulong generation)
    {
        var dest = new ControlPlaneClient("/tmp/hypa-projection-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        AttachSession.ApplyCubesConnectOutcome(
            live,
            MouseTestGeom.ApplyPort(),
            live.Cubes.Single(),
            Outcome(dest, bootId, generation));
    }

    /// <summary>
    /// The pane state that a connection builds: a shown frame at a high
    /// generation, the observed panes, the sent pane sizes, an open reanchor
    /// request and a scroll revision.
    /// </summary>
    private static void HoldPaneState(AttachLiveState live)
    {
        var frame = MouseTestGeom.Frame("$ old mux", "p1", cols: 40) with { Generation = 7 };
        live.SetPaneFrame(frame);
        live.StoreAppliedBlit("p1", 7, 0);
        lock (live.ChromeStateGate)
        {
            live.ObservedPaneIds.Add("p1");
            live.ObservedPaneIds.Add("p2");
            live.LastSentPaneSizes["p1"] = (40, 23);
            live.LastSentPaneSizes["p2"] = (39, 23);
            live.OwnPaneResizeSentAt["p1"] = live.Time.GetUtcNow();
        }

        Assert.True(live.ChainReanchors.Request("p2", live.Time.GetUtcNow()));
        Assert.True(live.TrySetPaneScrollMetrics(
            "p1",
            0,
            10,
            23,
            new PaneScrollRevision("99999999999999999999-old", 5)));
    }

    private static void AssertNoPaneState(AttachLiveState live)
    {
        Assert.False(live.TryGetPaneFrame("p1", out _));
        Assert.Equal(0, live.LastAppliedBlit("p1").Generation);
        lock (live.ChromeStateGate)
        {
            Assert.Empty(live.ObservedPaneIds);
            Assert.Empty(live.LastSentPaneSizes);
            Assert.Empty(live.OwnPaneResizeSentAt);
        }

        Assert.False(live.ChainReanchors.IsOpen("p2"));
        Assert.False(live.ChainReanchors.HasWork);

        // A scroll state of the next mux applies, also when its epoch sorts
        // before the epoch of the earlier mux.
        Assert.True(live.TrySetPaneScrollMetrics(
            "p1",
            0,
            3,
            23,
            new PaneScrollRevision("00000000000000000001-new", 1)));
    }

    private static CubesConnectRetargetOutcome Outcome(ControlPlaneClient dest, string bootId, ulong generation) =>
        new()
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = AttachEndpointKinds.Unix,
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestPaneId = "p1",
            DestInputLease = "dest_input",
            DestResizeLease = "dest_resize",
            DestSubscribeId = "sub_dest_" + generation,
            DestClient = dest,
            TargetBootId = bootId,
            TargetConnectionGeneration = generation,
        };
}
