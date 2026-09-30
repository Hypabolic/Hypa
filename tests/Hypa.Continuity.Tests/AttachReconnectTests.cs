using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.Continuity.Tests;

public class AttachReconnectTests
{
    [Fact]
    public void Two_second_stall_reconnects_and_repaints()
    {
        var service = new AttachReconnectService();
        var first = Cap("nonce001");
        var attached = service.Attach(first);
        Assert.True(attached.Ok, attached.Detail);
        Assert.Equal(1, service.PaintCount);

        Assert.True(service.NoteObserved(MuxBinary(1, 3, "screen")).Ok);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);
        Assert.False(service.DumpedToShell);
        Assert.True(service.MuxRunning);
        Assert.Equal(AttachReconnectState.Stalled, service.State);

        var offer = service.Reconnect(Request(attached.Value, Cap("nonce002"), service.LastReceived));
        Assert.True(offer.Ok, offer.Detail);
        Assert.True(offer.Value!.InputLeaseRequired);
        Assert.True(service.ReclaimLease("lease_reclaim").Ok);
        Assert.True(service.CompleteReplay(painted: true).Ok);
        Assert.Equal(AttachReconnectState.Connected, service.State);
        Assert.Equal(2, service.PaintCount);
        Assert.False(service.DumpedToShell);
        Assert.True(service.MuxRunning);
        Assert.False(service.ServerStopRequested);
    }

    [Fact]
    public void Input_typed_once_does_not_run_twice_after_reconnect()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce011"));
        Assert.True(service.ApplyInput(ClientBinary(2, 1, "ls\n")).Ok);
        Assert.Equal(1, service.InputAppliedCount);
        Assert.True(service.NoteObserved(MuxBinary(1, 4, "ok")).Ok);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var offer = service.Reconnect(Request(attached.Value, Cap("nonce012"), service.LastReceived));
        Assert.True(offer.Ok, offer.Detail);
        foreach (var frame in offer.Value!.ReplayFrames)
            Assert.True(AttachReconnectRules.MayReplay(frame));
        Assert.DoesNotContain(
            offer.Value.ReplayFrames,
            frame => frame.Direction == StreamDirection.ClientToMux);
        Assert.True(service.ReclaimLease("lease_after").Ok);
        Assert.True(service.CompleteReplay(painted: true).Ok);
        Assert.Equal(1, service.InputAppliedCount);
    }

    [Fact]
    public void Lease_is_not_valid_across_stall_without_reclaim()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce021"));
        var oldLease = service.LeaseId;
        Assert.False(string.IsNullOrEmpty(oldLease));
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);
        Assert.Null(service.LeaseId);
        Assert.False(service.ApplyInput(ClientBinary(2, 2, "x")).Ok);

        var offer = service.Reconnect(Request(attached.Value, Cap("nonce022"), []));
        Assert.True(offer.Ok, offer.Detail);
        Assert.True(offer.Value!.InputLeaseRequired);
        Assert.True(offer.Value.SnapshotPaint);
        var complete = service.CompleteReplay(painted: true);
        Assert.False(complete.Ok);
        Assert.Equal(ConnectivityReasons.Unauthorized, complete.Reason);
        Assert.True(service.ReclaimLease("lease_new").Ok);
        Assert.NotEqual(oldLease, service.LeaseId);
        Assert.True(service.CompleteReplay(painted: true).Ok);
        Assert.True(service.ApplyInput(ClientBinary(2, 3, "y")).Ok);
        Assert.Equal(1, service.InputAppliedCount);
    }

    [Fact]
    public void Client_shell_observe_reconnect_completes_without_input_lease()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce041"));
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var offer = service.Reconnect(Request(attached.Value, Cap("nonce042"), []));
        Assert.True(offer.Ok, offer.Detail);
        Assert.True(service.AdmitWithoutInputLease().Ok);
        Assert.True(service.CompleteReplay(painted: true).Ok);
        Assert.Equal(AttachReconnectState.Connected, service.State);
        Assert.Null(service.LeaseId);
    }

    [Fact]
    public void Admit_without_input_lease_fails_when_connected()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce043"));
        Assert.True(attached.Ok, attached.Detail);
        Assert.Equal(AttachReconnectState.Connected, service.State);
        var denied = service.AdmitWithoutInputLease();
        Assert.False(denied.Ok);
        Assert.Equal(ConnectivityReasons.StreamReset, denied.Reason);
        Assert.Equal("admit without a lease requires reconnect", denied.Detail);
    }

    [Fact]
    public void Mux_process_still_runs_after_client_drop()
    {
        var service = new AttachReconnectService();
        Assert.True(service.Attach(Cap("nonce031")).Ok);
        Assert.True(service.Disconnect().Ok);
        Assert.True(service.MuxRunning);
        Assert.False(service.ServerStopRequested);
        Assert.True(service.DumpedToShell);
        Assert.Equal(AttachReconnectState.Detached, service.State);
    }

    [SkippableFact]
    public async Task Live_mux_process_answers_ping_after_client_drop()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix sockets only.");
        var launch = TryFindHypaLaunch();
        if (launch is null
            && string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("CI requires a built hypa binary for live mux process reconnect");
        }

        Skip.If(launch is null, "hypa binary required for live mux process.");
        LiveMuxLaunch.RequireGhosttyLibraryPath();
        var (dir, sock) = NewPrivateSocketPath("c53muxproc");
        var logPath = Path.Combine(dir, "mux.log");
        var home = Path.Combine(dir, "home");
        Directory.CreateDirectory(home);
        Process? mux = null;
        try
        {
            var started = LiveMuxLaunch.StartServe(launch.Value, "c53-mux", dir, sock, home, logPath);
            mux = started.Process;
            await LiveMuxLaunch.WaitForPingAsync(sock, TimeSpan.FromSeconds(30), mux, started.Stderr);
            var pid = mux.Id;
            Assert.False(mux.HasExited);

            await using (var client = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2)))
            {
                await client.ConnectAsync();
                var ping = await client.CallAsync(ProtocolMethods.Ping);
                Assert.True(ping.GetProperty("ok").GetBoolean());
            }

            Assert.False(mux.HasExited);
            Assert.Equal(pid, mux.Id);

            await using var next = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
            await next.ConnectAsync();
            var alive = await next.CallAsync(ProtocolMethods.Ping);
            Assert.True(alive.GetProperty("ok").GetBoolean());
            Assert.False(mux.HasExited);
            Assert.Equal(pid, mux.Id);
        }
        finally
        {
            TryKillMux(mux);
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public void User_detach_does_not_reconnect()
    {
        Assert.Equal(ClientLossKind.Detach, AttachLossPolicy.Classify(true, null));
        Assert.False(AttachLossPolicy.ShouldReconnect(ClientLossKind.Detach));
        Assert.True(AttachReconnectRules.DumpsToShell(ClientLossKind.Detach));

        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce041"));
        Assert.True(service.Detach().Ok);
        var offer = service.Reconnect(Request(attached.Value, Cap("nonce042"), []));
        Assert.False(offer.Ok);
        Assert.Equal(ConnectivityReasons.StreamReset, offer.Reason);
        Assert.True(service.MuxRunning);
        Assert.False(service.ServerStopRequested);
    }

    [Fact]
    public void Reconnect_requires_fresh_capability_and_new_attempt_id()
    {
        var service = new AttachReconnectService();
        var first = Cap("nonce051");
        var attached = service.Attach(first);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var reused = service.Reconnect(new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = attached.Value,
            Capability = first,
            LastReceived = [],
        });
        Assert.False(reused.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, reused.Reason);

        var sameCap = service.Reconnect(new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = AttachAttemptId.New(),
            Capability = first,
            LastReceived = [],
        });
        Assert.False(sameCap.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, sameCap.Reason);

        var ok = service.Reconnect(Request(attached.Value, Cap("nonce052"), []));
        Assert.True(ok.Ok, ok.Detail);
    }

    [Fact]
    public void Cursor_unavailable_forces_snapshot_paint()
    {
        var empty = AttachReconnectRules.RequiresSnapshot([]);
        Assert.True(empty);
        var missing = AttachReconnectRules.RequiresSnapshot(
        [
            new ChannelCursor
            {
                ChannelId = 1,
                LastReceivedSequence = 4,
                Unavailable = true,
            },
        ]);
        Assert.True(missing);

        var service = new AttachReconnectService();
        var first = Cap("nonce061");
        var attached = service.Attach(first);
        Assert.True(service.NoteObserved(MuxBinary(1, 9, "grid")).Ok);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);
        var offer = service.Reconnect(Request(attached.Value, Cap("nonce062"),
            AttachReconnectRules.BindCursors(
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 9,
                    Unavailable = true,
                },
            ],
            attached.Value,
            first)));
        Assert.True(offer.Ok, offer.Detail);
        Assert.True(offer.Value!.SnapshotPaint);
        Assert.Empty(offer.Value.ReplayFrames);
    }

    [Fact]
    public void Journal_events_may_replay_after_last_sequence()
    {
        var observed = new[]
        {
            MuxControl(0, 1, """{"ok":true}"""),
            MuxControl(0, 2, """{"seq":2}"""),
            MuxControl(0, 3, """{"seq":3}"""),
            ClientBinary(2, 1, "nope"),
        };
        var replay = AttachReconnectRules.SelectReplay(
            observed,
            [new ChannelCursor { ChannelId = 0, LastReceivedSequence = 1 }]);
        Assert.Equal(2, replay.Count);
        Assert.Equal(2UL, replay[0].Sequence);
        Assert.Equal(3UL, replay[1].Sequence);
        Assert.All(replay, frame => Assert.True(AttachReconnectRules.MayReplay(frame)));
    }

    [Fact]
    public void Terminal_input_frames_are_not_replayed()
    {
        Assert.False(AttachReconnectRules.MayReplay(ClientBinary(2, 8, "rm -rf\n")));
        Assert.False(AttachReconnectRules.MayReplay(StreamFrame.Resize(
            StreamDirection.ClientToMux, 2, 1, 80, 24)));
        Assert.True(AttachReconnectRules.MayReplay(MuxBinary(1, 8, "out")));
        Assert.False(AttachReconnectRules.MayReplay(
            StreamFrame.Heartbeat(StreamDirection.MuxToClient, 1)));
    }

    [Fact]
    public void Sequence_tracker_records_last_received_per_channel()
    {
        var tracker = new ChannelSequenceTracker();
        tracker.Note(MuxBinary(1, 2, "a"));
        tracker.Note(MuxBinary(1, 5, "b"));
        tracker.Note(MuxBinary(3, 1, "c"));
        tracker.Note(StreamFrame.Heartbeat(StreamDirection.MuxToClient, 99));
        Assert.True(tracker.TryGet(1, out var seq));
        Assert.Equal(5UL, seq);
        Assert.True(tracker.TryGet(3, out var other));
        Assert.Equal(1UL, other);
        Assert.False(tracker.TryGet(0, out _));
        var snap = tracker.Snapshot();
        Assert.Equal(2, snap.Count);
    }

    [Fact]
    public void Attach_attempt_id_is_unique_per_attempt()
    {
        Assert.True(AttachAttemptId.TryParse("att_abcd1234", out var parsed));
        Assert.Equal("att_abcd1234", parsed.Value);
        Assert.False(AttachAttemptId.TryParse("/tmp/att.sock", out _));
        Assert.False(AttachAttemptId.TryParse("dev_abcd1234", out _));
        var a = AttachAttemptId.New();
        var b = AttachAttemptId.New();
        Assert.NotEqual(a.Value, b.Value);
        Assert.StartsWith("att_", a.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Ping_timeout_is_a_stall_and_does_not_dump_to_shell()
    {
        Assert.Equal(
            ClientLossKind.Stall,
            AttachLossPolicy.Classify(false, "control ping timed out"));
        Assert.True(AttachLossPolicy.ShouldReconnect(ClientLossKind.Stall));
        Assert.False(AttachReconnectRules.DumpsToShell(ClientLossKind.Stall));
        Assert.Equal(
            ClientLossKind.Stall,
            AttachLossPolicy.Classify(false, "connection closed"));
        Assert.Equal(
            ClientLossKind.Stall,
            AttachLossPolicy.Classify(false, "control ping failed"));
        Assert.Equal(
            ClientLossKind.Stall,
            AttachLossPolicy.Classify(false, "input sender fault"));
        Assert.Equal(
            ClientLossKind.Detach,
            AttachLossPolicy.Classify(false, "tty hangup"));
    }

    [Fact]
    public void Input_sender_fault_during_stall_does_not_dump_to_shell()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(
                new RecordingReconnectPort(), "w1", "t1", "p1"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            PaneId = "p1",
            InputLease = "lease-in",
        };
        using var attempt = new CancellationTokenSource();
        using var session = new CancellationTokenSource();
        var stall = AttachSession.ApplyInputSenderFault(
            live,
            new AttachInputFault(
                AttachInputFaultKind.SenderFault,
                0,
                "connection closed"),
            attempt,
            session);
        Assert.True(stall);
        Assert.True(live.InputStallRequested);
        Assert.False(live.DetachRequested);
        Assert.False(live.InputDetachRequested);
        Assert.Equal("input sender fault", live.StatusError);
        Assert.True(attempt.IsCancellationRequested);
        Assert.False(session.IsCancellationRequested);
        Assert.Equal(
            ClientLossKind.Stall,
            AttachLossPolicy.Classify(live.DetachRequested, live.StatusError));
        Assert.True(AttachLossPolicy.ShouldReconnect(ClientLossKind.Stall));
        Assert.False(AttachReconnectRules.DumpsToShell(ClientLossKind.Stall));

        var undeliverable = AttachSession.ApplyInputSenderFault(
            live,
            new AttachInputFault(
                AttachInputFaultKind.Undeliverable,
                3,
                "input target pane or lease is empty"),
            attempt,
            session);
        Assert.False(undeliverable);
        Assert.True(live.DetachRequested);
        Assert.True(session.IsCancellationRequested);
    }

    [Fact]
    public void Heartbeat_fallback_reason_classifies_as_stall()
    {
        var input = Task.Delay(TimeSpan.FromSeconds(30));
        var render = Task.Delay(TimeSpan.FromSeconds(30));
        var renew = Task.Delay(TimeSpan.FromSeconds(30));
        var beat = Task.CompletedTask;
        var tab = Task.Delay(TimeSpan.FromSeconds(30));
        var reason = AttachSession.DescribeAttachExit(
            beat, input, render, renew, beat, tab, userDetach: false);
        Assert.Equal("control ping failed", reason);
        Assert.Equal(ClientLossKind.Stall, AttachLossPolicy.Classify(false, reason));
        Assert.True(AttachLossPolicy.ShouldReconnect(ClientLossKind.Stall));
        Assert.False(AttachReconnectRules.DumpsToShell(ClientLossKind.Stall));
    }

    [Fact]
    public void Reconnect_mints_attempt_ids_and_retains_used_ids()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce081"));
        Assert.True(attached.Ok, attached.Detail);
        var minted = service.MintAttemptId();
        Assert.StartsWith("att_", minted.Value, StringComparison.Ordinal);
        Assert.NotEqual(attached.Value.Value, minted.Value);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var first = service.Reconnect(new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = minted,
            Capability = Cap("nonce082"),
            LastReceived = [],
        });
        Assert.True(first.Ok, first.Detail);
        Assert.Equal(minted.Value, first.Value!.AttemptId.Value);

        Assert.True(service.ReclaimLease("lease_mint").Ok);
        Assert.True(service.CompleteReplay(painted: true).Ok);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);
        var reused = service.Reconnect(new AttachReconnectRequest
        {
            PreviousAttemptId = minted,
            AttemptId = minted,
            Capability = Cap("nonce083"),
            LastReceived = [],
        });
        Assert.False(reused.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, reused.Reason);
    }

    [Fact]
    public void Reconnect_rejects_capability_bound_to_other_placement_device_or_role()
    {
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("nonce091"));
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var placement = service.Reconnect(Request(
            attached.Value,
            Cap("nonce092", placement: "plc_other1"),
            []));
        Assert.False(placement.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, placement.Reason);

        var device = service.Reconnect(Request(
            attached.Value,
            Cap("nonce093", device: "dev_other1"),
            []));
        Assert.False(device.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, device.Reason);

        var role = service.Reconnect(Request(
            attached.Value,
            Cap("nonce094", role: JoinRole.Mux),
            []));
        Assert.False(role.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, role.Reason);

        var ok = service.Reconnect(Request(attached.Value, Cap("nonce095"), []));
        Assert.True(ok.Ok, ok.Detail);
    }

    [Fact]
    public void Reconnect_rejects_cursor_bound_to_other_destination()
    {
        var service = new AttachReconnectService();
        var first = Cap("noncea01");
        var attached = service.Attach(first);
        Assert.True(service.NoteObserved(MuxBinary(1, 4, "grid")).Ok);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var missing = service.Reconnect(Request(
            attached.Value,
            Cap("noncea02"),
            [new ChannelCursor { ChannelId = 1, LastReceivedSequence = 4 }]));
        Assert.False(missing.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, missing.Reason);
        Assert.Equal(AttachReconnectState.Stalled, service.State);

        var placement = service.Reconnect(Request(
            attached.Value,
            Cap("noncea03"),
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 4,
                    AttemptId = attached.Value.Value,
                    PlacementId = "plc_other1",
                    DeviceId = first.DeviceId.Value,
                    Role = first.Role,
                },
            ]));
        Assert.False(placement.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, placement.Reason);

        var device = service.Reconnect(Request(
            attached.Value,
            Cap("noncea04"),
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 4,
                    AttemptId = attached.Value.Value,
                    PlacementId = first.PlacementId.Value,
                    DeviceId = "dev_other1",
                    Role = first.Role,
                },
            ]));
        Assert.False(device.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, device.Reason);

        var attempt = service.Reconnect(Request(
            attached.Value,
            Cap("noncea05"),
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 4,
                    AttemptId = AttachAttemptId.New().Value,
                    PlacementId = first.PlacementId.Value,
                    DeviceId = first.DeviceId.Value,
                    Role = first.Role,
                },
            ]));
        Assert.False(attempt.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, attempt.Reason);

        var ok = service.Reconnect(Request(attached.Value, Cap("noncea06"), service.LastReceived));
        Assert.True(ok.Ok, ok.Detail);
        Assert.DoesNotContain(
            ok.Value!.ReplayFrames,
            frame => frame.Direction == StreamDirection.ClientToMux);
    }

    [Fact]
    public void New_attach_clears_observed_frames_from_prior_destination()
    {
        var service = new AttachReconnectService();
        var first = Cap("nonceb01", placement: "plc_first1");
        var firstAttach = service.Attach(first);
        Assert.True(firstAttach.Ok, firstAttach.Detail);
        Assert.True(service.NoteObserved(MuxBinary(1, 5, "from-first")).Ok);
        Assert.Contains(
            service.LastReceived,
            cursor => cursor.PlacementId == "plc_first1" && cursor.AttemptId == firstAttach.Value.Value);

        var second = Cap("nonceb02", placement: "plc_secnd1", device: "dev_secnd1");
        var secondAttach = service.Attach(second);
        Assert.True(secondAttach.Ok, secondAttach.Detail);
        Assert.Empty(service.LastReceived);
        Assert.True(service.NoteObserved(MuxBinary(1, 2, "from-second")).Ok);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var mixed = service.Reconnect(Request(
            secondAttach.Value,
            Cap("nonceb03", placement: "plc_secnd1", device: "dev_secnd1"),
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 1,
                    AttemptId = firstAttach.Value.Value,
                    PlacementId = first.PlacementId.Value,
                    DeviceId = first.DeviceId.Value,
                    Role = first.Role,
                },
            ]));
        Assert.False(mixed.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, mixed.Reason);

        var offer = service.Reconnect(Request(
            secondAttach.Value,
            Cap("nonceb04", placement: "plc_secnd1", device: "dev_secnd1"),
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 1,
                    AttemptId = secondAttach.Value.Value,
                    PlacementId = second.PlacementId.Value,
                    DeviceId = second.DeviceId.Value,
                    Role = second.Role,
                },
            ]));
        Assert.True(offer.Ok, offer.Detail);
        Assert.DoesNotContain(
            offer.Value!.ReplayFrames,
            frame => Encoding.UTF8.GetString(frame.Payload.Span) == "from-first");
        Assert.Contains(
            offer.Value.ReplayFrames,
            frame => Encoding.UTF8.GetString(frame.Payload.Span) == "from-second");
        foreach (var cursor in service.LastReceived)
        {
            Assert.Equal(offer.Value.AttemptId.Value, cursor.AttemptId);
            Assert.Equal("plc_secnd1", cursor.PlacementId);
            Assert.Equal("dev_secnd1", cursor.DeviceId);
            Assert.Equal(JoinRole.Client, cursor.Role);
        }
    }

    [Fact]
    public void Sequence_tracker_snapshot_stamps_attempt_placement_device_and_role()
    {
        var tracker = new ChannelSequenceTracker();
        tracker.Note(MuxBinary(1, 2, "a"));
        var unbound = tracker.Snapshot();
        Assert.All(unbound, cursor =>
        {
            Assert.Equal("", cursor.AttemptId);
            Assert.Equal("", cursor.PlacementId);
            Assert.Equal("", cursor.DeviceId);
            Assert.Null(cursor.Role);
        });

        Assert.True(DeviceId.TryParse("dev_cli001", out var device));
        var attempt = AttachAttemptId.New();
        tracker.Bind(attempt, "plc_reconn", device, JoinRole.Client);
        var bound = tracker.Snapshot();
        Assert.Single(bound);
        Assert.Equal(attempt.Value, bound[0].AttemptId);
        Assert.Equal("plc_reconn", bound[0].PlacementId);
        Assert.Equal("dev_cli001", bound[0].DeviceId);
        Assert.Equal(JoinRole.Client, bound[0].Role);

        tracker.Clear();
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void Journal_subscribe_uses_last_received_when_cursor_is_available()
    {
        var snapshot = AttachSession.BuildEventsSubscribeParams(
            ["control", "lifecycle", "render"],
            [],
            snapshotPaint: true);
        // A fresh subscribe starts at the live edge and sends no cursor.
        Assert.False(snapshot.ContainsKey("from_seq"));
        Assert.Equal(0, snapshot["replay_budget"]!.GetValue<int>());

        var replay = AttachSession.BuildEventsSubscribeParams(
            ["control"],
            [new ChannelCursor { ChannelId = 0, LastReceivedSequence = 7 }],
            snapshotPaint: false);
        Assert.Equal(7, replay["from_seq"]!.GetValue<long>());
        Assert.Equal(1000, replay["replay_budget"]!.GetValue<int>());
        var json = replay.ToJsonString();
        Assert.DoesNotContain("socket_path", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Framed_reconnect_reports_last_received_and_does_not_replay_input()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_recfrm", "recfrm01");
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var muxTask = OutboundFramedSession.ConnectAsync(url, muxBoot);
        var clientTask = OutboundFramedSession.ConnectAsync(url, clientBoot);
        var muxOutcome = await muxTask;
        var clientOutcome = await clientTask;
        Assert.True(muxOutcome.Ok, muxOutcome.Detail);
        Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        await using var mux = muxOutcome.Value!;
        await using var client = clientOutcome.Value!;

        Assert.True((await mux.SendAsync(
            StreamFrame.Control(
                StreamDirection.MuxToClient,
                0,
                Encoding.UTF8.GetBytes("""{"seq":1}""")))).Ok);
        Assert.True((await mux.SendAsync(
            StreamFrame.Binary(
                StreamDirection.MuxToClient,
                1,
                0,
                Encoding.UTF8.GetBytes("screen")))).Ok);
        Assert.True((await client.SendAsync(
            StreamFrame.Binary(
                StreamDirection.ClientToMux,
                2,
                0,
                Encoding.UTF8.GetBytes("ls\n")))).Ok);

        var seen = 0;
        while (seen < 2)
        {
            var received = await client.ReceiveAsync();
            Assert.True(received.Ok, received.Detail);
            seen++;
        }

        var last = client.LastReceived;
        Assert.Contains(last, cursor => cursor.ChannelId == 0);
        Assert.Contains(last, cursor => cursor.ChannelId == 1);

        var service = new AttachReconnectService();
        var attached = service.Attach(clientBoot.Capability);
        Assert.True(attached.Ok, attached.Detail);
        foreach (var cursor in last)
        {
            Assert.True(service.NoteObserved(StreamFrame.Control(
                StreamDirection.MuxToClient,
                cursor.LastReceivedSequence,
                Encoding.UTF8.GetBytes("{}"),
                cursor.ChannelId)).Ok);
        }

        Assert.True(service.NoteObserved(MuxControl(0, cursorAfter(last, 0), """{"seq":next}""")).Ok);
        Assert.True(service.ApplyInput(ClientBinary(2, 1, "ls\n")).Ok);
        Assert.Equal(1, service.InputAppliedCount);
        Assert.True(service.Stall(TimeSpan.FromSeconds(2)).Ok);

        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = Cap("recfrm02", placement: "plc_recfrm", device: clientBoot.Capability.DeviceId.Value),
            LastReceived = AttachReconnectRules.BindCursors(
                last,
                attached.Value,
                clientBoot.Capability),
        };
        var json = AttachReconnectCodec.Write(request);
        Assert.Contains("\"previous_attempt_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"attach.reconnect\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("socket_path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HOME", json, StringComparison.Ordinal);

        var parsed = AttachReconnectCodec.Read(json);
        Assert.True(parsed.Ok, parsed.Detail);
        Assert.Equal(request.AttemptId.Value, parsed.Value!.AttemptId.Value);
        Assert.Equal(request.PreviousAttemptId.Value, parsed.Value.PreviousAttemptId.Value);

        var pair = ChannelFramedSession.Pair("plc_recfrm");
        await using var framedClient = pair.Client;
        await using var framedMux = pair.Mux;
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(framedMux, service, handshakeCts.Token).AsTask(),
            handshakeCts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(framedClient, request, handshakeCts.Token);
        var served = await serve;
        Assert.True(served.Ok, served.Detail);
        Assert.True(offered.Ok, offered.Detail);
        Assert.False(offered.Value!.SnapshotPaint);
        Assert.True(offered.Value.InputLeaseRequired);
        Assert.DoesNotContain(
            offered.Value.ReplayFrames,
            frame => frame.Direction == StreamDirection.ClientToMux);
        Assert.Equal(1, service.InputAppliedCount);
        Assert.Equal(AttachReconnectState.Replaying, service.State);
        return;

        static ulong cursorAfter(IReadOnlyList<ChannelCursor> cursors, uint channel)
        {
            foreach (var cursor in cursors)
            {
                if (cursor.ChannelId == channel)
                    return cursor.LastReceivedSequence + 1;
            }

            return 1;
        }
    }

    [SkippableFact]
    public async Task Unix_socket_peer_close_is_stall_and_keeps_mux()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath("c53stall");
        try
        {
            var leases = new InMemoryLeaseRegistry();
            var attachments = new InMemoryAttachmentRegistry();
            var state = new AppState(SessionId.New("c53-stall"));
            var cp = new ControlPlaneService(
                state,
                new GraphPaneFactory(),
                new NullIntelligence(),
                new NullDetector(),
                leases: leases,
                attachments: attachments);
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            await using (var client = new ControlPlaneClient(sock))
            {
                await client.ConnectAsync();
                var ping = await client.CallAsync(ProtocolMethods.Ping);
                Assert.True(ping.GetProperty("ok").GetBoolean());
            }

            await WaitUntilAsync(() => cp.LastClientLoss == AttachClientLoss.Stall, TimeSpan.FromSeconds(2));
            Assert.Equal(AttachClientLoss.Stall, cp.LastClientLoss);
            Assert.False(cp.ServerStopStarted);

            await using var next = new ControlPlaneClient(sock);
            await next.ConnectAsync();
            var alive = await next.CallAsync(ProtocolMethods.Ping);
            Assert.True(alive.GetProperty("ok").GetBoolean());
            Assert.False(cp.ServerStopStarted);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Live_lease_is_not_valid_across_socket_stall_without_reclaim()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath("c53lease");
        try
        {
            await using var mux = await LiveMux.StartAsync(dir);
            var created = await mux.CreateWorkspaceAsync();
            var paneId = created.PaneId;
            string oldLease;
            await using (var client = new ControlPlaneClient(mux.SocketPath))
            {
                await client.ConnectAsync();
                var claim = await client.CallAsync(
                    ProtocolMethods.RuntimeLeaseClaim,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["scope"] = "input",
                        ["ttl_ms"] = 30_000,
                    });
                oldLease = claim.GetProperty("lease_id").GetString()!;
                await client.CallAsync(
                    ProtocolMethods.PaneSendKeys,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["lease_id"] = oldLease,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("once\n"u8.ToArray()),
                    });
            }

            await WaitUntilAsync(() => mux.Factory.Writes.Count >= 1, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => mux.ControlPlane.LastClientLoss == AttachClientLoss.Stall,
                TimeSpan.FromSeconds(2));
            Assert.Single(mux.Factory.Writes);

            await using var next = new ControlPlaneClient(mux.SocketPath);
            await next.ConnectAsync();
            var denied = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                next.CallAsync(
                    ProtocolMethods.PaneSendKeys,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["lease_id"] = oldLease,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("replay\n"u8.ToArray()),
                    }));
            Assert.NotEqual(0, denied.Code);

            var reclaim = await next.CallAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = true,
                });
            var newLease = reclaim.GetProperty("lease_id").GetString()!;
            Assert.NotEqual(oldLease, newLease);
            await next.CallAsync(
                ProtocolMethods.PaneSendKeys,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["lease_id"] = newLease,
                    ["encoding"] = "base64",
                    ["data"] = Convert.ToBase64String("after\n"u8.ToArray()),
                });
            await WaitUntilAsync(() => mux.Factory.Writes.Count >= 2, TimeSpan.FromSeconds(2));
            Assert.Equal(2, mux.Factory.Writes.Count);
            Assert.Equal("once\n", mux.Factory.Writes[0]);
            Assert.Equal("after\n", mux.Factory.Writes[1]);
            var ping = await next.CallAsync(ProtocolMethods.Ping);
            Assert.True(ping.GetProperty("ok").GetBoolean());
            Assert.False(mux.ControlPlane.ServerStopStarted);
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Attach_session_reconnect_sends_fresh_capability_and_repaints()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath("c53sess");
        try
        {
            await using var mux = await LiveMux.StartAsync(dir);
            var created = await mux.CreateWorkspaceAsync();
            var reconnect = new AttachReconnectService();
            var session = new AttachSession(reconnect: reconnect);
            var attached = reconnect.Attach(Cap("nonce101"));
            Assert.True(attached.Ok, attached.Detail);
            Assert.True(reconnect.NoteObserved(MuxControl(0, 4, """{"seq":4}""")).Ok);
            Assert.True(reconnect.NoteObserved(MuxBinary(1, 2, "grid")).Ok);

            await using var placeholder = new ControlPlaneClient(mux.SocketPath);
            await placeholder.ConnectAsync();
            var commandPort = new ControlPlaneAttachCommandPort(placeholder);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            using var capture = new MemoryStream();
            using var tty = new UnixRawTerminal(capture, 8, 4);
            using var gate = new SemaphoreSlim(1, 1);
            var renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask);
            var live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = new AttachCommandDispatcher(
                    commandPort, created.WorkspaceId, created.TabId, created.PaneId),
                Renew = renew,
                WorkspaceId = created.WorkspaceId,
                TabId = created.TabId,
                PaneId = created.PaneId,
                ChromeEnabled = false,
                ReconnectPort = reconnect,
            };

            await Task.Delay(AttachReconnectLimits.StallWindow);
            var recovered = await session.ReconnectAfterStallAsync(
                new MuxReadyInfo("c53sess", mux.SocketPath, """{"ok":true}"""),
                commandPort,
                live,
                new SnapshotAssembler(),
                tty,
                gate,
                renew,
                CancellationToken.None);
            Assert.True(recovered.Ok, recovered.Detail);
            Assert.NotNull(session.LastReconnectRequest);
            Assert.NotEqual(
                attached.Value.Value,
                session.LastReconnectRequest!.AttemptId.Value);
            Assert.StartsWith(
                "att_",
                session.LastReconnectRequest.AttemptId.Value,
                StringComparison.Ordinal);
            Assert.NotEqual(
                "nonce101",
                session.LastReconnectRequest.Capability.Nonce.Value);
            Assert.Equal("plc_reconn", session.LastReconnectRequest.Capability.PlacementId.Value);
            Assert.Contains(
                session.LastReconnectRequest.LastReceived,
                cursor => cursor.ChannelId == 0 && cursor.LastReceivedSequence == 4);
            Assert.All(
                session.LastReconnectRequest.LastReceived,
                cursor =>
                {
                    Assert.Equal(attached.Value.Value, cursor.AttemptId);
                    Assert.Equal("plc_reconn", cursor.PlacementId);
                    Assert.Equal("dev_cli001", cursor.DeviceId);
                    Assert.Equal(JoinRole.Client, cursor.Role);
                });
            var requestJson = AttachReconnectCodec.Write(session.LastReconnectRequest);
            Assert.DoesNotContain("socket_path", requestJson, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(session.LastFramedReconnectJson));
            Assert.Contains(
                "\"type\":\"attach.reconnect\"",
                session.LastFramedReconnectJson,
                StringComparison.Ordinal);
            Assert.Contains(
                session.LastReconnectRequest.AttemptId.Value,
                session.LastFramedReconnectJson,
                StringComparison.Ordinal);
            var wire = AttachReconnectCodec.Read(session.LastFramedReconnectJson);
            Assert.True(wire.Ok, wire.Detail);
            Assert.Equal(
                session.LastReconnectRequest.AttemptId.Value,
                wire.Value!.AttemptId.Value);
            Assert.NotNull(session.FramedSession);
            Assert.True(session.LastReconnectOffer!.InputLeaseRequired);
            Assert.True(string.IsNullOrEmpty(live.InputLease));
            Assert.False(session.CalledServerStop);
            Assert.True(capture.Length > 0);
            var painted = Encoding.UTF8.GetString(capture.ToArray());
            Assert.Contains(HostBlitEncoder.SyncBegin, painted, StringComparison.Ordinal);
            Assert.Contains(HostBlitEncoder.SyncEnd, painted, StringComparison.Ordinal);
            Assert.Equal(
                CountOccurrences(painted, HostBlitEncoder.SyncBegin),
                CountOccurrences(painted, HostBlitEncoder.SyncEnd));
            Assert.Equal(8, live.Host.Cols);
            Assert.Equal(4, live.Host.Rows);
            Assert.Equal(AttachReconnectState.Connected, reconnect.State);
            Assert.False(reconnect.DumpedToShell);
            Assert.True(reconnect.MuxRunning);

            var ping = await recovered.Control!.CallAsync(ProtocolMethods.Ping);
            Assert.True(ping.GetProperty("ok").GetBoolean());
            Assert.False(mux.ControlPlane.ServerStopStarted);
            if (recovered.Control is not null)
                await recovered.Control.DisposeAsync();
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Attach_session_does_not_replay_input_typed_before_stall()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath("c53input");
        try
        {
            await using var mux = await LiveMux.StartAsync(dir);
            var created = await mux.CreateWorkspaceAsync();
            await using (var client = new ControlPlaneClient(mux.SocketPath))
            {
                await client.ConnectAsync();
                var claim = await client.CallAsync(
                    ProtocolMethods.RuntimeLeaseClaim,
                    new JsonObject
                    {
                        ["pane_id"] = created.PaneId,
                        ["scope"] = "input",
                        ["ttl_ms"] = 30_000,
                    });
                var lease = claim.GetProperty("lease_id").GetString()!;
                await client.CallAsync(
                    ProtocolMethods.PaneSendKeys,
                    new JsonObject
                    {
                        ["pane_id"] = created.PaneId,
                        ["lease_id"] = lease,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("once\n"u8.ToArray()),
                    });
            }

            await WaitUntilAsync(() => mux.Factory.Writes.Count >= 1, TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "once\n" }, mux.Factory.Writes.ToArray());

            var reconnect = new AttachReconnectService();
            Assert.True(reconnect.Attach(Cap("nonce111")).Ok);
            Assert.True(reconnect.ApplyInput(ClientBinary(2, 1, "once\n")).Ok);
            Assert.Equal(1, reconnect.InputAppliedCount);
            var session = new AttachSession(reconnect: reconnect);
            await using var placeholder = new ControlPlaneClient(mux.SocketPath);
            await placeholder.ConnectAsync();
            var commandPort = new ControlPlaneAttachCommandPort(placeholder);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            using var capture = new MemoryStream();
            using var tty = new UnixRawTerminal(capture, 8, 4);
            using var gate = new SemaphoreSlim(1, 1);
            var renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask);
            var live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = new AttachCommandDispatcher(
                    commandPort, created.WorkspaceId, created.TabId, created.PaneId),
                Renew = renew,
                WorkspaceId = created.WorkspaceId,
                TabId = created.TabId,
                PaneId = created.PaneId,
                ChromeEnabled = false,
                ReconnectPort = reconnect,
            };

            var recovered = await session.ReconnectAfterStallAsync(
                new MuxReadyInfo("c53input", mux.SocketPath, """{"ok":true}"""),
                commandPort,
                live,
                new SnapshotAssembler(),
                tty,
                gate,
                renew,
                CancellationToken.None);
            Assert.True(recovered.Ok, recovered.Detail);
            Assert.NotNull(session.LastReconnectOffer);
            Assert.DoesNotContain(
                session.LastReconnectOffer!.ReplayFrames,
                frame => frame.Direction == StreamDirection.ClientToMux);
            Assert.Equal(1, reconnect.InputAppliedCount);
            Assert.Equal(new[] { "once\n" }, mux.Factory.Writes.ToArray());
            if (recovered.Control is not null)
                await recovered.Control.DisposeAsync();
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Live_journal_subscribe_replays_after_last_received()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var (dir, sock) = NewPrivateSocketPath("c53journal");
        try
        {
            await using var mux = await LiveMux.StartAsync(dir);
            for (var i = 0; i < 4; i++)
            {
                var appended = await mux.Journal.AppendAsync(
                    EventClass.Control,
                    EventReliability.Reliable,
                    "control.note",
                    """{"ok":true}""");
                Assert.True(appended.IsOk);
            }

            var reconnect = new AttachReconnectService();
            var attached = reconnect.Attach(Cap("nonce121"));
            Assert.True(attached.Ok, attached.Detail);
            Assert.True(reconnect.NoteObserved(MuxControl(0, 2, """{"seq":2}""")).Ok);
            var session = new AttachSession(reconnect: reconnect);
            await using var placeholder = new ControlPlaneClient(mux.SocketPath);
            await placeholder.ConnectAsync();
            var commandPort = new ControlPlaneAttachCommandPort(placeholder);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            using var capture = new MemoryStream();
            using var tty = new UnixRawTerminal(capture, 8, 4);
            using var gate = new SemaphoreSlim(1, 1);
            var renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask);
            var created = await mux.CreateWorkspaceAsync();
            var live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = new AttachCommandDispatcher(
                    commandPort, created.WorkspaceId, created.TabId, created.PaneId),
                Renew = renew,
                WorkspaceId = created.WorkspaceId,
                TabId = created.TabId,
                PaneId = created.PaneId,
                ChromeEnabled = false,
                ReconnectPort = reconnect,
            };

            var recovered = await session.ReconnectAfterStallAsync(
                new MuxReadyInfo("c53journal", mux.SocketPath, """{"ok":true}"""),
                commandPort,
                live,
                new SnapshotAssembler(),
                tty,
                gate,
                renew,
                CancellationToken.None);
            Assert.True(recovered.Ok, recovered.Detail);
            Assert.False(session.LastReconnectOffer!.SnapshotPaint);
            Assert.Equal(2UL, session.LastReconnectRequest!.LastReceived[0].LastReceivedSequence);
            var subscribe = AttachSession.BuildEventsSubscribeParams(
                ["control"],
                session.LastReconnectRequest.LastReceived,
                session.LastReconnectOffer.SnapshotPaint);
            Assert.Equal(2L, subscribe["from_seq"]!.GetValue<long>());
            Assert.Equal(1000, subscribe["replay_budget"]!.GetValue<int>());

            await using var observer = new ControlPlaneClient(mux.SocketPath);
            await observer.ConnectAsync();
            await observer.CallAsync(ProtocolMethods.EventsSubscribe, subscribe);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var seen = new List<long>();
            while (DateTime.UtcNow < deadline)
            {
                foreach (var ev in observer.DrainPendingEvents())
                    CollectEventSeqs(ev, seen);

                if (seen.Contains(3) && seen.Contains(4))
                    break;
                await Task.Delay(20);
            }

            Assert.Contains(3L, seen);
            Assert.Contains(4L, seen);
            Assert.DoesNotContain(1L, seen);
            Assert.DoesNotContain(2L, seen);
            if (recovered.Control is not null)
                await recovered.Control.DisposeAsync();
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public async Task Channel_framed_pair_sends_reconnect_request_to_peer()
    {
        var pair = ChannelFramedSession.Pair("plc_chan01");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var service = new AttachReconnectService();
        var attached = service.Attach(Cap("chan001a", placement: "plc_chan01"));
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.NoteObserved(MuxBinary(1, 4, "grid")).Ok);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);
        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = Cap("chan001b", placement: "plc_chan01"),
            LastReceived = service.LastReceived,
        };
        var json = AttachReconnectCodec.Write(request);
        Assert.Contains("\"type\":\"attach.reconnect\"", json, StringComparison.Ordinal);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(mux, service, cts.Token).AsTask(),
            cts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(client, request, cts.Token);
        var served = await serve;
        Assert.True(served.Ok, served.Detail);
        Assert.True(offered.Ok, offered.Detail);
        Assert.True(offered.Value!.InputLeaseRequired);
        Assert.DoesNotContain(
            offered.Value.ReplayFrames,
            frame => frame.Direction == StreamDirection.ClientToMux);
        var round = AttachReconnectCodec.Read(json);
        Assert.True(round.Ok, round.Detail);
        Assert.Equal(request.AttemptId.Value, round.Value!.AttemptId.Value);
    }

    [Fact]
    public async Task Framed_peer_rejects_capability_for_another_placement()
    {
        var pair = ChannelFramedSession.Pair("plc_chan01");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var service = new AttachReconnectService();
        var spy = new CountingReconnect(service);
        var attached = service.Attach(Cap("chanmix1"));
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);
        var previous = service.AttemptId;
        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = Cap("chanmix2"),
            LastReceived = [],
        };
        var bound = AttachReconnectRules.ValidateFramedBinding(mux.Binding, request);
        Assert.False(bound.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, bound.Reason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(mux, spy, cts.Token).AsTask(),
            cts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(client, request, cts.Token);
        var served = await serve;
        Assert.False(offered.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, offered.Reason);
        Assert.False(served.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, served.Reason);
        Assert.Equal(0, spy.ReconnectCalls);
        Assert.Equal(AttachReconnectState.Stalled, service.State);
        Assert.Equal(previous.Value, service.AttemptId.Value);
        Assert.True(service.MuxRunning);
        Assert.False(service.DumpedToShell);
    }

    [Fact]
    public async Task Framed_peer_rejects_cursor_for_another_placement()
    {
        var pair = ChannelFramedSession.Pair("plc_chan01");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var service = new AttachReconnectService();
        var spy = new CountingReconnect(service);
        var first = Cap("chanmix3", placement: "plc_chan01");
        var attached = service.Attach(first);
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.NoteObserved(MuxBinary(1, 4, "grid")).Ok);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);
        var previous = service.AttemptId;
        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = Cap("chanmix4", placement: "plc_chan01"),
            LastReceived =
            [
                new ChannelCursor
                {
                    ChannelId = 1,
                    LastReceivedSequence = 4,
                    AttemptId = attached.Value.Value,
                    PlacementId = "plc_other1",
                    DeviceId = first.DeviceId.Value,
                    Role = first.Role,
                },
            ],
        };
        var bound = AttachReconnectRules.ValidateFramedBinding(mux.Binding, request);
        Assert.False(bound.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, bound.Reason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(mux, spy, cts.Token).AsTask(),
            cts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(client, request, cts.Token);
        var served = await serve;
        Assert.False(offered.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, offered.Reason);
        Assert.False(served.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, served.Reason);
        Assert.Equal(0, spy.ReconnectCalls);
        Assert.Equal(AttachReconnectState.Stalled, service.State);
        Assert.Equal(previous.Value, service.AttemptId.Value);
        Assert.True(service.MuxRunning);
        Assert.False(service.DumpedToShell);
    }

    [Fact]
    public async Task Framed_binding_rejects_mismatched_placement_and_role()
    {
        var pair = ChannelFramedSession.Pair("plc_chan01");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var clientCap = Request(
            AttachAttemptId.New(),
            Cap("bindok01", placement: "plc_chan01"),
            []);
        Assert.True(AttachReconnectRules.ValidateFramedBinding(mux.Binding, clientCap).Ok);
        Assert.False(AttachReconnectRules.ValidateFramedBinding(client.Binding, clientCap).Ok);

        var otherPlacement = Request(
            AttachAttemptId.New(),
            Cap("bindbad1"),
            []);
        var placement = AttachReconnectRules.ValidateFramedBinding(
            mux.Binding,
            otherPlacement);
        Assert.False(placement.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, placement.Reason);

        var muxRole = Request(
            AttachAttemptId.New(),
            Cap("bindbad2", placement: "plc_chan01", role: JoinRole.Mux),
            []);
        var role = AttachReconnectRules.ValidateFramedBinding(mux.Binding, muxRole);
        Assert.False(role.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, role.Reason);
    }

    [Fact]
    public async Task Framed_peer_rejects_reused_capability_on_the_wire()
    {
        var pair = ChannelFramedSession.Pair("plc_recrej");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var service = new AttachReconnectService();
        var first = Cap("recrej01", placement: "plc_recrej");
        var attached = service.Attach(first);
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);

        var reused = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = first,
            LastReceived = [],
        };
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(mux, service, handshakeCts.Token).AsTask(),
            handshakeCts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(client, reused, handshakeCts.Token);
        var served = await serve;
        Assert.False(offered.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, offered.Reason);
        Assert.False(served.Ok);
        Assert.Equal(AttachReconnectState.Stalled, service.State);
    }

    [Fact]
    public void Remote_detach_does_not_print_mux_stop()
    {
        var remote = AttachLossPolicy.FormatBanner(null, remoteDestination: true);
        Assert.Equal(AttachLossPolicy.RemoteDetachBanner, remote);
        Assert.DoesNotContain("hypa mux stop", remote, StringComparison.Ordinal);
        var named = AttachLossPolicy.FormatBanner(
            "connection closed",
            remoteDestination: true,
            displayName: "Desk");
        Assert.Equal("disconnected from Desk. Destination mux is still running.", named);
        Assert.DoesNotContain("hypa mux stop", named, StringComparison.Ordinal);
        var local = AttachLossPolicy.FormatBanner(null, remoteDestination: false);
        Assert.Contains("hypa mux stop", local, StringComparison.Ordinal);
        Assert.Equal(AttachLossPolicy.LocalDetachBanner, local);
    }

    [Fact]
    public async Task Control_plane_drop_keeps_mux_and_invalidates_lease()
    {
        var leases = new InMemoryLeaseRegistry();
        var attachments = new InMemoryAttachmentRegistry();
        var state = new AppState(SessionId.New("reconnect-drop"));
        var cp = new ControlPlaneService(
            state,
            new UnusedPaneFactory(),
            new NullIntelligence(),
            new NullDetector(),
            leases: leases,
            attachments: attachments);
        var claim = leases.Claim("p1", LeaseScopes.Input, "conn_stall", false, null, 30_000);
        Assert.Equal(LeaseOutcomes.Granted, claim.Outcome);
        var conn = new FakeConnection("conn_stall");
        cp.OnClientDisconnected(conn, AttachClientLoss.Stall);
        Assert.Equal(AttachClientLoss.Stall, cp.LastClientLoss);
        Assert.False(cp.ServerStopStarted);
        var auth = leases.TryAuthorize("p1", LeaseScopes.Input, claim.Lease!.LeaseId, "conn_stall");
        Assert.NotEqual(LeaseAuthorizeStatus.Authorized, auth.Status);

        var reclaim = leases.Claim("p1", LeaseScopes.Input, "conn_next", true, "reconnect", 30_000);
        Assert.Equal(LeaseOutcomes.Granted, reclaim.Outcome);
        Assert.NotEqual(claim.Lease.LeaseId, reclaim.Lease!.LeaseId);

        var ping = await cp.DispatchAsync(ProtocolMethods.Ping, null, CancellationToken.None);
        Assert.Equal(JsonValueKind.Object, ping.ValueKind);
        Assert.True(ping.TryGetProperty("ok", out var ok) && ok.GetBoolean());
        Assert.False(cp.ServerStopStarted);
    }

    [Fact]
    public void True_disconnect_drops_attachments_and_leases_without_server_stop()
    {
        var leases = new InMemoryLeaseRegistry();
        var attachments = new InMemoryAttachmentRegistry();
        var state = new AppState(SessionId.New("reconnect-disc"));
        var cp = new ControlPlaneService(
            state,
            new UnusedPaneFactory(),
            new NullIntelligence(),
            new NullDetector(),
            leases: leases,
            attachments: attachments);
        leases.Claim("p1", LeaseScopes.Input, "conn_drop", false, null, 30_000);
        var created = attachments.Create("p1", "conn_drop", "sub_1", AttachmentModes.Observe, null);
        Assert.False(created.Invalid);
        cp.OnClientDisconnected(new FakeConnection("conn_drop"));
        Assert.Equal(AttachClientLoss.Disconnect, cp.LastClientLoss);
        Assert.Null(leases.GetActive("p1", LeaseScopes.Input));
        Assert.DoesNotContain(attachments.ListAll(), a => a.ConnectionId == "conn_drop");
        Assert.False(cp.ServerStopStarted);
    }

    [Fact]
    public void Reconnect_json_round_trips_source_generated_snake_case()
    {
        var document = new AttachReconnectRequestDocument
        {
            PreviousAttemptId = "att_prev0001",
            AttemptId = "att_next0001",
            Capability = new JoinCapabilityDocument
            {
                PlacementId = "plc_json1",
                DeviceId = "dev_alpha1",
                Role = JoinRole.Client,
                ExpiresAt = "2026-08-31T00:00:00.0000000Z",
                Nonce = "nonce101",
            },
            LastReceived =
            [
                new ChannelCursorDocument
                {
                    ChannelId = 1,
                    LastReceivedSequence = 12,
                    AttemptId = "att_prev0001",
                    PlacementId = "plc_json1",
                    DeviceId = "dev_alpha1",
                    Role = JoinRole.Client,
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            document,
            ConnectivityJsonContext.Default.AttachReconnectRequestDocument);
        Assert.Contains("\"previous_attempt_id\":\"att_prev0001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"attempt_id\":\"att_next0001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"last_received_sequence\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"channel_id\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"attempt_id\":\"att_prev0001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"placement_id\":\"plc_json1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"device_id\":\"dev_alpha1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hypa mux stop", json, StringComparison.Ordinal);
        Assert.DoesNotContain("socket_path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("previousAttemptId", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize(
            json,
            ConnectivityJsonContext.Default.AttachReconnectRequestDocument);
        Assert.NotNull(round);
        Assert.Equal("att_next0001", round!.AttemptId);
        Assert.Single(round.LastReceived);
        Assert.Equal(12UL, round.LastReceived[0].LastReceivedSequence);
        Assert.Equal("att_prev0001", round.LastReceived[0].AttemptId);
        Assert.Equal("plc_json1", round.LastReceived[0].PlacementId);
        Assert.Equal("dev_alpha1", round.LastReceived[0].DeviceId);
        Assert.Equal(JoinRole.Client, round.LastReceived[0].Role);

        var offer = new AttachReconnectOfferDocument
        {
            AttemptId = "att_next0001",
            SnapshotPaint = true,
            InputLeaseRequired = true,
            ReplayFrameCount = 0,
        };
        var offerJson = JsonSerializer.Serialize(
            offer,
            ConnectivityJsonContext.Default.AttachReconnectOfferDocument);
        Assert.Contains("\"snapshot_paint\":true", offerJson, StringComparison.Ordinal);
        Assert.Contains("\"input_lease_required\":true", offerJson, StringComparison.Ordinal);
    }

    private static AttachReconnectRequest Request(
        AttachAttemptId previous,
        JoinCapability capability,
        IReadOnlyList<ChannelCursor> lastReceived) =>
        new()
        {
            PreviousAttemptId = previous,
            AttemptId = AttachAttemptId.New(),
            Capability = capability,
            LastReceived = lastReceived,
        };

    private static JoinCapability Cap(
        string nonce,
        string placement = "plc_reconn",
        string device = "dev_cli001",
        JoinRole role = JoinRole.Client)
    {
        Assert.True(JoinNonce.TryParse(nonce, out var parsed));
        Assert.True(DeviceId.TryParse(device, out var parsedDevice));
        Assert.True(PlacementId.TryParse(placement, out var parsedPlacement));
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            parsedPlacement,
            parsedDevice,
            role,
            parsed,
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow);
        Assert.True(issued.Ok, issued.Detail);
        return issued.Value!;
    }

    private sealed class CountingReconnect(AttachReconnectService inner) : IAttachReconnect
    {
        public int ReconnectCalls { get; private set; }

        public AttachReconnectState State => inner.State;

        public AttachAttemptId AttemptId => inner.AttemptId;

        public JoinCapability? Capability => inner.Capability;

        public bool MuxRunning => inner.MuxRunning;

        public bool DumpedToShell => inner.DumpedToShell;

        public bool ServerStopRequested => inner.ServerStopRequested;

        public string? LeaseId => inner.LeaseId;

        public int PaintCount => inner.PaintCount;

        public int InputAppliedCount => inner.InputAppliedCount;

        public IReadOnlyList<ChannelCursor> LastReceived => inner.LastReceived;

        public ConnectivityOutcome<AttachAttemptId> Attach(JoinCapability capability) =>
            inner.Attach(capability);

        public AttachAttemptId MintAttemptId() => inner.MintAttemptId();

        public ConnectivityOutcome NoteObserved(StreamFrame frame) => inner.NoteObserved(frame);

        public ConnectivityOutcome ApplyInput(StreamFrame frame) => inner.ApplyInput(frame);

        public ConnectivityOutcome Stall(TimeSpan gap) => inner.Stall(gap);

        public ConnectivityOutcome Detach() => inner.Detach();

        public ConnectivityOutcome Disconnect() => inner.Disconnect();

        public ConnectivityOutcome<AttachReconnectOffer> Reconnect(AttachReconnectRequest request)
        {
            ReconnectCalls++;
            return inner.Reconnect(request);
        }

        public ConnectivityOutcome ReclaimLease(string leaseId) => inner.ReclaimLease(leaseId);

        public ConnectivityOutcome AdmitWithoutInputLease() => inner.AdmitWithoutInputLease();

        public ConnectivityOutcome CompleteReplay(bool painted) => inner.CompleteReplay(painted);
    }

    private sealed class RecordingReconnectPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(
            string method,
            JsonObject? parameters,
            CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("""{"ok":true}""").RootElement.Clone());
    }

    private static void CollectEventSeqs(JsonElement ev, List<long> seen)
    {
        if (ev.ValueKind != JsonValueKind.Object)
            return;
        if (!ev.TryGetProperty("params", out var parms) || parms.ValueKind != JsonValueKind.Object)
            return;
        if (parms.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value))
            seen.Add(value);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static (string FileName, string[] Prefix)? TryFindHypaLaunch()
    {
        var nativeName = OperatingSystem.IsWindows() ? "hypa.exe" : "hypa";
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var bin = Path.Combine(dir, "src", "Hypa.Cli", "bin", config, "net10.0");
                var native = Path.Combine(bin, nativeName);
                if (File.Exists(native))
                    return (native, []);
                var dll = Path.Combine(bin, "hypa.dll");
                if (File.Exists(dll))
                    return ("dotnet", ["exec", dll]);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static void TryKillMux(Process? mux)
    {
        if (mux is null)
            return;
        try
        {
            if (!mux.HasExited)
                mux.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
        }

        try
        {
            mux.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static (string Dir, string Sock) NewPrivateSocketPath(string prefix)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", prefix + "-" + id);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "hypa.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static StreamFrame MuxBinary(uint channel, ulong sequence, string text) =>
        StreamFrame.Binary(
            StreamDirection.MuxToClient,
            channel,
            sequence,
            Encoding.UTF8.GetBytes(text));

    private static StreamFrame ClientBinary(uint channel, ulong sequence, string text) =>
        StreamFrame.Binary(
            StreamDirection.ClientToMux,
            channel,
            sequence,
            Encoding.UTF8.GetBytes(text));

    private static StreamFrame MuxControl(uint channel, ulong sequence, string json) =>
        StreamFrame.Control(
            StreamDirection.MuxToClient,
            sequence,
            Encoding.UTF8.GetBytes(json),
            channel);

    private sealed class FakeConnection : IClientConnection
    {
        public FakeConnection(string id) => ConnectionId = id;

        public string ConnectionId { get; }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class UnusedPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            throw new InvalidOperationException("unused pane factory");
    }

    private sealed class LiveMux : IAsyncDisposable
    {
        private readonly UnixSocketServer _server;

        private LiveMux(
            ControlPlaneService cp,
            UnixSocketServer server,
            GraphPaneFactory factory,
            MemoryJournal journal,
            string socketPath)
        {
            ControlPlane = cp;
            _server = server;
            Factory = factory;
            Journal = journal;
            SocketPath = socketPath;
        }

        public ControlPlaneService ControlPlane { get; }

        public GraphPaneFactory Factory { get; }

        public MemoryJournal Journal { get; }

        public string SocketPath { get; }

        public static async Task<LiveMux> StartAsync(string socketDir)
        {
            var socket = Path.Combine(socketDir, "hypa.sock");
            var state = new AppState(SessionId.New("c53-live"));
            var factory = new GraphPaneFactory();
            var journal = new MemoryJournal();
            var hub = new EventSubscriptionHub();
            var cp = new ControlPlaneService(
                state,
                factory,
                new NullIntelligence(),
                new NullDetector(),
                journal: journal,
                subscriptions: hub,
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            var server = new UnixSocketServer(cp, socket);
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new LiveMux(cp, server, factory, journal, socket);
        }

        public async Task<(string WorkspaceId, string TabId, string PaneId)> CreateWorkspaceAsync()
        {
            await using var client = new ControlPlaneClient(SocketPath);
            await client.ConnectAsync().ConfigureAwait(false);
            var created = await client.CallAsync(
                    ProtocolMethods.WorkspaceCreate,
                    new JsonObject
                    {
                        ["cwd"] = "/tmp/c53-live",
                        ["create_pane"] = true,
                        ["command"] = "stub",
                        ["label"] = "main",
                    })
                .ConfigureAwait(false);
            return (
                created.GetProperty("workspace_id").GetString()!,
                created.GetProperty("focused_tab_id").GetString()!,
                created.GetProperty("pane").GetProperty("pane_id").GetString()!);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            await ControlPlane.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class MemoryJournal : IRuntimeEventJournal
    {
        private long _seq = 1;
        public List<RuntimeEventRecord> Records { get; } = [];

        public Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<JournalHealth>.Ok(GetHealth()));

        public Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
            EventClass @class,
            EventReliability reliability,
            string type,
            string payloadJson,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default)
        {
            var rec = new RuntimeEventRecord
            {
                Seq = _seq++,
                Class = @class,
                Reliability = reliability,
                Type = type,
                OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
                PayloadJson = payloadJson,
            };
            Records.Add(rec);
            return Task.FromResult(RuntimeResult<RuntimeEventRecord>.Ok(rec));
        }

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
            long fromSeqExclusive,
            IReadOnlySet<EventClass>? classes,
            int budget,
            CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(
                Records.Where(r => r.Seq > fromSeqExclusive).Take(budget).ToList()));

        public Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public JournalHealth GetHealth() => new()
        {
            NextSeq = _seq,
            ReplayComplete = true,
            Bytes = 0,
        };

        public long NextSeq => _seq;
    }

    internal sealed class GraphPaneFactory : IPaneRuntimeFactory
    {
        public List<string> Writes { get; } = [];

        public IPaneRuntime Create(PaneSpawnOptions options) => new GraphPaneRuntime(options.Id, Writes);
    }

    private sealed class GraphPaneRuntime(PaneId id, List<string> writes) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_053;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            writes.Add(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            writes.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(!IsAlive, this);
            return ValueTask.CompletedTask;
        }

        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullIntelligence : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
