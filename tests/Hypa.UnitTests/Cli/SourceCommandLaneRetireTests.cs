using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SourceCommandLaneRetireTests
{
    private const string SourceEndpoint = "plc_local";
    private const string DestEndpoint = "plc_peer";
    private const string SourceBoot = "local-boot";
    private const string DestBoot = "dest-boot";
    private const string ProbeMethod = "lane.probe";
    private const string OtherRpcMethod = "runtime.status";

    // The source acks focus-off and surface-off independently. Only the
    // surface-off ack gates the handoff, so a late focus-off ack can land
    // after the target activation has started. Cover both orders.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffRetireCancelsSourceShellLaneAndLeavesTransportsRunning(bool focusAckAfterTargetStarts)
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await using var sourcePeer = ScriptedNdjsonPeer.Listen(ownsPane: true);
        var panePid = sourcePeer.PanePid;
        await using var destPeer = ScriptedNdjsonPeer.Listen();
        await using var source = new ControlPlaneClient(
            sourcePeer.Path,
            connectTimeout: TimeSpan.FromSeconds(2),
            callTimeout: TimeSpan.FromSeconds(5));
        await using var dest = new ControlPlaneClient(
            destPeer.Path,
            connectTimeout: TimeSpan.FromSeconds(2),
            callTimeout: TimeSpan.FromSeconds(5));
        var sourceAccept = sourcePeer.AcceptAsync();
        var destAccept = destPeer.AcceptAsync();
        await source.ConnectAsync();
        await dest.ConnectAsync();
        await sourceAccept;
        await destAccept;

        var probe = await source.CallAsync(ProbeMethod);
        Assert.Equal("probe", probe.GetProperty("marker").GetString());

        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = SourceEndpoint;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.NextSurfaceSerial = 1;
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = SourceBoot;
        live.TransportEnvelope.StampServerGeneration(2);
        live.ConnectedBootId = SourceBoot;
        using var snapshot = JsonDocument.Parse(
            "{\"boot_id\":\"local-boot\",\"projection_revision\":1}");
        AttachSession.RebuildSidebar(
            live,
            snapshot.RootElement,
            requestGit: false,
            hydratePicker: false);
        Assert.Equal(SourceBoot, live.EndpointCommands.SnapshotBootId);
        live.SetEndpointSurfaceActive(SourceEndpoint, true);
        live.SetEndpointSurfaceActive(DestEndpoint, true);
        var sourcePort = new ControlPlaneAttachCommandPort(source);
        var destPort = new ControlPlaneAttachCommandPort(dest);
        sourcePort.UseShellLane(live);
        destPort.UseShellLane(live);
        live.Dispatcher.RebindPort(sourcePort);
        var sourceDispatcher = live.Dispatcher;
        var destDispatcher = new AttachCommandDispatcher(
            destPort,
            "w1",
            "t1",
            live.PaneId,
            "lease-r");
        var peerCube = new SidebarCubeItem
        {
            Id = DestEndpoint,
            Name = "Peer",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ConnectEnabled = true,
        };
        live.Cubes = [peerCube];
        live.CubesConnect = new ScriptedRetargeter(RetargetOutcome(dest));

        try
        {
            live.ActiveProjectionEndpointId = SourceEndpoint;
            await AssertShellReplyCompletesWhileRenderLoopBlockedOnGateAsync(live, sourcePort, source, sourcePeer);
            await AssertAccumulatedChunksAreTheCallerResultAsync(live, sourcePort, sourcePeer);
            live.ActiveProjectionEndpointId = DestEndpoint;
            var destZoom = destDispatcher.HandleAsync(
                    new KeyActionRequest(KeyActionId.Zoom),
                    CancellationToken.None)
                .AsTask();
            Assert.True(
                SpinWait.SpinUntil(
                    () => destPeer.Count(ProtocolMethods.PaneZoom) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", destPeer.Methods()));
            var destSplit = destDispatcher.HandleAsync(
                    new KeyActionRequest(KeyActionId.SplitVertical),
                    CancellationToken.None)
                .AsTask();
            Assert.Equal(0, destPeer.Count(ProtocolMethods.PaneSplit));

            var destZoomId = destPeer.RequestId(ProtocolMethods.PaneZoom);
            live.ControlSlot = new AttachControlSlot { Client = dest };
            await using (var destZoomLoop = RenderLoopRun.Start(live, dest))
            {
                await destPeer.InjectAsync(
                    "{\"id\":\"" + destZoomId
                    + "\",\"error\":{\"code\":12,\"message\":\"pane is busy\",\"data\":{\"error_code\":\"pane_busy\"}}}");
                var admitted = await Task.WhenAny(destZoom, Task.Delay(TimeSpan.FromSeconds(3)))
                    .ConfigureAwait(false);
                Assert.Same(destZoom, admitted);
                _ = destZoomLoop;
            }

            live.ControlSlot = new AttachControlSlot { Client = source };
            var destZoomError = await Assert.ThrowsAsync<ControlPlaneException>(() => destZoom);
            Assert.Equal("pane_busy", destZoomError.ErrorCode);
            Assert.NotEqual(AttachEndpointCommands.CancelledCode, destZoomError.ErrorCode);
            Assert.True(
                SpinWait.SpinUntil(
                    () => destPeer.Count(ProtocolMethods.PaneSplit) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", destPeer.Methods()));

            live.ActiveProjectionEndpointId = SourceEndpoint;
            var sourceZoom = sourceDispatcher.HandleAsync(
                    new KeyActionRequest(KeyActionId.Zoom),
                    CancellationToken.None)
                .AsTask();
            Assert.True(
                SpinWait.SpinUntil(
                    () => sourcePeer.Count(ProtocolMethods.PaneZoom) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", sourcePeer.Methods()));
            var sourceSplit = sourceDispatcher.HandleAsync(
                    new KeyActionRequest(KeyActionId.SplitVertical),
                    CancellationToken.None)
                .AsTask();
            var sourceRatio = sourceDispatcher.SetSplitRatioAsync([0], 0.4, CancellationToken.None);
            var reload = sourcePort.CallAsync(
                ProtocolMethods.ServerReloadConfig,
                new System.Text.Json.Nodes.JsonObject(),
                CancellationToken.None);
            var integrationList = sourcePort.CallAsync(
                ProtocolMethods.IntegrationList,
                null,
                CancellationToken.None);
            var integrationInstall = sourcePort.CallAsync(
                ProtocolMethods.IntegrationInstall,
                new System.Text.Json.Nodes.JsonObject { ["target"] = "claude" },
                CancellationToken.None);
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.PaneSplit));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.LayoutSetSplitRatio));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.ServerReloadConfig));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.IntegrationList));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.IntegrationInstall));

            var otherRpc = source.CallAsync(
                OtherRpcMethod,
                new System.Text.Json.Nodes.JsonObject { ["marker"] = "other-rpc" });
            Assert.True(
                SpinWait.SpinUntil(
                    () => sourcePeer.Count(OtherRpcMethod) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", sourcePeer.Methods()));
            Assert.Equal(1, source.PendingCallCount);

            await AttachSession.ApplyCubesConnectAsync(
                new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: DestEndpoint),
                live,
                sourcePort,
                tty: null,
                CancellationToken.None);
            AssertPaneSurvived(sourcePeer, panePid, live);
            await AssertSeparatePtyChildrenExitAsync();
            await AttachSession.FlushDestConnectPrepForTests(live);
            AssertPaneSurvived(sourcePeer, panePid, live);
            if (live.HealthMonitor is not null)
                await live.HealthMonitor.DisposeAsync();

            Assert.True(
                SpinWait.SpinUntil(
                    () => sourcePeer.Count(AttachEndpointProtocol.Focus) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", sourcePeer.Methods()));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.PaneSplit));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.LayoutSetSplitRatio));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.ServerReloadConfig));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.IntegrationList));
            Assert.Equal(0, sourcePeer.Count(ProtocolMethods.IntegrationInstall));
            Assert.True(
                SpinWait.SpinUntil(
                    () => sourcePeer.Count(AttachEndpointProtocol.SurfaceInterest) == 1,
                    TimeSpan.FromSeconds(3)),
                string.Join(",", sourcePeer.Methods()));
            Assert.Equal(3, source.PendingCallCount);
            Assert.False(dest.IsDisposed);
            Assert.False(source.IsDisposed);
            Assert.False(otherRpc.IsCompleted);
            Assert.False(destSplit.IsCompleted);
            lock (live.ActivationGate)
                Assert.Empty(live.ActivationCompletions);
            Assert.IsType<ActivationPhase.ReleasingSource>(live.PendingActivation!.Phase);
            Assert.False(source.ReanchorRequired);

            var sourceZoomError = await Assert.ThrowsAsync<ControlPlaneException>(() => sourceZoom);
            var sourceSplitError = await Assert.ThrowsAsync<ControlPlaneException>(() => sourceSplit);
            var sourceRatioError = await Assert.ThrowsAsync<ControlPlaneException>(() => sourceRatio);
            var reloadError = await Assert.ThrowsAsync<ControlPlaneException>(() => reload);
            var listError = await Assert.ThrowsAsync<ControlPlaneException>(() => integrationList);
            var installError = await Assert.ThrowsAsync<ControlPlaneException>(() => integrationInstall);
            Assert.Equal(AttachEndpointCommands.CancelledCode, sourceZoomError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.CancelledCode, sourceSplitError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.CancelledCode, sourceRatioError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.CancelledCode, reloadError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.CancelledCode, listError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.CancelledCode, installError.ErrorCode);
            Assert.Equal(AttachEndpointCommands.InterruptedMessage, reloadError.Message);
            Assert.Equal(AttachEndpointCommands.InterruptedMessage, sourceZoomError.Message);
            Assert.Equal(sourceZoomError.Message, sourceSplitError.Message);
            Assert.IsNotType<ControlPlaneClientTimeoutException>(sourceZoomError);

            var cancelled = live.EndpointCommands.Outcomes;
            var inFlight = Assert.Single(
                cancelled,
                outcome => outcome.MethodName == ProtocolMethods.PaneZoom
                    && outcome.Code == AttachEndpointCommands.CancelledCode);
            var queued = Assert.Single(
                cancelled,
                outcome => outcome.MethodName == ProtocolMethods.PaneSplit
                    && outcome.Code == AttachEndpointCommands.CancelledCode);
            var ratio = Assert.Single(
                cancelled,
                outcome => outcome.MethodName == ProtocolMethods.LayoutSetSplitRatio);
            Assert.Equal(AttachEndpointCommands.CancelledCode, ratio.Code);
            Assert.Equal(
                AttachEndpointCommands.CancelledCode,
                Assert.Single(cancelled, outcome => outcome.MethodName == ProtocolMethods.ServerReloadConfig).Code);
            Assert.Equal(
                AttachEndpointCommands.CancelledCode,
                Assert.Single(cancelled, outcome => outcome.MethodName == ProtocolMethods.IntegrationList).Code);
            Assert.Equal(
                AttachEndpointCommands.CancelledCode,
                Assert.Single(cancelled, outcome => outcome.MethodName == ProtocolMethods.IntegrationInstall).Code);
            var busy = Assert.Single(cancelled, outcome => outcome.Code == "pane_busy");
            Assert.Equal(ProtocolMethods.PaneZoom, busy.MethodName);
            Assert.Equal(0, inFlight.FollowUpActions);
            Assert.Equal(0, queued.FollowUpActions);
            Assert.DoesNotContain(
                cancelled,
                outcome => outcome.RequestId.StartsWith("client-shell-", StringComparison.Ordinal));

            var reliable = await NextEventAsync(source);
            Assert.Contains("kept-before-retire", reliable.GetRawText(), StringComparison.Ordinal);
            var render = await NextEventAsync(source);
            Assert.Contains("frame-before-retire", render.GetRawText(), StringComparison.Ordinal);

            var shellOutcomes = live.EndpointCommands.Outcomes.Count;
            live.ControlSlot!.Client = source;
            await using (var sourceReads = RenderLoopRun.Start(live, source))
            {
                await InjectAndWaitForShellAdmitAsync(
                    live,
                    sourcePeer,
                    "{\"id\":\"" + inFlight.RequestId + "\",\"final_chunk\":false,\"result\":{\"partial\":true}}");
                Assert.True(live.EndpointCommands.HasTombstone(SourceEndpoint, inFlight.RequestId));

                await InjectAndWaitForShellAdmitAsync(
                    live,
                    sourcePeer,
                    "{\"id\":\"" + inFlight.RequestId + "\",\"final_chunk\":false,\"result\":{\"more\":true}}");
                Assert.True(live.EndpointCommands.HasTombstone(SourceEndpoint, inFlight.RequestId));
                Assert.Equal(AttachEndpointCommands.CancelledCode, inFlight.Code);
                Assert.Equal(shellOutcomes, live.EndpointCommands.Outcomes.Count);

                await InjectAndWaitForShellAdmitAsync(
                    live,
                    sourcePeer,
                    "{\"id\":\"" + inFlight.RequestId + "\",\"final_chunk\":true,\"result\":{\"ok\":true}}");
                Assert.False(live.EndpointCommands.HasTombstone(SourceEndpoint, inFlight.RequestId));

                var reads = Volatile.Read(ref live.RenderLoopReads);
                await sourcePeer.InjectAsync(
                    "{\"id\":\"" + inFlight.RequestId + "\",\"final_chunk\":false,\"result\":{\"partial\":true}}");
                await sourceReads.WaitForReadAsync(reads + 1);
                Assert.False(live.EndpointCommands.HasTombstone(SourceEndpoint, inFlight.RequestId));
                Assert.Equal(shellOutcomes, live.EndpointCommands.Outcomes.Count);
                Assert.Equal(AttachEndpointCommands.CancelledCode, inFlight.Code);
            }

            live.ControlSlot.Client = dest;
            await using (var destReads = RenderLoopRun.Start(live, dest))
            {
                await destPeer.ReleaseAsync();
                await destSplit.WaitAsync(TimeSpan.FromSeconds(3));
                _ = destReads;
            }

            Assert.True(destSplit.IsCompletedSuccessfully);
            var destSplitOutcome = Assert.Single(
                live.EndpointCommands.Outcomes,
                outcome => outcome.MethodName == ProtocolMethods.PaneSplit && outcome.Code is null);
            Assert.Equal(0, destSplitOutcome.FollowUpActions);
            Assert.Equal(AttachEndpointCommands.CancelledCode, queued.Code);
            Assert.Equal("pane_busy", busy.Code);

            var outcomesAfterDest = live.EndpointCommands.Outcomes.Count;
            live.ControlSlot.Client = source;
            if (focusAckAfterTargetStarts)
                await ReleaseFocusAckAfterTargetStartsAsync(live, sourcePeer);
            else
                await ReleaseRetireAcksTogetherAsync(live, source, sourcePeer);

            Assert.Equal(AttachEndpointCommands.CancelledCode, inFlight.Code);
            Assert.Equal(outcomesAfterDest, live.EndpointCommands.Outcomes.Count);
            var otherResult = await otherRpc.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("other-rpc", otherResult.GetProperty("marker").GetString());

            // Read phase and queue together. Reply continuations can still
            // enqueue after the loop stops, so a second read could disagree.
            ActivationPhase? phase;
            List<EndpointActivationRpcCompletion> queuedCompletions;
            lock (live.ActivationGate)
            {
                phase = live.PendingActivation?.Phase;
                queuedCompletions = live.ActivationCompletions.ToList();
            }

            // Only the surface-off ack gates the release (AcceptsResponse in
            // ReleasingSource matches the surface-off id). The focus-off ack is
            // fire-and-forget: it can be enqueued before or after the handoff
            // moves to the target, so check whichever retire replies are queued
            // by id, never by position.
            var retireFocus = queuedCompletions
                .OfType<EndpointActivationRpcCompletion.Control>()
                .SingleOrDefault(c => c.Result.RequestId.EndsWith(":off", StringComparison.Ordinal));
            if (retireFocus is not null)
            {
                Assert.Null(retireFocus.Result.Error);
                Assert.StartsWith("client-shell-focus:", retireFocus.Result.RequestId, StringComparison.Ordinal);
                Assert.Equal(SourceBoot, retireFocus.Result.BootId);
            }

            var retireSurface = queuedCompletions
                .OfType<EndpointActivationRpcCompletion.Surface>()
                .SingleOrDefault(c => c.RequestId == RetireSurfaceRequestId);
            if (phase is ActivationPhase.ReleasingSource)
            {
                // Nothing processed the release yet: both retire replies are queued.
                Assert.NotNull(retireFocus);
                Assert.NotNull(retireSurface);
                Assert.Equal(RetireSurfaceRequestId, retireSurface.Result.RequestId);
                Assert.False(retireSurface.Result.Active);
                Assert.Equal(SourceBoot, retireSurface.Result.BootId);
            }
            else
            {
                // The release was handled and the target activation started.
                // Any surface reply still queued is the target's surface-on.
                Assert.IsType<ActivationPhase.ActivatingTarget>(phase);
                Assert.Null(retireSurface);
                Assert.All(
                    queuedCompletions.OfType<EndpointActivationRpcCompletion.Surface>(),
                    c =>
                    {
                        Assert.Equal(DestEndpoint, c.EndpointId);
                        Assert.EndsWith(":on", c.RequestId, StringComparison.Ordinal);
                    });
            }

            Assert.False(dest.IsDisposed);
            Assert.False(source.IsDisposed);
            AssertPaneSurvived(sourcePeer, panePid, live);
            Assert.True(
                phase is ActivationPhase.ReleasingSource or ActivationPhase.ActivatingTarget,
                DumpState(live));
        }
        finally
        {
            if (live.HealthMonitor is not null)
            {
                await live.HealthMonitor.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The render loop handles the source replies. Either the two retire
    /// completions are still queued, or the loop already handled the release
    /// and the handoff moved on to the target. Wait for one of the two.
    /// </summary>
    private static async Task ReleaseRetireAcksTogetherAsync(
        AttachLiveState live,
        ControlPlaneClient source,
        ScriptedNdjsonPeer sourcePeer)
    {
        await using var lateReads = RenderLoopRun.Start(live, source);
        var reads = Volatile.Read(ref live.RenderLoopReads);
        await sourcePeer.ReleaseAsync();
        await lateReads.WaitForReadAsync(reads + 1);
        Assert.True(
            SpinWait.SpinUntil(() => RetireCompletionsQueued(live) || RetireCompletionsHandled(live), TimeSpan.FromSeconds(10)),
            DumpState(live));
    }

    /// <summary>
    /// Pin the order CI hit: the surface-off ack is handled on its own, the
    /// target activation starts and its surface-on reply is queued, and only
    /// then does the focus-off ack arrive. The client's read loop delivers
    /// replies; only the render loop or pump drains completions, and neither
    /// runs here, so each step is explicit.
    /// </summary>
    private static async Task ReleaseFocusAckAfterTargetStartsAsync(
        AttachLiveState live,
        ScriptedNdjsonPeer sourcePeer)
    {
        await sourcePeer.ReleaseAsync(line => line.Contains(RetireSurfaceRequestId, StringComparison.Ordinal));
        Assert.True(
            SpinWait.SpinUntil(
                () => Queued<EndpointActivationRpcCompletion.Surface>(live, c => c.RequestId == RetireSurfaceRequestId),
                TimeSpan.FromSeconds(10)),
            DumpState(live));

        AttachSession.ProcessActivationCompletionsForPump(live, tty: null);
        Assert.True(RetireCompletionsHandled(live), DumpState(live));
        Assert.True(
            SpinWait.SpinUntil(
                () => Queued<EndpointActivationRpcCompletion.Surface>(live, c => c.EndpointId == DestEndpoint),
                TimeSpan.FromSeconds(10)),
            DumpState(live));

        await sourcePeer.ReleaseAsync();
        Assert.True(
            SpinWait.SpinUntil(
                () => Queued<EndpointActivationRpcCompletion.Control>(
                    live,
                    c => c.EndpointId == SourceEndpoint && c.Result.RequestId.EndsWith(":off", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)),
            DumpState(live));
    }

    private static bool Queued<T>(AttachLiveState live, Func<T, bool> match)
        where T : EndpointActivationRpcCompletion
    {
        lock (live.ActivationGate)
            return live.ActivationCompletions.OfType<T>().Any(match);
    }

    private const string RetireSurfaceRequestId = "client-shell-surface:1:off";

    private static bool RetireCompletionsQueued(AttachLiveState live)
    {
        lock (live.ActivationGate)
        {
            return live.ActivationCompletions
                    .OfType<EndpointActivationRpcCompletion.Surface>()
                    .Any(c => c.RequestId == RetireSurfaceRequestId)
                && live.ActivationCompletions
                    .OfType<EndpointActivationRpcCompletion.Control>()
                    .Any(c => c.Result.RequestId.EndsWith(":off", StringComparison.Ordinal));
        }
    }

    private static bool RetireCompletionsHandled(AttachLiveState live) =>
        live.PendingActivation?.Phase is ActivationPhase.ActivatingTarget;

    private static string DumpState(AttachLiveState live)
    {
        lock (live.ActivationGate)
        {
            var queued = live.ActivationCompletions.Select(c => c switch
            {
                EndpointActivationRpcCompletion.Control k => "focus:" + k.Result.RequestId,
                EndpointActivationRpcCompletion.Surface u => "surface:" + u.RequestId,
                _ => c.GetType().Name,
            });
            return "phase=" + live.PendingActivation?.Phase.GetType().Name
                + " completions=[" + string.Join(",", queued) + "]"
                + " outcomes=" + live.EndpointCommands.Outcomes.Count;
        }
    }

    /// <summary>
    // Start the render loop, let it
    /// read one non-shell event, and hold the control gate so that loop stays
    /// blocked. <c>tab.focus</c> must finish before the gate is released, and
    /// the loop must not read that reply.
    /// </summary>
    private static async Task AssertShellReplyCompletesWhileRenderLoopBlockedOnGateAsync(
        AttachLiveState live,
        ControlPlaneAttachCommandPort port,
        ControlPlaneClient client,
        ScriptedNdjsonPeer peer)
    {
        var parked = new List<string>();
        var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < drainDeadline)
        {
            while (client.TryTakeQueuedEvent(out var queued))
                parked.Add(queued.GetRawText());
            if (parked.Count >= 2)
                break;
            await Task.Delay(20).ConfigureAwait(false);
        }

        var gate = new SemaphoreSlim(1, 1);
        var reads = RenderLoopRun.Start(live, client, gate);
        var held = false;
        try
        {
            var before = Volatile.Read(ref live.RenderLoopReads);
            await gate.WaitAsync().ConfigureAwait(false);
            held = true;
            await peer.InjectAsync(
                "{\"event\":\"session.lifecycle\",\"params\":{\"lane\":\"control\",\"marker\":\"gate-block\"}}")
                .ConfigureAwait(false);
            var readDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < readDeadline
                && Volatile.Read(ref live.RenderLoopReads) < before + 1)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            Assert.Equal(before + 1, Volatile.Read(ref live.RenderLoopReads));
            var pumped = live.EndpointCommands.PumpedReplyCount;
            var call = port.CallAsync(
                ProtocolMethods.TabFocus,
                new System.Text.Json.Nodes.JsonObject { ["tab_id"] = "tab-other" },
                CancellationToken.None);
            var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3)))
                .ConfigureAwait(false);
            Assert.True(
                call.IsCompleted && ReferenceEquals(finished, call),
                "tab.focus completed only after the control gate could be released");
            _ = await call.ConfigureAwait(false);
            Assert.True(
                live.EndpointCommands.PumpedReplyCount > pumped,
                "tab.focus completed without the socket-reader shell admit");
            Assert.Equal(before + 1, Volatile.Read(ref live.RenderLoopReads));
            Assert.False(gate.Wait(0));
        }
        finally
        {
            if (held)
                gate.Release();
            await reads.DisposeAsync().ConfigureAwait(false);
            gate.Dispose();
            foreach (var line in parked)
                await peer.InjectAsync(line).ConfigureAwait(false);
        }
    }

    /// <summary>
    // Two chunks form one
    /// body. The caller result uses bytes that exist only in the earlier chunk.
    /// A non-final chunk does not complete the wait. A server error in the
    /// complete body stays an error.
    /// </summary>
    private static async Task AssertAccumulatedChunksAreTheCallerResultAsync(
        AttachLiveState live,
        ControlPlaneAttachCommandPort port,
        ScriptedNdjsonPeer peer)
    {
        peer.DropReplies(ProtocolMethods.TabFocus);
        try
        {
            var success = "{\"id\":\"pending\",\"result\":{\"marker\":\"from-earlier\"}}";
            var successResult = await CallWithSplitBodyAsync(live, port, peer, success, "from-earlier")
                .ConfigureAwait(false);
            Assert.Null(successResult.Error);
            Assert.Equal("from-earlier", successResult.Result.GetProperty("marker").GetString());

            var error = "{\"id\":\"pending\",\"error\":{\"code\":1,\"message\":\"busy\",\"data\":{\"error_code\":\"action_rejected\"}}}";
            var rejected = await CallWithSplitBodyAsync(live, port, peer, error, "action_rejected")
                .ConfigureAwait(false);
            var rejectedError = Assert.IsType<ControlPlaneException>(rejected.Error);
            Assert.Equal("action_rejected", rejectedError.ErrorCode);
            Assert.NotEqual(AttachEndpointCommands.CancelledCode, rejectedError.ErrorCode);
        }
        finally
        {
            peer.AllowReplies(ProtocolMethods.TabFocus);
        }
    }

    private static async Task<ControlPlaneCallResult> CallWithSplitBodyAsync(
        AttachLiveState live,
        ControlPlaneAttachCommandPort port,
        ScriptedNdjsonPeer peer,
        string body,
        string earlierOnly)
    {
        var seen = peer.Count(ProtocolMethods.TabFocus);
        var call = port.CallWithRequestIdAsync(
            ProtocolMethods.TabFocus,
            new System.Text.Json.Nodes.JsonObject { ["tab_id"] = "tab-chunk" },
            CancellationToken.None);
        Assert.True(
            SpinWait.SpinUntil(
                () => peer.Count(ProtocolMethods.TabFocus) == seen + 1,
                TimeSpan.FromSeconds(3)));
        var requestId = peer.RequestId(ProtocolMethods.TabFocus);
        var rewritten = body.Replace("pending", requestId, StringComparison.Ordinal);
        var cut = rewritten.IndexOf(earlierOnly, StringComparison.Ordinal);
        Assert.True(cut >= 0);
        cut += earlierOnly.Length;
        var head = rewritten[..cut];
        var tail = rewritten[cut..];
        Assert.DoesNotContain(earlierOnly, tail, StringComparison.Ordinal);

        var pumped = live.EndpointCommands.PumpedReplyCount;
        await peer.InjectAsync(ChunkLine(requestId, finalChunk: false, head)).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && live.EndpointCommands.PumpedReplyCount <= pumped)
            await Task.Delay(20).ConfigureAwait(false);
        Assert.True(live.EndpointCommands.PumpedReplyCount > pumped);
        Assert.False(call.IsCompleted);

        await peer.InjectAsync(ChunkLine(requestId, finalChunk: true, tail)).ConfigureAwait(false);
        return await call.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    private static string ChunkLine(string requestId, bool finalChunk, string fragment)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(fragment);
        return "{\"id\":\"" + requestId
            + "\",\"final_chunk\":" + (finalChunk ? "true" : "false")
            + ",\"data\":" + encoded + "}";
    }

    private static async Task InjectAndWaitForShellAdmitAsync(
        AttachLiveState live,
        ScriptedNdjsonPeer peer,
        string line)
    {
        var pumped = live.EndpointCommands.PumpedReplyCount;
        await peer.InjectAsync(line).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (live.EndpointCommands.PumpedReplyCount > pumped)
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "socket reader did not admit the shell chunk. pumped="
            + live.EndpointCommands.PumpedReplyCount);
    }

    private static void AssertPaneSurvived(ScriptedNdjsonPeer peer, int panePid, AttachLiveState live)
    {
        Assert.Equal(panePid, peer.PanePid);
        Assert.True(peer.PanePid > 0);
        Assert.False(peer.PaneHasExited);
        Assert.Equal(0, Volatile.Read(ref live.EndpointDisconnectCount));
    }

    /// <summary>
    /// Three further peer-owned PTY children. Each client write is a separate child.
    /// The handoff pid stays on its own master.
    /// </summary>
    private static async Task AssertSeparatePtyChildrenExitAsync()
    {
        var interrupt = Convert.ToBase64String(new byte[] { 0x03 });
        await AssertPeerOwnedPtyExitsAsync(
            client => client.CallAsync(
                ProtocolMethods.PaneSendKeys,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["pane_id"] = "pane",
                    ["encoding"] = "base64",
                    ["data"] = interrupt,
                }),
            peer => Assert.True(peer.ExitedByTerminalInterrupt));
        await AssertPeerOwnedPtyExitsAsync(
            client => client.CallAsync(
                ProtocolMethods.PaneClose,
                new System.Text.Json.Nodes.JsonObject { ["pane_id"] = "pane" }));
        await AssertPeerOwnedPtyExitsAsync(
            client => client.CallAsync(
                "pane.signal",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["pane_id"] = "pane",
                    ["signal"] = "HUP",
                }));
    }

    private static async Task AssertPeerOwnedPtyExitsAsync(
        Func<ControlPlaneClient, Task> write,
        Action<ScriptedNdjsonPeer>? assertExit = null)
    {
        await using var peer = ScriptedNdjsonPeer.Listen(ownsPane: true);
        var pid = peer.PanePid;
        Assert.True(pid > 0);
        Assert.False(peer.PaneHasExited);
        await using var client = new ControlPlaneClient(
            peer.Path,
            connectTimeout: TimeSpan.FromSeconds(2),
            callTimeout: TimeSpan.FromSeconds(5));
        var accept = peer.AcceptAsync();
        await client.ConnectAsync();
        await accept;
        await write(client);
        Assert.True(
            SpinWait.SpinUntil(() => peer.PaneHasExited, TimeSpan.FromSeconds(3)),
            "waitpid did not report pane child " + pid + " exited");
        Assert.Equal(pid, peer.PanePid);
        Assert.True(peer.PaneHasExited);
        assertExit?.Invoke(peer);
    }

    private sealed class RenderLoopRun : IAsyncDisposable
    {
        private readonly AttachLiveState _live;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _gate;
        private readonly bool _ownsGate;
        private readonly UnixRawTerminal _tty;
        private readonly Task _loop;

        private RenderLoopRun(AttachLiveState live, ControlPlaneClient reader, SemaphoreSlim gate, bool ownsGate)
        {
            _live = live;
            _gate = gate;
            _ownsGate = ownsGate;
            _tty = new UnixRawTerminal(new MemoryStream());
            var session = new AttachSession();
            _loop = session.ReadRenderAsync(
                reader,
                reader,
                _gate,
                new SnapshotAssembler(),
                _tty,
                live,
                _stop,
                _stop.Token);
        }

        internal static RenderLoopRun Start(AttachLiveState live, ControlPlaneClient reader) =>
            new(live, reader, new SemaphoreSlim(1, 1), ownsGate: true);

        internal static RenderLoopRun Start(
            AttachLiveState live,
            ControlPlaneClient reader,
            SemaphoreSlim gate) =>
            new(live, reader, gate, ownsGate: false);

        internal async Task WaitForReadAsync(int target)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (_loop.IsFaulted)
                    await _loop.ConfigureAwait(false);
                if (Volatile.Read(ref _live.RenderLoopReads) >= target)
                    return;
                await Task.Delay(20).ConfigureAwait(false);
            }

            if (_loop.IsFaulted)
                await _loop.ConfigureAwait(false);
            throw new TimeoutException(
                "ReadRenderAsync did not read the chunk. reads="
                + Volatile.Read(ref _live.RenderLoopReads));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _stop.Dispose();
            if (_ownsGate)
                _gate.Dispose();
            _tty.Dispose();
        }
    }

    private static async Task<JsonElement> NextEventAsync(ControlPlaneClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (client.TryTakeQueuedEvent(out var ev))
                return ev;
            await Task.Delay(20);
        }

        throw new TimeoutException("shell response was not queued by the read loop");
    }

    private static CubesConnectRetargetOutcome RetargetOutcome(ControlPlaneClient dest) =>
        new()
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
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
            DestClient = dest,
            DestSubscribeId = "sub-dest",
            DestInputLease = "dest-in",
            DestResizeLease = "dest-r",
            SourceBootId = SourceBoot,
            SourceConnectionGeneration = 1,
            TargetBootId = DestBoot,
            TargetConnectionGeneration = 7,
        };

    private sealed class ScriptedRetargeter(CubesConnectRetargetOutcome outcome) : ICubesConnectRetargeter
    {
        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome);
    }

    private sealed class ScriptedNdjsonPeer : IAsyncDisposable
    {
        private static readonly byte[] ReliableEvent = Encoding.UTF8.GetBytes(
            "{\"event\":\"session.lifecycle\",\"params\":{\"lane\":\"control\",\"marker\":\"kept-before-retire\"}}\n");

        private static readonly byte[] RenderEvent = Encoding.UTF8.GetBytes(
            "{\"event\":\"terminal.render\",\"params\":{\"lane\":\"render\",\"marker\":\"frame-before-retire\"}}\n");

        private readonly Socket _listener;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly object _gate = new();
        private readonly List<SeenRpc> _seen = [];
        private readonly List<string> _held = [];
        private readonly HashSet<string> _dropReply = new(StringComparer.Ordinal);
        private NetworkStream? _stream;
        private Task? _reader;
        private Thread? _masterReader;
        private bool _released;
        private int _master = -1;
        private bool _reaped;
        private int _waitStatus;

        private ScriptedNdjsonPeer(Socket listener, string path, bool ownsPane)
        {
            _listener = listener;
            Path = path;
            if (!ownsPane)
                return;

            // The mux owns the pane.
            SpawnSleepPane(out _master, out var pid);
            PanePid = pid;
            StartMasterReader();
        }

        internal string Path { get; }

        internal int PanePid { get; }

        internal bool PaneHasExited
        {
            get
            {
                if (PanePid <= 0)
                    return true;
                if (_reaped)
                    return true;
                var result = waitpid(PanePid, out var status, Wnohang);
                if (result == 0)
                    return false;
                if (result == PanePid)
                {
                    _waitStatus = status;
                    _reaped = true;
                    return true;
                }

                var err = Marshal.GetLastWin32Error();
                if (err == Eintr)
                    return false;
                _reaped = true;
                return true;
            }
        }

        internal int WaitStatus => _waitStatus;

        internal bool ExitedByTerminalInterrupt
        {
            get
            {
                if (!PaneHasExited)
                    return false;
                return (_waitStatus & 0x7f) == Sigint;
            }
        }

        internal static ScriptedNdjsonPeer Listen(bool ownsPane = false)
        {
            var path = "/tmp/hypa-ls-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new ScriptedNdjsonPeer(listener, path, ownsPane);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _reader = ReadAsync();
        }

        internal int Count(string method)
        {
            lock (_gate)
                return _seen.Count(seen => seen.Method == method);
        }

        internal List<string> Methods()
        {
            lock (_gate)
                return _seen.Select(seen => seen.Method).ToList();
        }

        internal void DropReplies(string method)
        {
            lock (_gate)
                _dropReply.Add(method);
        }

        internal void AllowReplies(string method)
        {
            lock (_gate)
                _dropReply.Remove(method);
        }

        internal string RequestId(string method)
        {
            lock (_gate)
            {
                for (var i = _seen.Count - 1; i >= 0; i--)
                {
                    if (_seen[i].Method == method)
                        return _seen[i].Id;
                }
            }

            return string.Empty;
        }

        internal Task InjectAsync(string line) => WriteLineAsync(line);

        /// <summary>Write only the held replies <paramref name="select"/> picks; keep holding the rest.</summary>
        internal async Task ReleaseAsync(Func<string, bool> select)
        {
            List<string> flush;
            lock (_gate)
            {
                flush = _held.Where(select).ToList();
                _held.RemoveAll(line => select(line));
            }

            foreach (var line in flush)
                await WriteLineAsync(line);
        }

        internal async Task ReleaseAsync()
        {
            List<string> flush;
            lock (_gate)
            {
                _released = true;
                flush = _held.ToList();
                _held.Clear();
            }

            foreach (var line in flush)
                await WriteLineAsync(line);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ReleaseAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }

            StopForegroundGroup();
            if (_masterReader is { IsAlive: true })
                _masterReader.Join(TimeSpan.FromSeconds(1));
            _stream?.Dispose();
            _listener.Dispose();
            if (_reader is not null)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                }
            }

            _write.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        private async Task ReadAsync()
        {
            var stream = _stream ?? throw new InvalidOperationException("peer is not connected");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        return;
                    if (line.Length == 0)
                        continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("method", out var methodEl))
                        continue;
                    var method = methodEl.GetString() ?? string.Empty;
                    var id = root.TryGetProperty("id", out var idEl)
                        ? idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? string.Empty : idEl.GetRawText()
                        : string.Empty;
                    string? requestId = null;
                    if (root.TryGetProperty("params", out var parms)
                        && parms.ValueKind == JsonValueKind.Object
                        && parms.TryGetProperty("request_id", out var requestEl)
                        && requestEl.ValueKind == JsonValueKind.String)
                    {
                        requestId = requestEl.GetString();
                    }

                    DeliverPaneWrite(root, method);
                    var reply = Reply(id, method, requestId);
                    var writeNow = false;
                    lock (_gate)
                    {
                        _seen.Add(new SeenRpc(method, id, requestId));
                        if (method == ProbeMethod)
                            writeNow = true;
                        else if (_dropReply.Contains(method))
                            writeNow = false;
                        else if (Hold(method) && !_released)
                            _held.Add(reply);
                        else
                            writeNow = true;
                    }

                    if (!writeNow)
                        continue;
                    if (method == ProbeMethod)
                    {
                        await WriteBytesAsync(ReliableEvent);
                        await WriteBytesAsync(RenderEvent);
                    }

                    await WriteLineAsync(reply);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void DeliverPaneWrite(JsonElement root, string method)
        {
            if (PanePid <= 0)
                return;

            if (method is ProtocolMethods.PaneSendKeys
                or ProtocolMethods.PaneSendInput
                or ProtocolMethods.PaneSendText
                or ProtocolMethods.PluginPaneSendText)
            {
                if (!TryDecodePaneBytes(root, out var bytes))
                    return;
                WriteMaster(bytes);
                return;
            }

            if (method is ProtocolMethods.PaneClose or ProtocolMethods.PluginPaneClose || IsSignalRpc(root, method))
            {
                SignalForegroundGroup(Sighup);
                CloseMaster();
            }
        }

        private static bool TryDecodePaneBytes(JsonElement root, out byte[] bytes)
        {
            bytes = [];
            if (!root.TryGetProperty("params", out var parms) || parms.ValueKind != JsonValueKind.Object)
                return false;

            if (parms.TryGetProperty("encoding", out var encoding)
                && encoding.ValueKind == JsonValueKind.String
                && string.Equals(encoding.GetString(), "base64", StringComparison.Ordinal)
                && parms.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String
                && data.GetString() is { Length: > 0 } encoded)
            {
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                    return bytes.Length > 0;
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            if (parms.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { Length: > 0 } plain)
            {
                bytes = Encoding.UTF8.GetBytes(plain);
                return true;
            }

            return false;
        }

        private static bool IsSignalRpc(JsonElement root, string method)
        {
            if (method.Contains("signal", StringComparison.OrdinalIgnoreCase))
                return true;
            return root.TryGetProperty("params", out var parms)
                && parms.ValueKind == JsonValueKind.Object
                && parms.TryGetProperty("signal", out _);
        }

        private void WriteMaster(byte[] bytes)
        {
            if (_master < 0 || bytes.Length == 0)
                return;
            var pending = bytes;
            while (pending.Length > 0 && _master >= 0)
            {
                var wrote = write(_master, pending, (nuint)pending.Length);
                if (wrote <= 0)
                    return;
                if (wrote >= pending.Length)
                    return;
                pending = pending[(int)wrote..];
            }
        }

        private void SignalForegroundGroup(int signal)
        {
            if (PanePid <= 0 || _reaped)
                return;
            _ = killpg(PanePid, signal);
        }

        private void CloseMaster()
        {
            if (_master < 0)
                return;
            close(_master);
            _master = -1;
        }

        // That read lets the terminal finish delivering VINTR to the foreground child.
        private void StartMasterReader()
        {
            var fd = _master;
            if (fd < 0)
                return;
            _masterReader = new Thread(() =>
            {
                var buf = new byte[256];
                while (true)
                {
                    var n = read(fd, buf, (nuint)buf.Length);
                    if (n < 0 && Marshal.GetLastWin32Error() == Eintr)
                        continue;
                    if (n <= 0)
                        return;
                }
            })
            {
                IsBackground = true,
                Name = "pty-master-read",
            };
            _masterReader.Start();
        }

        private void StopForegroundGroup()
        {
            SignalForegroundGroup(Sighup);
            CloseMaster();
        }

        // The mux owns that pane.
        /// The child runs <c>posix_openpt</c>, <c>grantpt</c>, <c>unlockpt</c>, <c>fork</c>,
        /// <c>setsid</c>, <c>TIOCSCTTY</c>, and <c>execve("/bin/sleep")</c>. Attach retire
        /// must not kill it.
        private static void SpawnSleepPane(out int master, out int pid)
        {
            var spawn = PaneSpawn.Create();
            if (spawn(out master, out pid) != 0 || master < 0 || pid <= 0)
                throw new InvalidOperationException("pty pane spawn failed errno " + Marshal.GetLastWin32Error());

            Thread.Sleep(30);
            if (waitpid(pid, out var status, Wnohang) != 0)
            {
                close(master);
                throw new InvalidOperationException("pane child exited during exec status " + status);
            }
        }

        private static bool Hold(string method) =>
            method is OtherRpcMethod
                or AttachEndpointProtocol.Focus
                or AttachEndpointProtocol.SurfaceInterest
                or ProtocolMethods.PaneZoom
                or ProtocolMethods.PaneSplit;

        private static string Reply(string id, string method, string? requestId)
        {
            var result = method switch
            {
                ProbeMethod => "{\"marker\":\"probe\"}",
                OtherRpcMethod => "{\"marker\":\"other-rpc\"}",
                AttachEndpointProtocol.Focus =>
                    "{\"request_id\":\"" + (requestId ?? string.Empty)
                    + "\",\"boot_id\":\"local-boot\",\"projection_revision\":1,\"geometry_revision\":1}",
                AttachEndpointProtocol.SurfaceInterest =>
                    "{\"request_id\":\"" + (requestId ?? string.Empty)
                    + "\",\"active\":false,\"lease_id\":\"src-in\",\"boot_id\":\"local-boot\",\"minimum_projection_revision\":0,\"geometry_revision\":1,\"connection_generation\":1}",
                _ => "{\"ok\":true}",
            };
            return "{\"id\":\"" + id + "\",\"result\":" + result + "}";
        }

        private async Task WriteLineAsync(string line) =>
            await WriteBytesAsync(Encoding.UTF8.GetBytes(line + "\n"));

        private async Task WriteBytesAsync(byte[] bytes)
        {
            var stream = _stream ?? throw new InvalidOperationException("peer is not connected");
            await _write.WaitAsync();
            try
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            finally
            {
                _write.Release();
            }
        }

        private readonly record struct SeenRpc(string Method, string Id, string? RequestId);

        private const int Wnohang = 1;
        private const int Sigint = 2;
        private const int Sighup = 1;
        private const int Eintr = 4;

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true)]
        private static extern int killpg(int processGroup, int signal);

        [DllImport("libc", SetLastError = true)]
        private static extern nint write(int fd, byte[] buffer, nuint count);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, byte[] buffer, nuint count);

        private static class PaneSpawn
        {
            private const string Source = """
                #define _XOPEN_SOURCE 600
                #include <errno.h>
                #include <fcntl.h>
                #include <stdlib.h>
                #include <termios.h>
                #include <unistd.h>
                #include <sys/ioctl.h>

                /* herdr src/pty/backend/unix.rs:12-35 keeps the master.
                   herdr src/pane.rs:2286-2304 owns the child pid. */
                int hypa_spawn_sleep_pane(int *master_out, int *pid_out) {
                    int master = posix_openpt(O_RDWR | O_NOCTTY);
                    if (master < 0)
                        return -1;
                    if (grantpt(master) != 0 || unlockpt(master) != 0) {
                        close(master);
                        return -1;
                    }
                    char *name = ptsname(master);
                    if (name == 0) {
                        close(master);
                        return -1;
                    }
                    int pid = fork();
                    if (pid < 0) {
                        close(master);
                        return -1;
                    }
                    if (pid == 0) {
                        if (setsid() < 0)
                            _exit(2);
                        int slave = open(name, O_RDWR);
                        if (slave < 0)
                            _exit(3);
                        if (ioctl(slave, TIOCSCTTY, 0) < 0)
                            _exit(4);
                        struct termios t;
                        if (tcgetattr(slave, &t) != 0)
                            _exit(5);
                        t.c_lflag |= ISIG;
                        t.c_cc[VINTR] = 3;
                        if (tcsetattr(slave, TCSANOW, &t) != 0)
                            _exit(6);
                        if (tcsetpgrp(slave, getpid()) != 0)
                            _exit(7);
                        if (dup2(slave, 0) < 0 || dup2(slave, 1) < 0 || dup2(slave, 2) < 0)
                            _exit(8);
                        if (slave > 2)
                            close(slave);
                        close(master);
                        char *argv[] = {"sleep", "60", 0};
                        char *envp[] = {0};
                        execve("/bin/sleep", argv, envp);
                        _exit(127);
                    }
                    *master_out = master;
                    *pid_out = pid;
                    return 0;
                }
                """;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate int Spawn(out int master, out int pid);

            private static readonly object Gate = new();
            private static Spawn? Cached;

            internal static Spawn Create()
            {
                lock (Gate)
                {
                    Cached ??= Compile();
                    return Cached;
                }
            }

            private static Spawn Compile()
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hypa-pane-spawn-" + Environment.ProcessId);
                Directory.CreateDirectory(dir);
                var src = System.IO.Path.Combine(dir, "pane.c");
                var lib = System.IO.Path.Combine(dir, OperatingSystem.IsMacOS() ? "pane.dylib" : "pane.so");
                var log = System.IO.Path.Combine(dir, "cc.log");
                File.WriteAllText(src, Source);
                var built = system($"cc -shared -fPIC -o '{lib}' '{src}' >'{log}' 2>&1");
                if (built != 0 || !File.Exists(lib))
                {
                    var detail = File.Exists(log) ? File.ReadAllText(log) : "cc produced no library";
                    throw new InvalidOperationException(detail);
                }

                var handle = NativeLibrary.Load(lib);
                var export = NativeLibrary.GetExport(handle, "hypa_spawn_sleep_pane");
                return Marshal.GetDelegateForFunctionPointer<Spawn>(export);
            }

            [DllImport("libc", SetLastError = true)]
            private static extern int system(string command);
        }
    }
}
