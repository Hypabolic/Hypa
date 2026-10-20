using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live <see cref="ControlPlaneService"/> with one <c>/bin/cat</c> pane and
/// one attach connection that has an active surface. The connection keeps
/// its render lines in a real <see cref="ClientWriterQueue"/>. The test runs
/// the writer: it takes each line with the binding recheck of the mux, as
/// the socket writer does. A projection change between the commit of a
/// frame and its write makes the writer cancel the line.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class WriterCancelReanchorLiveTests
{
    private const string ClientId = "cancel-client";

    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task A_canceled_committed_cells_line_makes_the_next_post_a_full_frame_that_the_client_applies()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        using var client = new ClientSide(session.PaneId);
        Assert.All(client.Apply(session.CellsOf(0)), Assert.True);

        // The mux commits this frame as the delta base of the observer and
        // queues its line. A focus change moves the projection before the
        // writer takes the line, so the writer cancels it.
        session.Runtime.PublishFedOutputForTests("LOST-ONE\r\n");
        await session.PaintAsync();
        Assert.Equal(1, session.Sink.QueuedRenderLines);
        Assert.True(session.Cp.AttachSurfaceInterest.ConsumeProjectionFloor(session.Sink.ConnectionId, ClientId).IsOk);
        var mark = session.Sink.Count;
        await session.Sink.DrainAsync(session.Cp);
        Assert.Equal(1, session.Sink.CanceledLines);

        // The next post of the pane is a full frame, also with no new output:
        // the cancel asks for an origin paint. Its timer can run it before
        // this paint.
        await session.PaintAsync();
        await session.Sink.DrainAsync(session.Cp);
        var sent = session.CellsOf(mark);
        Assert.NotEmpty(sent);
        Assert.True(IsFull(sent[0]), sent[0].GetRawText());
        Assert.All(client.Apply(sent), Assert.True);
        Assert.Contains("LOST-ONE", client.HostText(), StringComparison.Ordinal);

        // A later delta chains on that full frame.
        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("AFTER\r\n");
        await session.PaintAsync();
        await session.Sink.DrainAsync(session.Cp);
        var delta = Assert.Single(session.CellsOf(mark));
        Assert.False(IsFull(delta), delta.GetRawText());
        Assert.All(client.Apply([delta]), Assert.True);
        Assert.Contains("AFTER", client.HostText(), StringComparison.Ordinal);
        Assert.Equal(delta.GetProperty("generation").GetInt64(), client.Live.LastAppliedBlit(session.PaneId).Generation);
    }

    [SkippableFact]
    public async Task A_written_cells_line_marks_no_reanchor()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        using var client = new ClientSide(session.PaneId);
        Assert.All(client.Apply(session.CellsOf(0)), Assert.True);

        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("KEPT\r\n");
        await session.PaintAsync();
        await session.Sink.DrainAsync(session.Cp);
        Assert.Equal(0, session.Sink.CanceledLines);

        session.Runtime.PublishFedOutputForTests("NEXT\r\n");
        await session.PaintAsync();
        await session.Sink.DrainAsync(session.Cp);
        var sent = session.CellsOf(mark);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, body => Assert.False(IsFull(body), body.GetRawText()));
        Assert.All(client.Apply(sent), Assert.True);
    }

    private static bool IsFull(JsonElement body) =>
        (body.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True)
        || (body.TryGetProperty("reanchor", out var re) && re.ValueKind == JsonValueKind.True);

    private static AttachEndpointHello Hello() =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = ClientId,
            Geometry = new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
        };

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private async Task<Session> OpenAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-writer-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("writer-cancel"));
        state.UpdateSession(s => s with { Name = "writer-cancel", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var sink = new QueueSink("writer-cancel");
        var session = new Session(dir, paths, cp, sink);
        try
        {
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["command"] = "/bin/cat",
                    ["cwd"] = dir,
                }),
                CancellationToken.None);
            session.PaneId = create.GetProperty("pane_id").GetString()!;
            session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));

            await cp.HandleAttachHelloAsync(Hello(), sink, CancellationToken.None);
            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "on",
                    ClientId = ClientId,
                    Active = true,
                    GeometryRevision = 1,
                },
                sink,
                CancellationToken.None);
            await session.ObserveAsync();
            var deadline = DateTime.UtcNow + Poll.HangGuard;
            await sink.DrainAsync(cp);
            while (session.CellsOf(0).Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "no first frame of the pane");
                await session.PaintAsync();
                await sink.DrainAsync(cp);
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The attach client side for one pane: a chrome-off live state over an in-memory TTY.</summary>
    private sealed class ClientSide : IDisposable
    {
        private readonly UnixRawTerminal _tty;

        public ClientSide(string paneId)
        {
            _tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
            };
        }

        public AttachLiveState Live { get; }

        public List<bool> Apply(IEnumerable<JsonElement> bodies)
        {
            var applied = new List<bool>();
            foreach (var body in bodies)
            {
                var cells = body.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
                Assert.NotNull(cells);
                applied.Add(AttachSession.TryWriteCells(_tty, Live, cells, reanchorBeforePaint: true));
            }

            return applied;
        }

        public string HostText()
        {
            var text = new StringBuilder();
            for (var row = 0; row < Live.Host.Rows; row++)
            {
                for (var col = 0; col < Live.Host.Cols; col++)
                    text.Append(Live.Host.CellAt(col, row).Text);
                text.Append('\n');
            }

            return text.ToString();
        }

        public void Dispose() => _tty.Dispose();
    }

    private sealed class Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp, QueueSink sink)
        : IAsyncDisposable
    {
        public ControlPlaneService Cp { get; } = cp;

        public QueueSink Sink { get; } = sink;

        public string PaneId { get; set; } = "";

        public PaneRuntime Runtime { get; set; } = null!;

        public async Task PaintAsync()
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        public List<JsonElement> CellsOf(int lineMark)
        {
            var list = new List<JsonElement>();
            foreach (var line in Sink.Lines.Skip(lineMark))
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("kind", out var kind)
                    || kind.GetString() != TerminalRenderCellsPayload.KindCells
                    || !payload.TryGetProperty("pane_id", out var pane)
                    || pane.GetString() != PaneId)
                {
                    continue;
                }

                list.Add(payload.Clone());
            }

            return list;
        }

        public async Task ObserveAsync()
        {
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                Sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], Sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                Sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                Sink,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
        }
    }

    /// <summary>
    /// A connection whose render lines wait in a real writer queue. Reliable
    /// lines are written at once. <see cref="DrainAsync"/> runs the writer.
    /// </summary>
    private sealed class QueueSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly ClientWriterQueue _lanes = new();
        private int _queued;
        private int _canceled;

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes => 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        /// <summary>Render lines that wait for the writer.</summary>
        public int QueuedRenderLines => Volatile.Read(ref _queued);

        /// <summary>Bound render lines that the writer canceled.</summary>
        public int CanceledLines => Volatile.Read(ref _canceled);

        /// <summary>
        /// Takes every queued line as the socket writer does: a bound line
        /// is written only when the binding recheck of the mux passes, and a
        /// canceled line goes to the cancel hook of the mux.
        /// </summary>
        public async Task DrainAsync(ControlPlaneService cp)
        {
            while (true)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                var item = await _lanes.DequeueAsync(
                    idle.Token,
                    binding => binding is null || cp.RecheckAttachSurfaceEmit(binding),
                    binding =>
                    {
                        Interlocked.Increment(ref _canceled);
                        Interlocked.Decrement(ref _queued);
                        cp.NoteAttachRenderCanceled(binding);
                    });
                if (item is null)
                    return;
                Interlocked.Decrement(ref _queued);
                Add(Encoding.UTF8.GetString(item.Utf8.Span).TrimEnd('\n'));
                item.Completion?.TrySetResult();
                _lanes.NotifyWriteCompleted();
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Count1(_lanes.TryEnqueueRender(jsonLine));

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Count1(_lanes.TryEnqueueRender(utf8Json));

        public WriterLaneResult TryEnqueueRender(ReadOnlyMemory<byte> ndjsonLine) =>
            Count1(_lanes.TryEnqueueRender(ndjsonLine));

        public WriterLaneResult TryEnqueueRender(ReadOnlyMemory<byte> ndjsonLine, AttachSurfaceEmitBinding? emitBinding) =>
            Count1(_lanes.TryEnqueueRender(ndjsonLine, emitBinding));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Count1(_lanes.EnqueueOrderedRender(jsonLine));

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Count1(_lanes.EnqueueOrderedRender(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(ReadOnlyMemory<byte> ndjsonLine) =>
            Count1(_lanes.EnqueueOrderedRender(ndjsonLine));

        public WriterLaneResult EnqueueOrderedRender(ReadOnlyMemory<byte> ndjsonLine, AttachSurfaceEmitBinding? emitBinding) =>
            Count1(_lanes.EnqueueOrderedRender(ndjsonLine, emitBinding));

        public WriterLaneResult EnqueueOrderedRenderBatch(
            IReadOnlyList<ReadOnlyMemory<byte>> ndjsonLines,
            AttachSurfaceEmitBinding? emitBinding)
        {
            var admitted = _lanes.EnqueueOrderedRenderBatch(ndjsonLines, emitBinding);
            if (admitted.IsOk)
                Interlocked.Add(ref _queued, ndjsonLines.Count);
            return admitted;
        }

        private WriterLaneResult Count1(WriterLaneResult admitted)
        {
            if (admitted.IsOk)
                Interlocked.Increment(ref _queued);
            return admitted;
        }

        private void Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
        }
    }
}
