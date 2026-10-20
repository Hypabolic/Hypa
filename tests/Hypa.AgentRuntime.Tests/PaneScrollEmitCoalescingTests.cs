using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// <c>pane.scroll_changed</c> is journaled at most once per pane per
/// <see cref="ControlPlaneService.ScrollEmitInterval"/>. The first change after a quiet
/// interval goes out at once. The last change of a burst goes out at the end of the
/// interval, so live and replay subscribers end on the latest offset.
/// </summary>
public sealed class PaneScrollEmitCoalescingTests : IDisposable
{
    private const int MaxOffset = 500;
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public PaneScrollEmitCoalescingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-scroll-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task A_burst_of_scroll_changes_journals_few_records_and_the_last_has_the_last_offset()
    {
        const int burst = 200;
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        var beforeBurst = fixture.Journal.NextSeq - 1;

        // The clock stands still: the whole burst falls in one interval.
        for (var offset = 1; offset <= burst; offset++)
            await fixture.ScrollAsync(offset);

        await fixture.EndBurstAsync(clock);
        var stored = await fixture.WaitJournalScrollAsync(beforeBurst, burst);

        Assert.InRange(stored.Count, 1, 3);
        Assert.Equal(1, stored[0].Offset);
        Assert.Equal((burst, MaxOffset), stored[^1]);
        Assert.Equal((burst, MaxOffset), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);
    }

    [Fact]
    public async Task A_subscriber_that_resumes_from_before_a_burst_ends_on_the_latest_offset()
    {
        const int burst = 120;
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        var beforeBurst = fixture.Journal.NextSeq - 1;

        for (var offset = 1; offset <= burst; offset++)
            await fixture.ScrollAsync(offset);
        await fixture.EndBurstAsync(clock);
        _ = await fixture.WaitJournalScrollAsync(beforeBurst, burst);

        var resumed = new Sink("c_resumed");
        await fixture.SubscribeAsync(resumed, fromSeq: beforeBurst, replay: true);

        var replayed = resumed.ScrollPayloads(fixture.PaneId);
        Assert.NotEmpty(replayed);
        Assert.True(replayed.Count < burst, $"replay carried {replayed.Count} scroll records for {burst} changes");
        Assert.Equal((burst, MaxOffset), replayed[^1]);
    }

    [Fact]
    public async Task One_scroll_change_after_a_quiet_interval_is_emitted_at_once()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);

        // The clock does not move: the leading edge emits without a timer.
        await fixture.ScrollAsync(7);
        await fixture.Sink.WaitForScrollAsync(fixture.PaneId, 7);
        Assert.True(fixture.Cp.ScrollEmitsAreIdleForTest);

        // After a quiet interval the next isolated change also goes out at once.
        clock.Advance(ControlPlaneService.ScrollEmitInterval);
        await fixture.ScrollAsync(9);
        await fixture.Sink.WaitForScrollAsync(fixture.PaneId, 9);
        Assert.True(fixture.Cp.ScrollEmitsAreIdleForTest);
        Assert.Equal(
            new List<(int Offset, int MaxOffset)> { (7, MaxOffset), (9, MaxOffset) },
            fixture.Sink.ScrollPayloads(fixture.PaneId));
    }

    [Fact]
    public async Task A_following_pane_sends_no_scroll_change_when_only_max_offset_moves_and_the_thumb_stays()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        Assert.True(fixture.Rows > 0);
        // Past this max the thumb has one row on each track up to the bound.
        const int big = 50_000;
        foreach (var track in new[] { 1, fixture.Rows, PaneScrollStateBookTrackBound })
            Assert.Equal(1, ScrollbarThumbMath.Compute(0, big - 200, track, track)!.Value.Length);

        fixture.Runtime.Max = big;
        fixture.Paint();
        await fixture.Sink.WaitForScrollAsync(fixture.PaneId, 0);
        await fixture.EndBurstAsync(clock);
        var sent = fixture.Sink.ScrollPayloads(fixture.PaneId).Count;
        var mark = fixture.Journal.NextSeq - 1;

        // The output grows the history, and the history drain makes it smaller
        // again. The view follows the output at offset 0.
        for (var frame = 1; frame <= 200; frame++)
        {
            fixture.Runtime.Max = big + (frame % 40 < 30 ? frame : -frame);
            fixture.Paint();
            if (frame % 20 == 0)
                clock.Advance(ControlPlaneService.ScrollEmitInterval);
        }

        await fixture.EndBurstAsync(clock);
        Assert.Equal(sent, fixture.Sink.ScrollPayloads(fixture.PaneId).Count);
        Assert.Empty(await fixture.JournalScrollAsync(mark));

        // A change that moves the thumb goes out.
        fixture.Runtime.Max = fixture.Rows;
        fixture.Paint();
        await fixture.EndBurstAsync(clock);
        Assert.Equal((0, fixture.Rows), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);

        // So does a change that hides the scrollbar.
        clock.Advance(ControlPlaneService.ScrollEmitInterval);
        fixture.Runtime.Max = 0;
        fixture.Paint();
        await fixture.EndBurstAsync(clock);
        Assert.Equal((0, 0), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);
    }

    [Fact]
    public async Task A_thumb_change_on_a_track_taller_than_the_pane_rows_goes_out()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);

        // Another attach sets the pane size. This attach shows the pane in a
        // box of more rows, so its track is taller than the pane rows.
        var rows = fixture.Rows;
        var (from, to, track) = ThumbChangeOnlyOnATallerTrack(rows);
        // Each track of up to the pane rows shows the same thumb.
        Assert.True(PaneScrollStateBook.SameThumbOnEachTrack(from, to, rows));
        Assert.NotEqual(
            ScrollbarThumbMath.Compute(0, from, track, track),
            ScrollbarThumbMath.Compute(0, to, track, track));

        fixture.Runtime.Max = from;
        fixture.Paint();
        await fixture.EndBurstAsync(clock);
        Assert.Equal((0, from), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);

        clock.Advance(ControlPlaneService.ScrollEmitInterval);
        fixture.Runtime.Max = to;
        fixture.Paint();
        await fixture.EndBurstAsync(clock);
        Assert.Equal((0, to), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);
    }

    [Fact]
    public async Task A_late_origin_read_does_not_replace_a_newer_wheel_step()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        await fixture.ScrollAsync(10);

        // New output moved the scrolled view up by 2 rows. The PTY output
        // callback reads that origin. Before it stores it, a wheel step of 3
        // rows comes in from the attach.
        fixture.Runtime.Offset = 12;
        Task<JsonElement>? wheel = null;
        fixture.Runtime.OnOriginRead = () =>
        {
            fixture.Runtime.OnOriginRead = null;
            wheel = Task.Run(() => fixture.ScrollAsync(12, delta: 3));
            // With the read outside the lock of the pane, the step ends here.
            // With the read inside it, the step waits for the callback.
            wheel.Wait(TimeSpan.FromMilliseconds(300));
        };
        fixture.Cp.MaybeEmitScrollChanged(fixture.Runtime, originOnly: true);
        var stepped = await wheel!;
        Assert.Equal(15, stepped.GetProperty("offset").GetInt32());
        await fixture.EndBurstAsync(clock);

        // The newest state that the mux sent is the wheel step.
        using var last = JsonDocument.Parse(fixture.Sink.RawPayloads(fixture.PaneId)[^1]);
        Assert.Equal(15, last.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(stepped.GetProperty("revision").GetInt64(), last.RootElement.GetProperty("revision").GetInt64());
    }

    /// <summary>
    /// Two max offsets that give the same thumb on a track of
    /// <paramref name="rows"/> rows but a different thumb on a taller track.
    /// </summary>
    private static (int From, int To, int Track) ThumbChangeOnlyOnATallerTrack(int rows)
    {
        for (var track = rows + 1; track <= PaneScrollStateBookTrackBound; track++)
        {
            for (var from = 1; from < 20_000; from += 7)
            {
                var to = from + (from / 2) + 1;
                if (PaneScrollStateBook.SameThumbOnEachTrack(from, to, rows)
                    && ScrollbarThumbMath.Compute(0, from, track, track) != ScrollbarThumbMath.Compute(0, to, track, track))
                {
                    return (from, to, track);
                }
            }
        }

        throw new InvalidOperationException("no geometry where only a taller track moves the thumb");
    }

    private const int PaneScrollStateBookTrackBound = PaneScrollStateBook.MaxTrackRows;

    [Fact]
    public async Task A_subscriber_that_resumes_after_a_scroll_back_to_the_bottom_ends_on_offset_zero()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        const int big = 50_000;
        fixture.Runtime.Max = big;
        var beforeScroll = fixture.Journal.NextSeq - 1;

        // Up into the history and back to the bottom. Each offset change goes out.
        for (var offset = 3; offset <= 12; offset += 3)
        {
            await fixture.ScrollAsync(offset);
            clock.Advance(ControlPlaneService.ScrollEmitInterval);
        }

        await fixture.ScrollAsync(0);
        await fixture.EndBurstAsync(clock);
        Assert.Equal(
            [3, 6, 9, 12, 0],
            fixture.Sink.ScrollPayloads(fixture.PaneId).Select(s => s.Offset).ToList());

        // Then the pane follows its output. Only max_offset changes.
        for (var frame = 1; frame <= 50; frame++)
        {
            fixture.Runtime.Max = big + frame;
            fixture.Paint();
        }

        await fixture.EndBurstAsync(clock);
        Assert.Equal((0, big), fixture.Sink.ScrollPayloads(fixture.PaneId)[^1]);

        var resumed = new Sink("c_resumed");
        await fixture.SubscribeAsync(resumed, fromSeq: beforeScroll, replay: true);
        var replayed = resumed.ScrollPayloads(fixture.PaneId);
        Assert.Equal(5, replayed.Count);
        Assert.Equal(0, replayed[^1].Offset);
    }

    [Fact]
    public async Task Replies_and_events_carry_a_rising_revision_of_one_epoch()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);

        var first = await fixture.ScrollAsync(4);
        await fixture.Sink.WaitForScrollAsync(fixture.PaneId, 4);
        clock.Advance(ControlPlaneService.ScrollEmitInterval);
        var second = await fixture.ScrollAsync(8);
        await fixture.Sink.WaitForScrollAsync(fixture.PaneId, 8);

        var epoch = first.GetProperty("revision_epoch").GetString();
        Assert.False(string.IsNullOrEmpty(epoch));
        Assert.Equal(epoch, second.GetProperty("revision_epoch").GetString());
        Assert.True(second.GetProperty("revision").GetInt64() > first.GetProperty("revision").GetInt64());

        var events = fixture.Sink.RawPayloads(fixture.PaneId);
        Assert.Equal(2, events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            using var doc = JsonDocument.Parse(events[i]);
            var reply = i == 0 ? first : second;
            Assert.Equal(epoch, doc.RootElement.GetProperty("revision_epoch").GetString());
            Assert.Equal(reply.GetProperty("revision").GetInt64(), doc.RootElement.GetProperty("revision").GetInt64());
        }
    }

    [Fact]
    public async Task A_scroll_delta_counts_from_the_current_origin_of_the_mux()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        var reply = await fixture.ScrollAsync(10);
        Assert.Equal(10, reply.GetProperty("offset").GetInt32());

        // New output arrives while the view is scrolled back. The view keeps
        // its history row, so its offset from the bottom goes up.
        fixture.Runtime.Offset = 17;

        // The attach knew offset 10 and asks for 13, with a delta of 3.
        reply = await fixture.ScrollAsync(13, delta: 3);
        Assert.Equal(20, reply.GetProperty("offset").GetInt32());

        reply = await fixture.ScrollAsync(0, delta: -100);
        Assert.Equal(0, reply.GetProperty("offset").GetInt32());

        // A mux reads only the fields that it knows. A mux with no delta
        // field uses the offset.
        reply = await fixture.Cp.DispatchAsync(
            ProtocolMethods.PaneScroll,
            JsonDocument.Parse(new JsonObject { ["pane_id"] = fixture.PaneId, ["offset"] = 6, ["not_a_field"] = 3 }.ToJsonString()).RootElement,
            CancellationToken.None);
        Assert.Equal(6, reply.GetProperty("offset").GetInt32());
    }

    private async Task<Fixture> NewFixtureAsync(ManualTimeProvider clock)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("scroll-emit"));
        state.UpdateSession(s => s with { Name = "scroll-emit", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var journal = new FileRuntimeEventJournal(_paths, new SqliteJournalManifestStore(_paths), state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new ScrollPaneFactory();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            timeProvider: clock);
        var create = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject { ["command"] = "echo", ["cwd"] = _dir }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;
        var rows = state.GetPane(new PaneId(paneId))!.Rows;
        var fixture = new Fixture(cp, journal, new Sink("c_live"), paneId, factory.Last!, rows);
        await fixture.SubscribeAsync(fixture.Sink, fromSeq: journal.NextSeq - 1, replay: false);
        return fixture;
    }

    private sealed class Fixture(
        ControlPlaneService cp,
        FileRuntimeEventJournal journal,
        Sink sink,
        string paneId,
        ScrollPaneRuntime runtime,
        int rows) : IAsyncDisposable
    {
        public ControlPlaneService Cp => cp;

        public FileRuntimeEventJournal Journal => journal;

        public Sink Sink => sink;

        public string PaneId => paneId;

        public ScrollPaneRuntime Runtime => runtime;

        /// <summary>The pane rows: the track of its scrollbar.</summary>
        public int Rows => rows;

        public Task<JsonElement> ScrollAsync(int offset, int? delta = null)
        {
            var parameters = new JsonObject { ["pane_id"] = paneId, ["offset"] = offset };
            if (delta is { } step)
                parameters["delta"] = step;
            return cp.DispatchAsync(
                ProtocolMethods.PaneScroll,
                JsonDocument.Parse(parameters.ToJsonString()).RootElement,
                CancellationToken.None);
        }

        /// <summary>The live paint path: the mux reads the pane metrics after a frame.</summary>
        public void Paint() => cp.MaybeEmitScrollChanged(runtime);

        /// <summary>All scroll records of the pane in the journal after <paramref name="fromSeq"/>.</summary>
        public async Task<List<(int Offset, int MaxOffset)>> JournalScrollAsync(long fromSeq)
        {
            var range = await journal.ReadRangeAsync(fromSeq, classes: null, budget: 4096);
            Assert.True(range.IsOk);
            return range.Value
                .Where(r => r.Type == ProtocolEventTypes.PaneScrollChanged)
                .Select(r => ParseScroll(r.PayloadJson))
                .Where(s => s.PaneId == paneId)
                .Select(s => (s.Offset, s.MaxOffset))
                .ToList();
        }

        /// <summary>Let the interval end, so the trailing emit of the burst goes out.</summary>
        public async Task EndBurstAsync(ManualTimeProvider clock)
        {
            clock.Advance(ControlPlaneService.ScrollEmitInterval);
            await Poll.UntilAsync(() => cp.ScrollEmitsAreIdleForTest, "the scroll emits to end");
            await cp.WaitReliableEmitsQuiescentForTestsAsync();
        }

        /// <summary>The journaled scroll records of the pane after <paramref name="fromSeq"/>, once the last one has <paramref name="offset"/>.</summary>
        public async Task<List<(int Offset, int MaxOffset)>> WaitJournalScrollAsync(long fromSeq, int offset)
        {
            List<(int Offset, int MaxOffset)> stored = [];
            await Poll.UntilAsync(
                () =>
                {
                    var range = journal.ReadRangeAsync(fromSeq, classes: null, budget: 4096).GetAwaiter().GetResult();
                    Assert.True(range.IsOk);
                    stored = range.Value
                        .Where(r => r.Type == ProtocolEventTypes.PaneScrollChanged)
                        .Select(r => ParseScroll(r.PayloadJson))
                        .Where(s => s.PaneId == paneId)
                        .Select(s => (s.Offset, s.MaxOffset))
                        .ToList();
                    return stored.Count > 0 && stored[^1].Offset == offset;
                },
                "the journaled scroll record with offset " + offset);
            return stored;
        }

        public async Task SubscribeAsync(Sink target, long fromSeq, bool replay)
        {
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                JsonDocument.Parse(new JsonObject
                {
                    ["from_seq"] = fromSeq,
                    ["types"] = new JsonArray(ProtocolEventTypes.PaneScrollChanged),
                    ["live"] = true,
                }.ToJsonString()).RootElement,
                target,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            IReadOnlyList<RuntimeEventRecord> records = [];
            if (replay)
            {
                var prepared = await cp.PrepareEventsSubscribeAsync(subId, CancellationToken.None);
                Assert.True(prepared.IsOk);
                records = prepared.Value;
            }

            await cp.CompleteEventsSubscribeAsync(subId, records, target, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await cp.ShutdownAsync(CancellationToken.None);
            await journal.DisposeAsync();
        }
    }

    private static (string? PaneId, int Offset, int MaxOffset) ParseScroll(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        return (
            root.GetProperty("pane_id").GetString(),
            root.GetProperty("offset").GetInt32(),
            root.GetProperty("max_offset").GetInt32());
    }

    private sealed class Sink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                _lines.Add(jsonLine);
            return Task.CompletedTask;
        }

        public List<(int Offset, int MaxOffset)> ScrollPayloads(string paneId) =>
            RawPayloads(paneId)
                .Select(ParseScroll)
                .Select(s => (s.Offset, s.MaxOffset))
                .ToList();

        public List<string> RawPayloads(string paneId)
        {
            string[] lines;
            lock (_gate)
                lines = _lines.ToArray();
            var result = new List<string>();
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("params", out var p)
                    || p.GetProperty("type").GetString() != ProtocolEventTypes.PaneScrollChanged)
                {
                    continue;
                }

                var payload = p.GetProperty("payload");
                var json = payload.ValueKind == JsonValueKind.String ? payload.GetString()! : payload.GetRawText();
                if (ParseScroll(json).PaneId == paneId)
                    result.Add(json);
            }

            return result;
        }

        public Task WaitForScrollAsync(string paneId, int offset) =>
            Poll.UntilAsync(
                () => ScrollPayloads(paneId) is { Count: > 0 } seen && seen[^1].Offset == offset,
                "pane.scroll_changed with offset " + offset);
    }

    private sealed class ScrollPaneFactory : IPaneRuntimeFactory
    {
        public ScrollPaneRuntime? Last { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options) => Last = new ScrollPaneRuntime(options.Id);
    }

    private sealed class ScrollPaneRuntime(PaneId id) : IPaneRuntime, IPaneVtSnapshot
    {
        private int _offset;
        private int _maxOffset = MaxOffset;

        /// <summary>The history rows above the live view. A test moves it as output would.</summary>
        public int Max
        {
            get => Volatile.Read(ref _maxOffset);
            set => Volatile.Write(ref _maxOffset, value);
        }

        /// <summary>The offset from the bottom. A test moves it as output moves a scrolled view.</summary>
        public int Offset
        {
            get => Volatile.Read(ref _offset);
            set => Volatile.Write(ref _offset, value);
        }

        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_301;
        public long FeedGeneration => 0;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => "";
        public string ReadDetectionText() => "";

        public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
        {
            json = "";
            feedGeneration = 0;
            return false;
        }

        public bool TryGetScrollMetrics(out int offset, out int maxOffset)
        {
            offset = Volatile.Read(ref _offset);
            maxOffset = Max;
            return true;
        }

        /// <summary>Runs after the origin read, before the caller uses it.</summary>
        public Action? OnOriginRead { get; set; }

        public bool TryGetScrollOrigin(out int offset)
        {
            offset = Volatile.Read(ref _offset);
            OnOriginRead?.Invoke();
            return true;
        }

        public bool TrySetScrollOrigin(int offset)
        {
            Volatile.Write(ref _offset, Math.Clamp(offset, 0, Max));
            return true;
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
