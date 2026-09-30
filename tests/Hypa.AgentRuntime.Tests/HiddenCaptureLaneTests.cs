using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class HiddenCaptureLaneTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public HiddenCaptureLaneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-hidden-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Visible_set_uses_owned_subscription_not_attach_client_id()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, _) = await StartAsync(factory);
        try
        {
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                JsonDocument.Parse(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["live"] = true,
                }.ToJsonString()).RootElement,
                sink,
                CancellationToken.None);
            var subscriptionId = sub.GetProperty("subscription_id").GetString()!;
            var attachClientId = sub.GetProperty("attach_client_id").GetString()!;
            Assert.Equal("c_hidden", attachClientId);
            Assert.NotEqual(subscriptionId, attachClientId);

            var ok = await cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                JsonDocument.Parse(new JsonObject
                {
                    ["subscription_id"] = subscriptionId,
                    ["pane_ids"] = new JsonArray(paneId),
                }.ToJsonString()).RootElement,
                sink,
                CancellationToken.None);
            Assert.True(ok.GetProperty("ok").GetBoolean());

            await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.TerminalVisibleSet,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["subscription_id"] = attachClientId,
                        ["pane_ids"] = new JsonArray(paneId),
                    }.ToJsonString()).RootElement,
                    sink,
                    CancellationToken.None));
            _ = subId;
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hide_reveal_without_hidden_output_requires_full()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, runtime) = await StartAsync(factory);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            runtime.Emit("visible\n");
            await WaitUntil(() => runtime.AttachCaptureCount > 0);
            Assert.Equal(
                CaptureAdmissionKind.Capture,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);

            await PublishVisibleAsync(cp, sink, subId, []);
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            Assert.Equal(
                CaptureAdmissionKind.CaptureFull,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_capture_leaves_pending_full()
    {
        var factory = new CapturePaneFactory { CaptureSucceeds = false };
        var (cp, sink, subId, paneId, runtime) = await StartAsync(factory);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            runtime.Emit("fail-pack\n");
            await WaitUntil(() => runtime.AttachCaptureCount > 0);
            Assert.Equal(
                CaptureAdmissionKind.CaptureFull,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_queue_pack_leaves_pending_full()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, runtime) = await StartAsync(factory);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            await PublishVisibleAsync(cp, sink, subId, []);
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            Assert.Equal(
                CaptureAdmissionKind.CaptureFull,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
            var before = runtime.AttachCaptureCount;
            var linesBefore = sink.Snapshot().Count;
            cp.FailNextSnapshotPackOnce();
            runtime.Emit("queue-fail\n");
            await WaitUntil(() => runtime.AttachCaptureCount > before);
            // A failed pack keeps the pending full. A retry can post that full
            // frame at once, which then clears the pending state.
            if (cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind
                is not CaptureAdmissionKind.CaptureFull)
            {
                // The retry posts on its own thread. Wait for the frame.
                bool PostedFull(string line) =>
                    line.Contains(paneId, StringComparison.Ordinal)
                    && (line.Contains("\"full\":true", StringComparison.Ordinal)
                        || line.Contains("\"kind\":\"snapshot\"", StringComparison.Ordinal));
                await WaitUntil(() => sink.Snapshot().Skip(linesBefore).Any(PostedFull));
                Assert.Contains(sink.Snapshot().Skip(linesBefore), PostedFull);
            }
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Skip_preserves_vt_feed_and_journal()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, runtime, journal) = await StartWithJournalAsync(factory);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, []);
            var before = runtime.AttachCaptureCount;
            runtime.Emit("hidden-bytes\n");
            await Task.Delay(40);
            var range = await journal.ReadRangeAsync(0, classes: null, budget: 80, CancellationToken.None);
            Assert.True(range.IsOk);
            Assert.DoesNotContain(range.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);
            Assert.Contains("hidden-bytes", runtime.FedText, StringComparison.Ordinal);
            Assert.Equal(before, runtime.AttachCaptureCount);
            Assert.Equal(0, runtime.LiveCaptureCount);
            Assert.False(cp.TryPeekCoalescerPendingForTest(paneId, out _));
            Assert.Equal(
                CaptureAdmissionKind.Skip,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await journal.DisposeAsync();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Attach_baseline_captures_while_hidden()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, runtime) = await StartAsync(factory, observe: false);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, []);
            Assert.Equal(0, runtime.AttachCaptureCount);
            await ObserveAsync(cp, sink, subId, paneId);
            Assert.True(runtime.AttachCaptureCount > 0);
            Assert.Equal(
                CaptureAdmissionKind.Skip,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Parked_scroll_metrics_grow_at_origin_without_full_capture()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, runtime) = await StartAsync(
            factory,
            types: ["output", "render", ProtocolEventTypes.PaneScrollChanged]);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, []);
            var before = runtime.AttachCaptureCount;
            runtime.ScrollMax = 4;
            runtime.Emit("line-1\n");
            await WaitScrollMaxAsync(sink, paneId, 4);
            runtime.ScrollMax = 9;
            runtime.Emit("line-2\n");
            await WaitScrollMaxAsync(sink, paneId, 9);
            Assert.Equal(before, runtime.AttachCaptureCount);
            Assert.Equal(0, runtime.LiveCaptureCount);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disconnect_removes_client_from_union()
    {
        var factory = new CapturePaneFactory();
        var (cp, sink, subId, paneId, _) = await StartAsync(factory);
        try
        {
            await PublishVisibleAsync(cp, sink, subId, [paneId]);
            Assert.True(cp.VisibleSets.MayCapture(paneId, DateTimeOffset.UtcNow));
            cp.OnClientDisconnected(sink);
            Assert.False(cp.VisibleSets.MayCapture(paneId, DateTimeOffset.UtcNow));
            Assert.Equal(
                CaptureAdmissionKind.Skip,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Zero_live_clients_skip_before_subscribe()
    {
        var factory = new CapturePaneFactory();
        var (cp, paneId) = await StartUnsubscribedAsync(factory);
        try
        {
            Assert.False(cp.VisibleSets.MayCapture(paneId, DateTimeOffset.UtcNow));
            Assert.Equal(
                CaptureAdmissionKind.Skip,
                cp.VisibleSets.AdmitCapture(paneId, DateTimeOffset.UtcNow).Kind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Cp, CapturingConnection Sink, string SubId, string PaneId, CapturePaneRuntime Runtime)>
        StartAsync(
            CapturePaneFactory factory,
            bool observe = true,
            IReadOnlyList<string>? types = null)
    {
        var (cp, sink, subId, paneId, runtime, _) = await StartCoreAsync(factory, observe, types, journal: false);
        return (cp, sink, subId, paneId, runtime);
    }

    private async Task<(ControlPlaneService Cp, CapturingConnection Sink, string SubId, string PaneId, CapturePaneRuntime Runtime, FileRuntimeEventJournal Journal)>
        StartWithJournalAsync(CapturePaneFactory factory)
    {
        var (cp, sink, subId, paneId, runtime, journal) = await StartCoreAsync(
            factory, observe: true, types: ["output", "render"], journal: true);
        return (cp, sink, subId, paneId, runtime, journal!);
    }

    private async Task<(ControlPlaneService Cp, string PaneId)> StartUnsubscribedAsync(CapturePaneFactory factory)
    {
        var (cp, _, _, paneId, _, _) = await StartCoreAsync(
            factory, observe: false, types: ["output"], journal: false, subscribe: false);
        return (cp, paneId);
    }

    private async Task<(
        ControlPlaneService Cp,
        CapturingConnection Sink,
        string SubId,
        string PaneId,
        CapturePaneRuntime Runtime,
        FileRuntimeEventJournal? Journal)> StartCoreAsync(
        CapturePaneFactory factory,
        bool observe,
        IReadOnlyList<string>? types,
        bool journal,
        bool subscribe = true)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("hidden-cap"));
        state.UpdateSession(s => s with { Name = "hidden-cap", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        FileRuntimeEventJournal? fileJournal = null;
        if (journal || subscribe)
        {
            fileJournal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
            Assert.True((await fileJournal.RecoverAsync()).IsOk);
        }

        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: fileJournal,
            subscriptions: hub);
        var sink = new CapturingConnection("c_hidden");
        var subId = "";
        if (subscribe)
        {
            var arr = new JsonArray();
            foreach (var type in types ?? ["output", "render"])
                arr.Add(type);
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                JsonDocument.Parse(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = arr,
                    ["live"] = true,
                }.ToJsonString()).RootElement,
                sink,
                CancellationToken.None);
            subId = sub.GetProperty("subscription_id").GetString()!;
            await cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
        }

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse(new JsonObject
            {
                ["command"] = "echo",
                ["cwd"] = _dir,
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;
        var runtime = factory.Created.Single(r => r.Id.Value == paneId);
        if (observe && subscribe)
            await ObserveAsync(cp, sink, subId, paneId);
        return (cp, sink, subId, paneId, runtime, fileJournal);
    }

    private static async Task PublishVisibleAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string subId,
        IReadOnlyList<string> paneIds)
    {
        var ids = new JsonArray();
        foreach (var id in paneIds)
            ids.Add(id);
        await cp.DispatchAsync(
            ProtocolMethods.TerminalVisibleSet,
            JsonDocument.Parse(new JsonObject
            {
                ["subscription_id"] = subId,
                ["pane_ids"] = ids,
            }.ToJsonString()).RootElement,
            connection,
            CancellationToken.None);
    }

    private static Task<JsonElement> ObserveAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string subId,
        string paneId) =>
        cp.DispatchAsync(
            ProtocolMethods.TerminalObserve,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = paneId,
                ["subscription_id"] = subId,
            }.ToJsonString()).RootElement,
            connection,
            CancellationToken.None);

    private static async Task WaitUntil(Func<bool> ready)
    {
        for (var i = 0; i < 80; i++)
        {
            if (ready())
                return;
            await Task.Delay(25);
        }

        Assert.Fail("timed out waiting for capture path");
    }

    private static async Task<string> WaitJournalOutputAsync(FileRuntimeEventJournal journal, string text)
    {
        for (var i = 0; i < 80; i++)
        {
            var range = await journal.ReadRangeAsync(0, classes: null, budget: 80, CancellationToken.None);
            Assert.True(range.IsOk);
            var sb = new StringBuilder();
            foreach (var rec in range.Value.Where(r => r.Type == ProtocolEventTypes.TerminalOutput))
            {
                using var doc = JsonDocument.Parse(rec.PayloadJson);
                var dataB64 = doc.RootElement.GetProperty("data").GetString()!;
                sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(dataB64)));
            }

            if (sb.ToString().Contains(text, StringComparison.Ordinal))
                return sb.ToString();
            await Task.Delay(25);
        }

        Assert.Fail("journal never stored output");
        return "";
    }

    private static async Task WaitScrollMaxAsync(CapturingConnection sink, string paneId, int maxOffset)
    {
        for (var i = 0; i < 80; i++)
        {
            foreach (var line in sink.Snapshot())
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("params", out var p))
                    continue;
                if (p.GetProperty("type").GetString() != ProtocolEventTypes.PaneScrollChanged)
                    continue;
                var payload = p.GetProperty("payload");
                if (payload.ValueKind == JsonValueKind.String)
                    payload = JsonDocument.Parse(payload.GetString()!).RootElement.Clone();
                if (payload.GetProperty("pane_id").GetString() != paneId)
                    continue;
                if (payload.GetProperty("offset").GetInt32() == 0
                    && payload.GetProperty("max_offset").GetInt32() == maxOffset)
                    return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("did not observe parked scroll max " + maxOffset);
    }

    private sealed class CapturingConnection(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
                return _lines.ToArray();
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                _lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturePaneFactory : IPaneRuntimeFactory
    {
        public List<CapturePaneRuntime> Created { get; } = [];
        public bool CaptureSucceeds { get; init; } = true;

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var runtime = new CapturePaneRuntime(options.Id, CaptureSucceeds);
            Created.Add(runtime);
            return runtime;
        }
    }

    private sealed class CapturePaneRuntime(PaneId id, bool captureSucceeds) : IPaneRuntime, IPaneVtSnapshot
    {
        private readonly object _gate = new();
        private readonly StringBuilder _fed = new();
        private int _attachCapture;
        private int _liveCapture;
        private long _feed;

        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_300;
        public int ScrollOffset { get; set; }
        public int ScrollMax { get; set; }
        public long FeedGeneration => Volatile.Read(ref _feed);
        public PaneFeedPaintDecision LastFeedPaintDecision => new(FeedGeneration, true);
        public int AttachCaptureCount => Volatile.Read(ref _attachCapture);
        public int LiveCaptureCount => Volatile.Read(ref _liveCapture);
        public string FedText
        {
            get
            {
                lock (_gate)
                    return _fed.ToString();
            }
        }

        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
#pragma warning disable CS0067
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public void Emit(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (_gate)
                _fed.Append(text);
            Interlocked.Increment(ref _feed);
            OutputReceived?.Invoke(this, bytes);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => FedText;
        public string ReadRecentText(int maxLines) => FedText;
        public string ReadRecentUnwrappedText(int maxLines) => FedText;
        public string ReadDetectionText() => FedText;

        public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
        {
            Interlocked.Increment(ref _liveCapture);
            feedGeneration = FeedGeneration;
            json = """{"schemaVersion":1,"provider":"ghostty","cols":2,"rows":1,"cursor":{"col":0,"row":0,"visible":true},"activeScreen":"main","cells":[[{"text":"x","width":1},{"text":" ","width":1}]]}""";
            return captureSucceeds;
        }

        public bool TryCaptureAttachSnapshot(out VtAttachSnapshot? snapshot, out long feedGeneration)
        {
            Interlocked.Increment(ref _attachCapture);
            feedGeneration = FeedGeneration;
            if (!captureSucceeds)
            {
                snapshot = null;
                return false;
            }

            snapshot = new VtAttachSnapshot(
                new VtFrame(
                    Id.Value,
                    2,
                    1,
                    new[] { new[] { VtCellView.Blank, VtCellView.Blank } },
                    new VtFrameCursor(0, 0, true, 0),
                    new VtFrameModes(false, true, false, false, "", false),
                    0,
                    0,
                    1),
                new VtScrollRegion(0, 0),
                1);
            return true;
        }

        public bool TryGetScrollMetrics(out int offset, out int maxOffset)
        {
            offset = ScrollOffset;
            maxOffset = ScrollMax;
            return true;
        }

        public bool TryGetScrollOrigin(out int offset)
        {
            offset = ScrollOffset;
            return true;
        }

        public bool TrySetScrollOrigin(int offset)
        {
            ScrollOffset = offset;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
