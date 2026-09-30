using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CatalogReloadAttachTests
{
    [Fact]
    public void Unavailable_owner_freeze_does_not_request_input_stall()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_ssh";
        live.ActiveMuxSocketPath = "/tmp/hypa-ssh.sock";

        AttachSession.ApplyUnavailableOwnerFreeze(live);

        Assert.False(live.InputStallRequested);
        Assert.True(live.PresentationFrozen);
        Assert.True(live.PlacementOwnerUnavailable);
        Assert.Null(live.ActiveMuxSocketPath);
        Assert.True(AttachSession.BlocksPresentationInput(live));
    }

    [Fact]
    public void Catalog_reload_retirement_applies_only_for_current_selection()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_b";
        live.QueueCatalogReload([], SidebarCubeCatalogState.Ready, "plc_a");

        Assert.True(AttachSession.TryFlushCatalogReloadPaint(live, tty: null));
        Assert.False(live.PlacementOwnerUnavailable);

        live.QueueCatalogReload([], SidebarCubeCatalogState.Ready, "plc_b");
        Assert.True(AttachSession.TryFlushCatalogReloadPaint(live, tty: null));
        Assert.True(live.PlacementOwnerUnavailable);
    }

    [Fact]
    public void Retired_ssh_selection_requires_ssh_path_for_stall_reconnect()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_ssh";
        live.ConnectedSshPlacement = true;
        live.PlacementOwnerUnavailable = true;
        live.ActiveMuxSocketPath = null;

        Assert.True(AttachSession.RequiresSshReconnectPath(live));
        Assert.True(AttachSession.BlocksPresentationInput(live));
    }

    [Fact]
    public void Register_connect_replaces_prior_endpoint_generation()
    {
        var registry = new SshPlacementEndpointRegistry();
        var firstDisposed = false;
        var secondDisposed = false;
        var first = new SshAttachEndpoint(RemotePath("/tmp/a.sock", 1, () => firstDisposed = true));
        var second = new SshAttachEndpoint(RemotePath("/tmp/b.sock", 2, () => secondDisposed = true));

        var firstGeneration = registry.RegisterConnect("plc_a", first);
        var secondGeneration = registry.RegisterConnect("plc_a", second);
        Assert.Equal((ulong)1, firstGeneration);
        Assert.Equal((ulong)2, secondGeneration);
        Assert.True(registry.IsConnectActive("plc_a", secondGeneration, second));
        Assert.False(registry.IsConnectActive("plc_a", firstGeneration, first));

        registry.Retire("plc_a");
        Assert.True(firstDisposed);
        Assert.True(secondDisposed);
        Assert.False(registry.IsConnectActive("plc_a", secondGeneration, second));
    }

    [Fact]
    public void Input_sender_rejects_enqueue_when_presentation_frozen()
    {
        var sender = new AttachInputSender(
            new ControlPlaneClient("/tmp/hypa-input-sender-test.sock"),
            () => ("pane", "lease"),
            blocksForward: () => true);
        Assert.False(sender.TryEnqueue([0x61]));
    }

    [Fact]
    public void Unavailable_owner_freeze_discards_queued_input()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.InputSender = new AttachInputSender(
            new ControlPlaneClient("/tmp/hypa-input-sender-freeze.sock"),
            () => (live.PaneId, live.InputLease),
            blocksForward: () => AttachSession.BlocksPaneKeys(live));
        Assert.True(live.InputSender.TryEnqueue([0x61]));

        AttachSession.ApplyUnavailableOwnerFreeze(live);

        Assert.False(live.InputSender.TryEnqueue([0x62]));
    }

    [Fact]
    public void RetireIfActive_does_not_remove_newer_generation()
    {
        var registry = new SshPlacementEndpointRegistry();
        var firstDisposed = false;
        var secondDisposed = false;
        var first = new SshAttachEndpoint(RemotePath("/tmp/a.sock", 1, () => firstDisposed = true));
        var second = new SshAttachEndpoint(RemotePath("/tmp/b.sock", 2, () => secondDisposed = true));
        registry.RegisterConnect("plc_a", first);
        registry.RegisterConnect("plc_a", second);

        Assert.False(registry.RetireIfActive("plc_a", 1, first));
        Assert.True(registry.IsConnectActive("plc_a", 2, second));
        Assert.True(firstDisposed);
        Assert.False(secondDisposed);
    }

    [Fact]
    public void Retarget_quiesces_queued_input_before_installing_client()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var first = new ControlPlaneClient("/tmp/hypa-input-sender-retarget-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-input-sender-retarget-b.sock");
        live.InputSender = new AttachInputSender(
            first,
            () => (live.PaneId, live.InputLease));
        Assert.True(live.InputSender.TryEnqueue([0x61]));
        live.InputSender.Retarget(second, () => live.InputLease = "lease_b");
        Assert.Equal("lease_b", live.InputLease);
        Assert.True(live.InputSender.TryEnqueue([0x62]));
    }

    [Fact]
    public async Task Retarget_discards_bytes_queued_before_generation_bump()
    {
        var first = new ControlPlaneClient("/tmp/hypa-input-sender-gen-bump-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-input-sender-gen-bump-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        Assert.True(sender.TryEnqueue([0x61]));
        sender.BumpGenerationForTests();
        sender.Retarget(second, () => { });
        await Task.Delay(50);

        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task Retarget_discards_bytes_after_generation_bump_under_admission()
    {
        var first = new ControlPlaneClient("/tmp/hypa-input-sender-gen-admission-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-input-sender-gen-admission-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        Assert.True(sender.TryEnqueue([0x61]));
        sender.IncrementGenerationUnderAdmissionForTests();
        sender.Retarget(second, () => { });
        await Task.Delay(50);

        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task Retarget_does_not_deliver_pre_switch_batch_to_new_client()
    {
        var first = new ControlPlaneClient("/tmp/hypa-input-sender-pre-switch-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-input-sender-pre-switch-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        Assert.True(sender.TryEnqueue([0x61]));
        sender.SendAdmissionForTests.Wait();
        await Task.Delay(50);

        var retarget = Task.Run(() => sender.Retarget(second, () => { }));
        sender.SendAdmissionForTests.Release();
        await retarget.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task TryEnqueue_rejects_while_retarget_holds_admission()
    {
        var first = new ControlPlaneClient("/tmp/hypa-input-sender-enqueue-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-input-sender-enqueue-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        sender.RetargetAdmissionForTests.Wait();
        var retarget = Task.Run(() => sender.Retarget(second, () => { }));
        await Task.Delay(20);
        Assert.False(sender.TryEnqueue([0x61]));
        sender.RetargetAdmissionForTests.Release();
        await retarget.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task Retarget_during_in_flight_notify_writes_nothing_to_destination()
    {
        var first = new ControlPlaneClient("/tmp/hypa-retarget-notify-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-retarget-notify-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        sender.SendAdmissionForTests.Wait();
        Assert.True(sender.TryEnqueue([0x61]));
        var retarget = Task.Run(() => sender.Retarget(second, () => { }));
        await Task.Delay(20);
        sender.SendAdmissionForTests.Release();
        await retarget.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task Retarget_drains_queued_batch_without_sender_fault()
    {
        var first = new ControlPlaneClient("/tmp/hypa-retarget-drain-a.sock");
        var second = new ControlPlaneClient("/tmp/hypa-retarget-drain-b.sock");
        await using var sender = new AttachInputSender(first, () => ("pane_a", "lease_a"));
        Assert.True(sender.TryEnqueue([0x61]));
        sender.Retarget(second, () => { });
        await Task.Delay(100);

        Assert.False(sender.IsFaulted);
        Assert.Equal(0, first.NotifyWriteCount);
        Assert.Equal(0, second.NotifyWriteCount);
    }

    [Fact]
    public async Task TryEnqueue_succeeds_while_notify_holds_send_admission()
    {
        var client = new ControlPlaneClient("/tmp/hypa-input-sender-notify-gate.sock");
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"));
        sender.SendAdmissionForTests.Wait();
        Assert.True(sender.TryEnqueue([0x61]));
        await Task.Delay(50);
        for (var i = 0; i < 12; i++)
            Assert.True(sender.TryEnqueue([(byte)('a' + i)]));
        Assert.Equal(0, sender.RejectedBatches);
        sender.SendAdmissionForTests.Release();
        await Task.Delay(50);
    }

    [Fact]
    public async Task TryEnqueue_succeeds_while_blocked_inside_notify()
    {
        var client = new ControlPlaneClient("/tmp/hypa-input-sender-inside-notify.sock");
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"))
        {
            BlockNotifyForTests = true,
        };
        Assert.True(sender.TryEnqueue([0x61]));
        await sender.NotifyEnteredForTests.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 12; i++)
            Assert.True(sender.TryEnqueue([(byte)('a' + i)]));
        Assert.Equal(0, sender.RejectedBatches);
        sender.NotifyHoldForTests.Release();
        await Task.Delay(100);
    }

    [Fact]
    public void ApplyCubesConnectOutcome_does_not_release_source_leases_on_dest_commit()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        AttachSession.ApplyCubesConnectOutcome(
            live,
            port,
            cube,
            RetargetOutcome(new ControlPlaneClient("/tmp/hypa-connect-release.sock")));

        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
        Assert.Equal("dest_input", live.InputLease);
        Assert.Equal("dest_resize", live.ResizeLease);
    }

    [Fact]
    public void ApplyCubesConnectOutcome_clears_live_leases_when_dest_has_none()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        AttachSession.ApplyCubesConnectOutcome(
            live,
            port,
            cube,
            RetargetOutcome(new ControlPlaneClient("/tmp/hypa-connect-clear.sock")) with
            {
                DestInputLease = null,
                DestResizeLease = null,
            });

        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
        Assert.Equal("pane_d", live.PaneId);
    }

    [Fact]
    public void ApplyCubesConnectOutcome_keeps_source_client_after_retarget()
    {
        var source = new ControlPlaneClient("/tmp/hypa-leases-order-source.sock");
        var control = new SourceLeaseReleasePort(source);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.RenderPort = control;
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var dest = new ControlPlaneClient("/tmp/hypa-leases-order-dest.sock");

        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = control,
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PaneId = "p1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
        };
        AttachSession.ApplyCubesConnectOutcome(live, control, cube, RetargetOutcome(dest), live.SourceBackup);

        Assert.Equal(0, control.ReleaseAttempts);
        Assert.False(source.IsDisposed);
        Assert.Same(dest, live.ControlSlot!.Client);
        Assert.Same(source, live.SourceBackup.SourceClient);
    }

    [Fact]
    public void Second_connect_does_not_release_leases_at_dest_commit()
    {
        var originalPort = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(originalPort, MouseTestGeom.Split());
        var original = new ControlPlaneClient("/tmp/hypa-hop-original.sock");
        live.ControlSlot = new AttachControlSlot { Client = original };
        live.InputLease = "lease_a";
        live.ResizeLease = "resize_a";
        var cubeA = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "First",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var firstDest = new ControlPlaneClient("/tmp/hypa-hop-first.sock");
        var hopTracker = new MouseRecordingPort();
        AttachSession.ApplyCubesConnectOutcome(
            live,
            AttachSession.LiveControlPort(live, originalPort),
            cubeA,
            RetargetOutcome(firstDest));
        live.RenderPort = hopTracker;

        live.InputLease = "lease_b";
        live.ResizeLease = "resize_b";
        var cubeB = new SidebarCubeItem
        {
            Id = "plc_b",
            Name = "Second",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var secondDest = new ControlPlaneClient("/tmp/hypa-hop-second.sock");
        AttachSession.ApplyCubesConnectOutcome(
            live,
            originalPort,
            cubeB,
            RetargetOutcome(secondDest));

        Assert.DoesNotContain(
            hopTracker.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
        Assert.DoesNotContain(
            originalPort.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
        Assert.Same(secondDest, live.ControlSlot!.Client);
    }

    [Fact]
    public void Failed_activate_connect_keeps_source_leases()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        var registry = new SshPlacementEndpointRegistry();
        var endpoint = new SshAttachEndpoint(RemotePath("/tmp/e.sock", 5, () => { }));
        var generation = registry.RegisterConnect("plc_a", endpoint);
        registry.Retire("plc_a");
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var outcome = RetargetOutcome(new ControlPlaneClient("/tmp/hypa-connect-fail.sock")) with
        {
            SshConnectGeneration = generation,
            DestEndpoint = endpoint,
        };
        Assert.False(registry.IsConnectActive("plc_a", generation, endpoint));
        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
    }

    private static CubesConnectRetargetOutcome RetargetOutcome(ControlPlaneClient destClient) =>
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
            DestPaneId = "pane_d",
            DestInputLease = "dest_input",
            DestResizeLease = "dest_resize",
            DestSubscribeId = "sub_dest",
            DestClient = destClient,
        };

    private static bool SpinWaitUntilDisposed(ControlPlaneClient client)
    {
        var deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline)
        {
            if (client.IsDisposed)
                return true;
            Thread.Sleep(10);
        }

        return client.IsDisposed;
    }

    private sealed class SourceLeaseReleasePort(ControlPlaneClient source) : IAttachCommandPort
    {
        public int ReleaseAttempts { get; private set; }

        public bool ReleaseAfterDispose { get; private set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (!string.Equals(method, ProtocolMethods.RuntimeLeaseRelease, StringComparison.Ordinal))
                return Task.FromResult(default(JsonElement));

            ReleaseAttempts++;
            try
            {
                return new ControlPlaneAttachCommandPort(source).CallAsync(method, parameters, ct);
            }
            catch (ObjectDisposedException)
            {
                ReleaseAfterDispose = true;
                return Task.FromResult(default(JsonElement));
            }
            catch (InvalidOperationException)
            {
                ReleaseAfterDispose = true;
                return Task.FromResult(default(JsonElement));
            }
            catch (IOException)
            {
                return Task.FromResult(default(JsonElement));
            }
        }
    }

    [Fact]
    public void ApplyCubesConnectOutcome_binds_dest_subscription_after_retarget()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        live.ControlSub = "sub_source";
        live.RenderSub = "sub_source";
        live.AttachClientId = "conn_47";
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var outcome = RetargetOutcome(new ControlPlaneClient("/tmp/hypa-dest-sub-bind.sock")) with
        {
            DestSubscribeId = "sub_dest",
            DestAttachClientId = "conn_5",
        };

        AttachSession.ApplyCubesConnectOutcome(
            live,
            new MouseRecordingPort(),
            cube,
            outcome);

        Assert.Equal("sub_dest", live.ControlSub);
        Assert.Equal("sub_dest", live.RenderSub);
        Assert.Equal("conn_47", live.AttachClientId);
    }

    [Fact]
    public void ApplyCubesConnectOutcome_fails_closed_when_dest_subscription_missing()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        live.ControlSub = "sub_source";
        live.RenderSub = "sub_source";
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };

        AttachSession.ApplyCubesConnectOutcome(
            live,
            port,
            cube,
            RetargetOutcome(new ControlPlaneClient("/tmp/hypa-dest-sub-missing.sock")) with
            {
                DestSubscribeId = null,
            });

        Assert.Equal("sub_source", live.ControlSub);
        Assert.Equal("sub_source", live.RenderSub);
        Assert.Equal("src_input", live.InputLease);
        Assert.Equal("src_resize", live.ResizeLease);
        Assert.NotEqual(CubesConnectActions.Retargeted, live.CubesConnectAction);
        Assert.DoesNotContain(
            port.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease);
        Assert.Null(live.ControlSlot?.Client);
    }

    [Fact]
    public async Task Dest_commit_posts_dest_subscribe_on_visible_set_and_observe()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ControlSub = "sub_22";
        live.RenderSub = "sub_22";
        live.AttachClientId = "conn_47";
        var cube = new SidebarCubeItem
        {
            Id = "plc_docker",
            Name = "docker",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        AttachSession.ApplyCubesConnectOutcome(
            live,
            port,
            cube,
            RetargetOutcome(new ControlPlaneClient("/tmp/hypa-dest-observe.sock")));

        Assert.Equal("sub_dest", AttachSession.OwnedControlSubscriptionId(live));
        Assert.Equal("conn_47", live.AttachClientId);
        Assert.Equal("pane_d", live.PaneId);
        Assert.Equal("dest_input", live.InputLease);

        await AttachSession.PublishVisibleSetAsync(
            port,
            live,
            ["pane_d"],
            CancellationToken.None);
        await AttachSession.ObserveVisiblePanesAsync(port, live, CancellationToken.None);

        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalVisibleSet
                && call.Params?["subscription_id"]?.GetValue<string>() == "sub_dest");
        Assert.DoesNotContain(
            port.Calls,
            call => call.Params?["subscription_id"]?.GetValue<string>() == "sub_22");
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalObserve
                && call.Params?["subscription_id"]?.GetValue<string>() == "sub_dest"
                && call.Params?["pane_id"]?.GetValue<string>() == "pane_d");

        var controlCalls = new List<(string Pane, string Lease, string Sub)>();
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();
        await AttachSession.RefreshFocusControlAsync(
            (pane, lease, sub, _) =>
            {
                controlCalls.Add((pane, lease, sub));
                return Task.CompletedTask;
            },
            gate,
            live,
            tty: null,
            linked,
            CancellationToken.None);

        Assert.Contains(
            controlCalls,
            call => call.Sub == "sub_dest"
                && call.Pane == "pane_d"
                && call.Lease == "dest_input");
        Assert.DoesNotContain(controlCalls, call => call.Sub == "sub_22");
    }

    [Fact]
    public async Task Local_restore_observes_without_source_lease_claim()
    {
        var sourcePort = new MouseRecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.RuntimeLeaseClaim)
                {
                    var scope = parameters?["scope"]?.GetValue<string>();
                    return MouseTestGeom.Parse(
                        $"{{\"outcome\":\"granted\",\"lease_id\":\"src_{scope}_fresh\"}}");
                }

                return MouseTestGeom.Parse("{}");
            },
        };
        var live = MouseTestGeom.ApplyLive(sourcePort, MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_docker";
        live.InputLease = "dest_input";
        live.ResizeLease = "dest_resize";
        live.ControlSub = "sub_dest";
        live.RenderSub = "sub_dest";
        var dest = new ControlPlaneClient("/tmp/hypa-restore-dest.sock");
        live.ControlSlot = new AttachControlSlot { Client = dest };
        var source = new ControlPlaneClient("/tmp/hypa-restore-source.sock");
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = sourcePort,
            ControlSub = "sub_source",
            RenderSub = "sub_source",
            AttachClientId = "conn_47",
            PaneId = "p1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PlacementDisplayName = "local",
        };
        var local = new SidebarCubeItem
        {
            Id = "plc_local",
            Name = "local",
            Kind = SidebarCubeKind.Local,
            Reachability = SidebarCubeReachability.Local,
        };
        live.Cubes = [local];

        await AttachSession.RestoreLocalCubeProjectionAsync(
            live,
            local,
            sourcePort,
            tty: null,
            CancellationToken.None);

        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
        Assert.False(live.InputFrozen);
        Assert.False(live.Renew.IsTracked("src_input"));
        Assert.False(live.Renew.IsTracked("src_resize"));
        Assert.Same(source, live.ControlSlot!.Client);
        Assert.Equal("sub_source", live.ControlSub);
        Assert.DoesNotContain(
            sourcePort.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseClaim);
        Assert.Contains(
            sourcePort.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseRelease
                && call.Params?["lease_id"]?.GetValue<string>() == "src_input");
        Assert.True(SpinWaitUntilDisposed(dest));
        Assert.False(source.IsDisposed);
    }

    [Fact]
    public async Task Disposed_source_stays_unavailable_and_clears_dest_routing()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-restore-gone-dest.sock");
        var source = new ControlPlaneClient("/tmp/hypa-restore-gone-source.sock");
        await source.DisposeAsync();
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.ControlSub = "sub_dest";
        live.RenderSub = "sub_dest";
        live.ConnectedPlacementId = "plc_docker";
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = port,
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PaneId = "p1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
        };
        var local = new SidebarCubeItem
        {
            Id = "plc_local",
            Name = "local",
            Kind = SidebarCubeKind.Local,
            Reachability = SidebarCubeReachability.Local,
        };
        live.Cubes = [local];

        await AttachSession.RestoreLocalCubeProjectionAsync(
            live,
            local,
            port,
            tty: null,
            CancellationToken.None);

        Assert.Equal("the previous endpoint is no longer connected", live.StatusError);
        Assert.Null(live.ControlSlot);
        Assert.True(string.IsNullOrEmpty(live.ControlSub));
        Assert.True(live.InputFrozen);
        Assert.True(live.PresentationFrozen);
        Assert.NotSame(dest, live.ControlSlot?.Client);
        Assert.True(SpinWaitUntilDisposed(dest));
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void RestoringSource_complete_observes_without_source_lease_claim()
    {
        var sourcePort = new MouseRecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.RuntimeLeaseClaim)
                {
                    var scope = parameters?["scope"]?.GetValue<string>();
                    return MouseTestGeom.Parse(
                        $"{{\"outcome\":\"granted\",\"lease_id\":\"src_{scope}_fresh\"}}");
                }

                return MouseTestGeom.Parse("{}");
            },
        };
        var live = MouseTestGeom.ApplyLive(sourcePort, MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_docker";
        live.InputLease = "dest_input";
        live.ResizeLease = "dest_resize";
        live.ControlSub = "sub_dest";
        live.RenderSub = "sub_dest";
        live.Renew.Untrack("lease-in");
        live.Renew.Untrack("lease-r");
        live.Renew.Track("src_input");
        live.Renew.Track("src_resize");
        var dest = new ControlPlaneClient("/tmp/hypa-restoring-set-active-dest.sock");
        live.ControlSlot = new AttachControlSlot { Client = dest };
        var source = new ControlPlaneClient("/tmp/hypa-restoring-set-active-source.sock");
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = sourcePort,
            ControlSub = "sub_source",
            RenderSub = "sub_source",
            AttachClientId = "conn_47",
            PaneId = "p1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PlacementDisplayName = "local",
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                new EndpointActivationLease
                {
                    EndpointId = "plc_local",
                    ConnectionGeneration = 1,
                    BootId = "local-boot",
                    MinimumProjectionRevision = 0,
                    ClientId = "attach-client",
                    LeaseId = "src_input",
                },
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource("endpoint handoff was rolled back", null)),
            new EndpointActivationLease
            {
                EndpointId = "plc_local",
                ConnectionGeneration = 1,
                BootId = "local-boot",
                MinimumProjectionRevision = 0,
                ClientId = "attach-client",
                LeaseId = "src_input",
            },
            new EndpointActivationLease
            {
                EndpointId = "plc_docker",
                ConnectionGeneration = 7,
                BootId = "dest-boot",
                MinimumProjectionRevision = 0,
                ClientId = "attach-client",
                LeaseId = "dest_input",
            });

        Assert.True(AttachSession.ApplyEndpointActivationSetActive(
            live,
            "plc_local",
            "plc_local",
            "plc_docker"));
        Assert.True(string.IsNullOrEmpty(live.InputLease));
        Assert.True(string.IsNullOrEmpty(live.ResizeLease));

        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
        Assert.False(live.Renew.IsTracked("src_input"));
        Assert.False(live.Renew.IsTracked("src_resize"));
        Assert.Same(source, live.ControlSlot!.Client);
        Assert.False(live.InputFrozen);
        Assert.False(live.PresentationFrozen);

        Assert.True(AttachSession.ApplyEndpointActivationSetActive(
            live,
            "plc_local",
            "plc_local",
            "plc_docker"));
        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
    }

    [Fact]
    public async Task ApplyEndpointActivationSetActive_disposed_source_clears_dest_routing()
    {
        var dest = new ControlPlaneClient("/tmp/hypa-set-active-gone-dest.sock");
        var source = new ControlPlaneClient("/tmp/hypa-set-active-gone-source.sock");
        await source.DisposeAsync();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSlot = new AttachControlSlot { Client = dest };
        live.ControlSub = "sub_dest";
        live.RenderSub = "sub_dest";
        live.ConnectedPlacementId = "plc_docker";
        live.InputLease = "dest_input";
        live.ResizeLease = "dest_resize";
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = MouseTestGeom.ApplyPort(),
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PaneId = "p1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
        };

        Assert.False(AttachSession.ApplyEndpointActivationSetActive(
            live,
            "plc_local",
            "plc_local",
            "plc_docker"));
        Assert.Equal("the previous endpoint is no longer connected", live.StatusError);
        Assert.Null(live.ControlSlot);
        Assert.True(string.IsNullOrEmpty(live.ControlSub));
        Assert.True(live.InputFrozen);
        Assert.True(live.PresentationFrozen);
        Assert.NotSame(dest, live.ControlSlot?.Client);
    }

    [Fact]
    public async Task Attach_start_does_not_dest_connect_from_prefs()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        var retargeter = new CatalogConnectProbe();
        live.CubesConnect = retargeter;
        live.SelectedCubeId = "plc_docker";
        live.ConnectedPlacementId = "plc_local";
        live.ActiveProjectionEndpointId = "plc_local";
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_local",
                Name = "local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = "plc_docker",
                Name = "docker",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];

        await AttachSession.MaybeRestoreSelectedCubeConnectAsync(
            port,
            live,
            tty: null,
            CancellationToken.None);

        Assert.False(retargeter.Called);
        Assert.Equal("plc_local", live.ActiveProjectionEndpointId);
        Assert.Equal("plc_docker", live.SelectedCubeId);
        Assert.True(live.RestoredCubeConnectAttempted);
    }

    [Fact]
    public void Restore_one_shot_is_not_burned_when_no_cube_is_returned()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.SelectedCubeId = "plc_missing";
        live.Cubes = [];
        Assert.False(AttachSession.TryResolveRestoredCubeConnect(live, out var cube));
        Assert.Null(cube);
        Assert.False(live.RestoredCubeConnectAttempted);
    }

    [Fact]
    public void Selected_unconnected_cube_does_not_steal_painted_pane()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "plc_local";
        live.ActiveProjectionEndpointId = "plc_local";
        using var localSnap = JsonDocument.Parse("""{"focused_workspace_id":"ws-local"}""");
        live.LastSnapshot = localSnap.RootElement.Clone();
        AttachSession.RememberCubeSnapshot(live, "plc_local", localSnap.RootElement);
        using var destSnap = JsonDocument.Parse("""{"focused_workspace_id":"ws-docker"}""");
        AttachSession.RememberCubeSnapshot(live, "plc_docker", destSnap.RootElement);
        var docker = new SidebarCubeItem
        {
            Id = "plc_docker",
            Name = "docker",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_local",
                Name = "local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            docker,
        ];

        AttachSession.SelectCube(live, docker);

        Assert.Equal("plc_docker", live.SelectedCubeId);
        Assert.Equal("plc_local", live.ActiveProjectionEndpointId);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        var status = CubesChromeSectionStrategy.TrailingStatus(
            docker,
            live.SelectedCubeId,
            live.ConnectedPlacementId);
        Assert.Equal(CubeTrailingStatusKind.Connecting, status.Kind);
    }

    [Fact]
    public void Stale_ssh_generation_at_begin_disposes_without_claim()
    {
        var registry = new SshPlacementEndpointRegistry();
        var disposed = false;
        var endpoint = new SshAttachEndpoint(RemotePath("/tmp/hypa-stale-ssh.sock", 1, () => disposed = true));
        var generation = registry.RegisterConnect("plc_ssh", endpoint);
        registry.Retire("plc_ssh");
        Assert.False(registry.IsConnectActive("plc_ssh", generation, endpoint));
        Assert.True(disposed);
    }

    private sealed class CatalogConnectProbe : ICubesConnectRetargeter
    {
        public bool Called { get; private set; }

        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(CubesConnectRetargetOutcome.LocalNoop(
                request.Destination,
                sourceMuxAlive: true,
                paneProcessCommands: [],
                processStartCount: 0,
                processStartCommands: []));
        }
    }

    private static RemoteMuxPath RemotePath(string socket, ulong generation, Action onDispose) =>
        new()
        {
            LocalSocketPath = socket,
            Session = "agents",
            Target = "user@dev",
            Generation = generation,
            DisposeAsyncAction = () =>
            {
                onDispose();
                return ValueTask.CompletedTask;
            },
        };
}
