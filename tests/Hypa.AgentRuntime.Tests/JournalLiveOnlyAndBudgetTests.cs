using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Hypa.Connectivity.Domain;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class JournalLiveOnlyAndBudgetTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public JournalLiveOnlyAndBudgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-livejr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Pane_output_does_not_grow_the_journal_and_reliable_intake_continues()
    {
        const long budget = 256 * 1024;
        var (cp, paneId, journal, runtime) = await StartPlaneAsync(budget);
        try
        {
            runtime.FireOutput("VISIBLE-MARK\n");
            await WaitUntil(() => Types(_sink).Contains(ProtocolEventTypes.TerminalOutput));
            // Commit the initial detection status before measuring the output burst.
            cp.ScanDetectionNowForTests(paneId);
            await WaitCommitsAsync(journal);
            var before = journal.GetHealth().Bytes;
            var chunk = new string('A', (int)budget);
            runtime.FireOutput(chunk);
            runtime.FireOutput(chunk);
            runtime.FireOutput(chunk);
            runtime.FireOutput(new string('B', 1024 * 1024));
            await Task.Delay(100);

            var health = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, Empty(), CancellationToken.None);
            var bytes = health.GetProperty("journal").GetProperty("bytes").GetInt64();
            Assert.Equal(before, bytes);
            Assert.True(bytes < budget);

            var reliable = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                ProtocolEventTypes.PaneLifecycle, """{"pane_id":"p1","state":"running"}""");
            Assert.True(reliable.IsOk, reliable.IsOk ? null : reliable.Error.Message);

            var replay = await journal.ReadRangeAsync(0, classes: null, budget: 200);
            Assert.True(replay.IsOk);
            Assert.DoesNotContain(replay.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);

            var read = await cp.DispatchAsync(
                ProtocolMethods.PaneRead,
                Json(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["source"] = ProtocolPaneReadSources.Visible,
                }),
                CancellationToken.None);
            Assert.Contains("B", read.GetProperty("text").GetString());
            Assert.Contains(ProtocolEventTypes.TerminalOutput, Types(_sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            await journal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Live_events_arrive_when_the_journal_refuses_every_append()
    {
        var logs = new CapturingProcessLogSink();
        var clock = new ManualTime();
        var seeder = await OpenAsync(
            "rs_refuse", maxJournalBytes: 64 * 1024, processLog: logs, time: clock);
        var seed = await seeder.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, """{"pane_id":"seed"}""");
        Assert.True(seed.IsOk, seed.IsOk ? null : seed.Error.Message);
        await seeder.DisposeAsync();
        var journal = await OpenAsync(
            "rs_refuse", maxJournalBytes: 1, processLog: logs, time: clock, recover: true);

        var state = new AppState(SessionId.New("rs_refuse"));
        state.UpdateSession(s => s with { Name = "rs_refuse", LifecycleState = SessionLifecycle.Ready });
        var workspace = state.CreateWorkspace(_dir, label: "default");
        state.RegisterPane(new PaneState
        {
            Id = new PaneId("p1"),
            TabId = workspace.TabIds[0],
            WorkspaceId = workspace.Id,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        });
        var hub = new EventSubscriptionHub();
        var sink = new CapturingConnection();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_live",
            ConnectionId = sink.ConnectionId,
            FromSeq = 0,
            Classes = new HashSet<EventClass>(),
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
        });
        sub.EnableLive();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Scripted(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            subscriptions: hub,
            redactor: new DefaultEventPayloadRedactor());
        try
        {
            Assert.True(cp.TryApplyAgentStatus(
                "p1", expectedGeneration: 1, AgentStatus.Idle, "claude", message: null));
            await cp.EmitLayoutUpdatedAsync(workspace.TabIds[0].Value, "p1", CancellationToken.None);
            var owner = new CapturingConnection("conn_owner");
            await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = "p1", ["scope"] = "input" }),
                owner,
                CancellationToken.None);

            await WaitUntil(() =>
                Types(sink).Contains(ProtocolEventTypes.PaneAgentStatusChanged)
                && Types(sink).Contains(ProtocolEventTypes.LayoutUpdated)
                && Types(sink).Contains(ProtocolEventTypes.LeaseChanged));

            var refused = await journal.AppendAsync(
                EventClass.Control, EventReliability.Reliable,
                ProtocolEventTypes.LeaseChanged, """{"lease_id":"l2"}""");
            Assert.False(refused.IsOk);
            Assert.NotNull(refused.Error.LiveRecord);
            Assert.True(refused.Error.LiveRecord!.Seq > seed.Value.Seq);

            var again = await journal.AppendAsync(
                EventClass.Control, EventReliability.Reliable,
                ProtocolEventTypes.LeaseChanged, """{"lease_id":"l3"}""");
            Assert.False(again.IsOk);
            Assert.True(again.Error.LiveRecord!.Seq > refused.Error.LiveRecord.Seq);

            var below = await journal.ReadRangeAsync(seed.Value.Seq, classes: null, budget: 10);
            Assert.False(below.IsOk);
            Assert.Equal(RuntimePersistenceError.CursorExpiredCode, below.Error.Code);
            Assert.True(below.Error.FloorSeq > refused.Error.LiveRecord.Seq);

            Assert.Single(logs.Records, r => r.Event == ProcessLogEvents.JournalRefused);
            clock.Now = clock.Now.AddSeconds(61);
            var later = await journal.AppendAsync(
                EventClass.Control, EventReliability.Reliable,
                ProtocolEventTypes.LeaseChanged, """{"lease_id":"l4"}""");
            Assert.False(later.IsOk);
            var refusalLines = logs.Records.Where(r => r.Event == ProcessLogEvents.JournalRefused).ToArray();
            Assert.Equal(2, refusalLines.Length);
            Assert.True(refusalLines[1].DropCount >= 2);
            Assert.False(string.IsNullOrWhiteSpace(refusalLines[0].EventType));
            Assert.False(string.IsNullOrWhiteSpace(refusalLines[0].Reason));

            await using var restarted = new FileRuntimeEventJournal(
                _paths,
                new SqliteJournalManifestStore(_paths),
                "rs_refuse",
                maxJournalBytes: 1);
            Assert.True((await restarted.RecoverAsync()).IsOk);
            Assert.True(restarted.NextSeq > again.Error.LiveRecord.Seq);

            var health = journal.GetHealth();
            Assert.True(health.RefusalCount >= 2);
            Assert.Contains("budget", health.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            await journal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_below_the_floor_fails_and_replay_keeps_retained_order()
    {
        var journal = await OpenAsync("rs_floor", maxJournalBytes: 32 * 1024 * 1024, rotateRecords: 2);
        for (var i = 0; i < 6; i++)
        {
            var append = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                ProtocolEventTypes.PaneLifecycle, $$"""{"n":{{i}}}""");
            Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
        }

        await journal.CloseOpenSegmentAsync();
        await journal.DisposeAsync();

        await using var recovered = await OpenAsync(
            "rs_floor", maxJournalBytes: 400, rotateRecords: 2, recover: true);
        var health = recovered.GetHealth();
        Assert.True(health.ReplayComplete);
        Assert.True(health.Bytes <= 400);
        Assert.True(health.FloorSeq > 1);

        var expired = await recovered.ReadRangeAsync(0, classes: null, budget: 20);
        Assert.False(expired.IsOk);
        Assert.Equal(health.FloorSeq, expired.Error.FloorSeq);

        var kept = await recovered.ReadRangeAsync(health.FloorSeq - 1, classes: null, budget: 50);
        Assert.True(kept.IsOk, kept.IsOk ? null : kept.Error.Message);
        Assert.NotEmpty(kept.Value);
        Assert.Equal(health.FloorSeq, kept.Value[0].Seq);
        for (var i = 1; i < kept.Value.Count; i++)
            Assert.True(kept.Value[i].Seq > kept.Value[i - 1].Seq);

        var state = new AppState(SessionId.New("rs_floor"));
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: recovered,
            subscriptions: new EventSubscriptionHub());
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject { ["from_seq"] = 0, ["live"] = true }),
                new CapturingConnection(),
                CancellationToken.None));
        Assert.Equal(ProtocolErrors.CursorExpired, ex.ErrorCode);
        Assert.Equal(health.FloorSeq, ex.FloorSeq);
        await cp.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unacked_export_and_a_barrier_stop_compaction()
    {
        await WriteClosedSegmentsAsync("rs_pin", count: 30);
        await ExecAsync(
            """
            INSERT INTO event_exports(export_id, session_id, consumer, last_acked_seq, updated_at)
            VALUES ('ex1', 'rs_pin', 'c', 0, '2026-09-28T00:00:00Z');
            """);

        await using var pinned = await OpenAsync(
            "rs_pin", maxJournalBytes: 512, rotateBytes: 256, recover: true, retention: true);
        Assert.True(pinned.GetHealth().ReplayComplete);
        Assert.True(pinned.GetHealth().Bytes > 512);
        var refused = await pinned.AppendAsync(
            EventClass.Control, EventReliability.Reliable,
            ProtocolEventTypes.LeaseChanged, """{"lease_id":"held"}""");
        Assert.False(refused.IsOk);
        Assert.NotNull(refused.Error.LiveRecord);
        Assert.Equal(ProtocolEventTypes.LeaseChanged, refused.Error.LiveRecord!.Type);

    }

    [Fact]
    public async Task Prepared_checkpoint_barrier_stops_compaction()
    {
        await WriteClosedSegmentsAsync("rs_barrier", count: 30);
        await ExecAsync(
            """
            INSERT INTO checkpoints(
              checkpoint_id, session_id, state, barrier_seq, next_seq_at_prepare,
              session_fingerprint, created_at)
            VALUES ('cp1', 'rs_barrier', 'prepared', 1, 1, 'fp', '2026-09-28T00:00:00Z');
            """);
        await using var held = await OpenAsync(
            "rs_barrier", maxJournalBytes: 512, rotateBytes: 256, recover: true, retention: true);
        Assert.True(held.GetHealth().Bytes > 512);
        Assert.True(held.GetHealth().ReplayComplete);
    }

    [Fact]
    public async Task Handoff_lifecycle_stops_compaction()
    {
        await WriteClosedSegmentsAsync("rs_handoff", count: 12);
        await ExecAsync(
            "UPDATE sessions SET lifecycle_state = 'handoff_pending' WHERE session_id = 'rs_handoff';");
        await using var handoff = await OpenAsync(
            "rs_handoff", maxJournalBytes: 512, rotateBytes: 256, recover: true, retention: true);
        Assert.True(handoff.GetHealth().Bytes > 512);
        Assert.True(handoff.GetHealth().ReplayComplete);
    }

    [Fact]
    public async Task Crash_between_manifest_delete_and_file_delete_stays_replay_complete()
    {
        var journal = await OpenAsync("rs_orphan", maxJournalBytes: 32 * 1024 * 1024, rotateRecords: 2);
        for (var i = 0; i < 4; i++)
        {
            Assert.True((await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                ProtocolEventTypes.PaneLifecycle, $$"""{"n":{{i}}}""")).IsOk);
        }

        await journal.CloseOpenSegmentAsync();
        var store = new SqliteJournalManifestStore(_paths);
        var list = await store.ListSegmentsAsync("rs_orphan");
        Assert.True(list.IsOk);
        var oldest = list.Value.OrderBy(s => s.FirstSeq).First();
        var floor = oldest.LastSeq!.Value + 1;
        Assert.True((await store.DeleteClosedSegmentAsync(oldest.SegmentId, "rs_orphan", floor)).IsOk);
        Assert.True(File.Exists(Path.Combine(_paths.StateDirectory, oldest.RelativePath)));
        await journal.DisposeAsync();

        await using var recovered = await OpenAsync(
            "rs_orphan", maxJournalBytes: 32 * 1024 * 1024, rotateRecords: 2, recover: true);
        var health = recovered.GetHealth();
        Assert.True(health.ReplayComplete);
        Assert.False(File.Exists(Path.Combine(_paths.StateDirectory, oldest.RelativePath)));
        var range = await recovered.ReadRangeAsync(health.FloorSeq - 1, classes: null, budget: 20);
        Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
        Assert.DoesNotContain(range.Value, r => r.Seq < health.FloorSeq);
    }

    [Fact]
    public async Task Ten_times_the_budget_compacts_on_the_first_recovery()
    {
        const long budget = 4 * 1024;
        var journal = await OpenAsync(
            "rs_ten", maxJournalBytes: budget * 40, rotateBytes: 300);
        var payload = "{\"n\":\"" + new string('b', 80) + "\"}";
        for (var i = 0; i < 200; i++)
        {
            var append = await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                ProtocolEventTypes.PaneLifecycle, payload);
            Assert.True(append.IsOk, append.IsOk ? null : append.Error.Message);
        }

        await journal.CloseOpenSegmentAsync();
        await journal.DisposeAsync();
        var before = JournalBytes();
        Assert.True(before > budget * 10, $"journal was {before} bytes");

        await using var recovered = await OpenAsync(
            "rs_ten", maxJournalBytes: budget, rotateBytes: 300, recover: true);
        var health = recovered.GetHealth();
        Assert.True(health.ReplayComplete);
        Assert.True(health.Bytes <= budget, $"recovered {health.Bytes} bytes");
    }

    [SkippableFact]
    public async Task Attach_subscribe_repaints_from_snapshot_when_the_cursor_expires()
    {
        Skip.If(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(), "Unix sockets only.");

        await WriteClosedSegmentsAsync("rs_attach", count: 20);
        await using var journal = await OpenAsync(
            "rs_attach", maxJournalBytes: 700, rotateBytes: 256, recover: true);
        Assert.True(journal.GetHealth().FloorSeq > 1);

        var state = new AppState(SessionId.New("rs_attach"));
        state.UpdateSession(s => s with { Name = "rs_attach", LifecycleState = SessionLifecycle.Ready });
        var workspace = state.CreateWorkspace(_dir, label: "default");
        state.RegisterPane(new PaneState
        {
            Id = new PaneId("p1"),
            TabId = workspace.TabIds[0],
            WorkspaceId = workspace.Id,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        });
        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            subscriptions: hub);
        var sock = Path.Combine(_dir, "hypa.sock");
        await using var server = new UnixSocketServer(cp, sock);
        await server.StartAsync(CancellationToken.None);
        await using var client = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
        await client.ConnectAsync();

        var painted = 0;
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SnapshotCommandPort(), "stale", "stale", "stale", ""),
            ChromeEnabled = true,
            PaneId = "stale",
            WorkspaceId = "stale",
            TabId = "stale",
        };
        // A resume from a cursor below the floor.
        var subscribed = await AttachSession.SubscribeAsync(
            client,
            ["control", "lifecycle"],
            CancellationToken.None,
            lastReceived: [new ChannelCursor { ChannelId = 0, LastReceivedSequence = 0 }],
            snapshotPaint: false,
            onSnapshot: snap =>
            {
                painted++;
                AttachSession.ApplyRecoveredSessionSnapshot(live, tty: null, snap);
            });
        Assert.False(string.IsNullOrWhiteSpace(subscribed.SubscriptionId));
        Assert.Equal(1, painted);
        Assert.Equal("p1", live.PaneId);
        Assert.NotNull(live.LastSnapshot);

        Assert.True(cp.TryApplyAgentStatus(
            "p1", expectedGeneration: 1, AgentStatus.Idle, "claude", message: null));
        var until = DateTime.UtcNow.AddSeconds(2);
        var saw = false;
        while (DateTime.UtcNow < until && !saw)
        {
            foreach (var ev in client.DrainPendingEvents())
            {
                if (ev.TryGetProperty("params", out var p)
                    && p.TryGetProperty("type", out var type)
                    && type.GetString() == ProtocolEventTypes.PaneAgentStatusChanged)
                {
                    saw = true;
                }
            }

            if (!saw)
                await Task.Delay(20);
        }

        Assert.True(saw);
        await cp.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_refused_append_still_publishes_and_counts_the_refusal()
    {
        var sync = new ThrowNextSync();
        await using var journal = await OpenAsync("rs_io", durableSync: sync);
        var state = new AppState(SessionId.New("rs_io"));
        state.UpdateSession(s => s with { Name = "rs_io", LifecycleState = SessionLifecycle.Ready });
        var workspace = state.CreateWorkspace(_dir, label: "default");
        state.RegisterPane(new PaneState
        {
            Id = new PaneId("p1"),
            TabId = workspace.TabIds[0],
            WorkspaceId = workspace.Id,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        });
        var hub = new EventSubscriptionHub();
        var sink = new CapturingConnection();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_io",
            ConnectionId = sink.ConnectionId,
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Lifecycle, EventClass.Control },
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
        });
        sub.EnableLive();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            subscriptions: hub);
        sync.ThrowNext = true;
        Assert.True(cp.TryApplyAgentStatus(
            "p1", expectedGeneration: 1, AgentStatus.Idle, "claude", message: null));
        var until = DateTime.UtcNow.AddSeconds(2);
        var saw = false;
        while (DateTime.UtcNow < until && !saw)
        {
            saw = Types(sink).Contains(ProtocolEventTypes.PaneAgentStatusChanged);
            if (!saw)
                await Task.Delay(20);
        }

        Assert.True(saw);
        await WaitCommitsAsync(journal);
        var health = journal.GetHealth();
        Assert.True(health.RefusalCount >= 1);
        Assert.True(health.FloorSeq > 1);
        Assert.False(string.IsNullOrWhiteSpace(health.LastRefusalReason));
    }

    [Fact]
    public async Task Live_publish_does_not_wait_for_the_file_sync()
    {
        var sync = new HoldSync();
        await using var journal = await OpenAsync("rs_hold", durableSync: sync);
        var state = new AppState(SessionId.New("rs_hold"));
        state.UpdateSession(s => s with { Name = "rs_hold", LifecycleState = SessionLifecycle.Ready });
        var workspace = state.CreateWorkspace(_dir, label: "default");
        state.RegisterPane(new PaneState
        {
            Id = new PaneId("p1"),
            TabId = workspace.TabIds[0],
            WorkspaceId = workspace.Id,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        });
        var hub = new EventSubscriptionHub();
        var sink = new CapturingConnection();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_hold",
            ConnectionId = sink.ConnectionId,
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Lifecycle },
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
        });
        sub.EnableLive();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            subscriptions: hub);
        var started = DateTime.UtcNow;
        Assert.True(cp.TryApplyAgentStatus(
            "p1", expectedGeneration: 1, AgentStatus.Idle, "claude", message: null));
        var entered = await Task.WhenAny(sync.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(sync.Entered.Task, entered);
        var saw = false;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && !saw)
        {
            saw = Types(sink).Contains(ProtocolEventTypes.PaneAgentStatusChanged);
            if (!saw)
                await Task.Delay(10);
        }

        var publishMs = (DateTime.UtcNow - started).TotalMilliseconds;
        double heldMs;
        try
        {
            Assert.True(saw, "live event arrived while file sync was blocked");
            await Task.Delay(50);
            heldMs = (DateTime.UtcNow - sync.BlockedAt).TotalMilliseconds;
        }
        finally
        {
            sync.Release.TrySetResult();
        }

        await WaitCommitsAsync(journal);
        Assert.Equal(0, journal.GetHealth().RefusalCount);
    }

    [Fact]
    public async Task Floor_from_a_failed_floor_write_is_in_the_next_progress_write()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await ExecAsync(
            """
            INSERT OR IGNORE INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete)
            VALUES ('rs_floor', 'j', 'ready', 'local', 0,
              '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z', 1);
            """);
        var inner = new SqliteJournalManifestStore(_paths);
        var store = new FloorRaiseGate(inner);
        var sync = new ThrowNextSync();
        await using var journal = new FileRuntimeEventJournal(
            _paths, store, "rs_floor", durableSync: sync, maxJournalBytes: 32 * 1024 * 1024);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":1}");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);
        store.FailRaise = true;
        sync.ThrowNext = true;
        var failed = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":2}");
        Assert.False(failed.IsOk);
        var gapSeq = failed.Error.LiveRecord!.Seq;
        var third = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":3}");
        Assert.True(third.IsOk, third.IsOk ? null : third.Error.Message);
        Assert.True(third.Value.Seq > gapSeq);
        await journal.DisposeAsync();

        await using var recovered = await OpenAsync("rs_floor", recover: true);
        var health = recovered.GetHealth();
        Assert.True(health.FloorSeq > gapSeq, $"floor {health.FloorSeq} gap {gapSeq}");
        Assert.True(health.NextSeq > gapSeq);
        var again = await recovered.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":4}");
        Assert.True(again.IsOk, again.IsOk ? null : again.Error.Message);
        Assert.True(again.Value.Seq > gapSeq);
    }

    [Fact]
    public async Task An_append_that_would_pass_the_budget_is_refused_before_the_write()
    {
        await using var journal = await OpenAsync("rs_fit", maxJournalBytes: 900);
        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":1}");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);
        var before = journal.GetHealth().Bytes;
        var second = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, "{\"n\":\"" + new string('x', 2000) + "\"}");
        Assert.False(second.IsOk);
        Assert.Equal(before, journal.GetHealth().Bytes);
        Assert.True(journal.GetHealth().FloorSeq > first.Value.Seq);
    }

    [Fact]
    public async Task Terminal_output_reaches_a_subscriber_that_resumed_from_a_journal_cursor()
    {
        // Output is numbered per pane, not on the journal cursor. A subscriber
        // with a high from_seq must still get the first output of a pane.
        var (cp, paneId, _, runtime) = await StartPlaneAsync(32 * 1024, fromSeq: 100_000);
        runtime.FireOutput("one\n");
        runtime.FireOutput("two\n");
        await WaitUntil(() => OutputSeqs(_sink).Count >= 2);
        var seqs = OutputSeqs(_sink);
        Assert.Equal(1, seqs[0]);
        for (var i = 1; i < seqs.Count; i++)
            Assert.True(seqs[i] > seqs[i - 1]);
        await cp.ShutdownAsync(CancellationToken.None);
        _ = paneId;
    }

    private static List<long> OutputSeqs(CapturingConnection sink)
    {
        var seqs = new List<long>();
        foreach (var line in sink.Lines.ToArray())
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.GetString() == ProtocolEventTypes.TerminalOutput
                && p.TryGetProperty("seq", out var seq))
            {
                seqs.Add(seq.GetInt64());
            }
        }

        return seqs;
    }

    private CapturingConnection _sink = new();

    [Fact]
    public async Task Commit_worker_survives_an_unexpected_fault()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await ExecAsync(
            """
            INSERT OR IGNORE INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete)
            VALUES ('rs_fault', 'j', 'ready', 'local', 0,
              '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z', 1);
            """);
        var store = new FloorRaiseGate(new SqliteJournalManifestStore(_paths));
        await using var journal = new FileRuntimeEventJournal(
            _paths, store, "rs_fault", maxJournalBytes: 32 * 1024 * 1024);
        Assert.True((await journal.RecoverAsync()).IsOk);

        store.ThrowNextFlush = true;
        var first = journal.ReserveReliable(
            EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle, "{\"n\":1}", DateTimeOffset.UtcNow);
        Assert.NotNull(first);
        var failed = await journal.CommitReservedAsync(first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(failed.IsOk);

        var second = journal.ReserveReliable(
            EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle, "{\"n\":2}", DateTimeOffset.UtcNow);
        Assert.NotNull(second);
        var committed = await journal.CommitReservedAsync(second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(committed.IsOk, committed.IsOk ? null : committed.Error.Message);
        Assert.True(second.Seq > first.Seq);
        Assert.True(journal.GetHealth().FloorSeq > first.Seq);
    }

    [Fact]
    public async Task Replay_includes_every_record_published_before_it_started()
    {
        await using var journal = await OpenAsync("rs_pending");
        // Records are published at reserve time and written later by the commit
        // worker. A replay that starts after the publish must return all of them.
        for (var round = 0; round < 5; round++)
        {
            var from = journal.GetHealth().NextSeq - 1;
            var reserved = new List<long>();
            for (var i = 0; i < 40; i++)
            {
                var r = journal.ReserveReliable(
                    EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle,
                    "{\"n\":" + i + "}", DateTimeOffset.UtcNow);
                Assert.NotNull(r);
                reserved.Add(r.Seq);
            }

            var range = await journal.ReadRangeAsync(from, classes: null, budget: 100)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(range.IsOk, range.IsOk ? null : range.Error.Message);
            Assert.Equal(reserved, range.Value.Select(r => r.Seq).ToList());
        }
    }

    [Fact]
    public async Task A_restart_after_a_crash_never_reuses_a_published_sequence()
    {
        var sync = new HoldSync();
        var crashed = await OpenAsync("rs_lease", durableSync: sync);
        // Published live, but the process dies before the write reaches disk.
        var published = crashed.ReserveReliable(
            EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle, "{\"n\":1}", DateTimeOffset.UtcNow);
        Assert.NotNull(published);
        var entered = await Task.WhenAny(sync.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(sync.Entered.Task, entered);

        var store = new SqliteJournalManifestStore(_paths);
        var restarted = new FileRuntimeEventJournal(_paths, store, "rs_lease");
        Assert.True((await restarted.RecoverAsync()).IsOk);
        try
        {
            var next = restarted.ReserveReliable(
                EventClass.Lifecycle, ProtocolEventTypes.PaneLifecycle, "{\"n\":2}", DateTimeOffset.UtcNow);
            Assert.NotNull(next);
            Assert.True(next.Seq > published.Seq, $"reused {next.Seq} <= {published.Seq}");
            // The lost event may have been seen live, so a resume from before it
            // must expire rather than skip it.
            var resume = await restarted.ReadRangeAsync(published.Seq - 1, classes: null, budget: 10);
            Assert.False(resume.IsOk);
            Assert.True(restarted.GetHealth().FloorSeq > published.Seq);
        }
        finally
        {
            sync.Release.TrySetResult();
            await restarted.DisposeAsync();
            await crashed.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_clean_restart_keeps_sequences_contiguous()
    {
        var journal = await OpenAsync("rs_clean");
        var first = await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable, ProtocolEventTypes.PaneLifecycle, "{\"n\":1}");
        Assert.True(first.IsOk, first.IsOk ? null : first.Error.Message);
        await journal.DisposeAsync();

        await using var reopened = await OpenAsync("rs_clean", recover: true);
        Assert.Equal(first.Value.Seq + 1, reopened.NextSeq);
    }

    [Fact]
    public async Task A_subscribe_without_replay_below_the_floor_does_not_expire()
    {
        await WriteClosedSegmentsAsync("rs_edge", count: 20);
        await using var journal = await OpenAsync(
            "rs_edge", maxJournalBytes: 700, rotateBytes: 256, recover: true);
        Assert.True(journal.GetHealth().FloorSeq > 1);
        var cp = new ControlPlaneService(
            new AppState(SessionId.New("rs_edge")),
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            subscriptions: new EventSubscriptionHub());

        // No cursor: what is retained, then live.
        await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject { ["live"] = true }),
            new CapturingConnection("c_edge_1"),
            CancellationToken.None);
        // An old cursor with no replay asks for nothing that was lost.
        await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject { ["from_seq"] = 0, ["replay_budget"] = 0, ["live"] = true }),
            new CapturingConnection("c_edge_2"),
            CancellationToken.None);
        await cp.ShutdownAsync(CancellationToken.None);
    }

    private static async Task WaitCommitsAsync(FileRuntimeEventJournal journal)
    {
        var appended = await journal.AppendAsync(
            EventClass.Lifecycle,
            EventReliability.Reliable,
            ProtocolEventTypes.SessionLifecycle,
            "{\"state\":\"ready\"}");
        Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);
    }

    private async Task<(ControlPlaneService Cp, string PaneId, FileRuntimeEventJournal Journal, TestPaneFactories.ScriptedPaneRuntime Runtime)>
        StartPlaneAsync(long budget, long fromSeq = 0)
    {
        var factory = TestPaneFactories.Scripted();
        var state = new AppState(SessionId.New("rs_out"));
        state.UpdateSession(s => s with { Name = "rs_out", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(_paths);
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var migrated = await migrator.MigrateAsync();
        Assert.True(migrated.IsOk, migrated.IsOk ? null : migrated.Error.Message);
        var saved = await store.SaveAsync(state.Snapshot());
        Assert.True(saved.IsOk, saved.IsOk ? null : saved.Error.Message);
        var journal = new FileRuntimeEventJournal(
            _paths,
            new SqliteJournalManifestStore(_paths),
            state.SessionId.Value,
            maxJournalBytes: budget);
        var recovered = await journal.RecoverAsync();
        Assert.True(recovered.IsOk, recovered.IsOk ? null : recovered.Error.Message);
        var hub = new EventSubscriptionHub();
        _sink = new CapturingConnection();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_out",
            ConnectionId = _sink.ConnectionId,
            FromSeq = fromSeq,
            Classes = new HashSet<EventClass>(),
            ReplayBudget = 0,
            Live = true,
            Sink = _sink,
        });
        sub.EnableLive();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub,
            redactor: new DefaultEventPayloadRedactor());
        var created = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["command"] = "/bin/echo",
                ["cwd"] = _dir,
            }),
            CancellationToken.None);
        var paneId = created.GetProperty("pane_id").GetString()!;
        sub.AttachPane(paneId, attachmentId: "att_out", mode: "observe");
        var runtime = factory.Created.Single(r => r.Id.Value == paneId);
        return (cp, paneId, journal, runtime);
    }

    private async Task WriteClosedSegmentsAsync(string sessionId, int count)
    {
        var journal = await OpenAsync(sessionId, maxJournalBytes: 32 * 1024 * 1024, rotateBytes: 280);
        var payload = "{\"n\":\"" + new string('c', 40) + "\"}";
        for (var i = 0; i < count; i++)
        {
            Assert.True((await journal.AppendAsync(
                EventClass.Lifecycle, EventReliability.Reliable,
                ProtocolEventTypes.PaneLifecycle, payload)).IsOk);
        }

        await journal.CloseOpenSegmentAsync();
        await journal.DisposeAsync();
    }

    private async Task<FileRuntimeEventJournal> OpenAsync(
        string sessionId,
        long? maxJournalBytes = null,
        long? rotateBytes = null,
        int? rotateRecords = null,
        bool recover = false,
        bool retention = false,
        IProcessLogSink? processLog = null,
        TimeProvider? time = null,
        IDurableFileSync? durableSync = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await ExecAsync(
            $"""
            INSERT OR IGNORE INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete)
            VALUES ('{sessionId}', 'j', 'ready', 'local', 0,
              '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z', 1);
            """);
        var journal = new FileRuntimeEventJournal(
            _paths,
            new SqliteJournalManifestStore(_paths),
            sessionId,
            rotateBytes: rotateBytes,
            rotateRecords: rotateRecords,
            maxJournalBytes: maxJournalBytes,
            retention: retention ? new SqliteJournalRetentionQuery(_paths) : null,
            processLog: processLog,
            time: time,
            durableSync: durableSync);
        var recovered = await journal.RecoverAsync();
        Assert.True(recovered.IsOk, recovered.IsOk ? null : recovered.Error.Message);
        return journal;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private long JournalBytes()
    {
        if (!Directory.Exists(_paths.JournalDirectory))
            return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.JournalDirectory, "*.hyjr"))
            total += new FileInfo(file).Length;
        return total;
    }

    private async Task WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            if (ready())
                return;
            await Task.Delay(20);
        }

        Assert.Fail("timed out waiting for live events; lines=" + _sink.Lines.Count);
    }

    private static List<string> Types(CapturingConnection sink)
    {
        var types = new List<string>();
        foreach (var line in sink.Lines.ToArray())
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.GetString() is { } name)
            {
                types.Add(name);
            }
        }

        return types;
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement;

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-28T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingConnection(string id = "c_live") : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = id;
        public List<string> Lines { get; } = [];

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }

    private sealed class SnapshotCommandPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class ThrowNextSync : IDurableFileSync
    {
        private readonly DurableFileSync _inner = DurableFileSync.Production;

        public bool ThrowNext { get; set; }

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new IOException("sync failed");
            }

            _inner.SyncFileThenDirectory(fileHandle, filePath);
        }
    }

    private sealed class HoldSync : IDurableFileSync
    {
        private readonly DurableFileSync _inner = DurableFileSync.Production;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTime BlockedAt { get; private set; }

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            BlockedAt = DateTime.UtcNow;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            _inner.SyncFileThenDirectory(fileHandle, filePath);
        }
    }

    private sealed class FloorRaiseGate(SqliteJournalManifestStore inner) : IJournalManifestStore
    {
        public bool FailRaise { get; set; }

        public bool ThrowNextFlush { get; set; }

        private void ThrowIfArmed()
        {
            if (!ThrowNextFlush)
                return;
            ThrowNextFlush = false;
            throw new InvalidOperationException("manifest store fault");
        }

        public Task<RuntimeResult<RuntimeUnit>> UpsertSegmentAsync(
            JournalSegmentManifest segment, CancellationToken ct = default) =>
            inner.UpsertSegmentAsync(segment, ct);

        public Task<RuntimeResult<IReadOnlyList<JournalSegmentManifest>>> ListSegmentsAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ListSegmentsAsync(sessionId, ct);

        public Task<RuntimeResult<JournalSegmentManifest?>> GetOpenSegmentAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.GetOpenSegmentAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseSegmentAsync(
            string segmentId,
            long? lastSeq,
            int recordCount,
            long byteCount,
            string checksumSha256,
            DateTimeOffset closedAt,
            CancellationToken ct = default) =>
            inner.CloseSegmentAsync(
                segmentId, lastSeq, recordCount, byteCount, checksumSha256, closedAt, ct);

        public Task<RuntimeResult<long>> ReadEventCursorAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ReadEventCursorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> WriteEventCursorAsync(
            string sessionId, long nextSeq, CancellationToken ct = default) =>
            inner.WriteEventCursorAsync(sessionId, nextSeq, ct);

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            CancellationToken ct = default)
        {
            ThrowIfArmed();
            return inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> FlushSegmentProgressAsync(
            JournalSegmentManifest segment,
            string sessionId,
            long nextSeq,
            long floorSeq,
            CancellationToken ct = default)
        {
            ThrowIfArmed();
            return inner.FlushSegmentProgressAsync(segment, sessionId, nextSeq, floorSeq, ct);
        }

        public Task<RuntimeResult<long>> ReadRetentionFloorAsync(
            string sessionId, CancellationToken ct = default) =>
            inner.ReadRetentionFloorAsync(sessionId, ct);

        public Task<RuntimeResult<RuntimeUnit>> RaiseRetentionFloorAsync(
            string sessionId, long floorSeq, long nextSeq, CancellationToken ct = default)
        {
            if (FailRaise)
            {
                return Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("floor write failed")));
            }

            return inner.RaiseRetentionFloorAsync(sessionId, floorSeq, nextSeq, ct);
        }

        public Task<RuntimeResult<RuntimeUnit>> DeleteClosedSegmentAsync(
            string segmentId, string sessionId, long floorSeq, CancellationToken ct = default) =>
            inner.DeleteClosedSegmentAsync(segmentId, sessionId, floorSeq, ct);
    }
}
