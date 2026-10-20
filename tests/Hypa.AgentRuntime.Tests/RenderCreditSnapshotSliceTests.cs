using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Terminal;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live <see cref="ControlPlaneService"/> with a large pane A and a small
/// pane B that gets the input of the connection. The panes give JSON full
/// snapshots, or cells frames when the test turns frames on. The connection
/// offers render credit with a fixed small budget. A large snapshot or cells
/// frame goes in slices, and each slice waits for credit.
/// </summary>
public sealed class RenderCreditSnapshotSliceTests
{
    private const int Budget = 6144;
    private const int Slice = 1024;

    [Fact]
    public async Task A_snapshot_goes_in_slices_that_wait_for_credit_and_an_echo_goes_between_them()
    {
        await using var session = await OpenAsync();
        var first = session.SnapshotLines(session.PaneA, 0);
        Assert.NotEmpty(first);
        Assert.DoesNotContain(first, line => line.Complete);
        // The bytes in flight stay near the budget, not the full snapshot.
        Assert.True(first.Sum(l => l.Bytes) <= Budget + MaxLine(first), $"{first.Sum(l => l.Bytes)} bytes");
        Assert.True(session.Credit().UnackedBytes <= Budget + MaxLine(first));

        // Input gives pane B the focus. Its echo goes while the rest of the
        // snapshot of pane A waits for credit.
        var mark = session.Sink.Count;
        await session.TypeAsync(session.PaneB, "e");
        Assert.Equal(session.PaneB, session.Credit().FocusedPaneId);
        session.RuntimeB.Emit("ECHO");
        await session.PaintAsync(session.PaneB);
        Assert.Contains(session.SnapshotLines(session.PaneB, mark), line => line.Complete);
        Assert.Empty(session.SnapshotLines(session.PaneA, mark));

        // Each ack lets the next slices go, until the batch is complete.
        var all = await session.AckUntilCompleteAsync(session.PaneA, first);
        var assembler = new SnapshotAssembler();
        AssembledSnapshot? frame = null;
        foreach (var line in all)
        {
            // A slice holds whole rows near the target size. A row larger
            // than the target goes alone.
            var rows = line.Payload.RowEnd - line.Payload.RowStart;
            Assert.True(line.Bytes <= Slice + 512 || rows == 1, $"slice of {line.Bytes} bytes and {rows} rows");
            if (assembler.TryAdd(line.Payload, out var complete))
                frame = complete;
        }

        Assert.NotNull(frame);
        Assert.Equal(SliceRuntime.LargeRows, frame.Rows);
        Assert.True(all.Count > 4, $"{all.Count} slices");
    }

    [Fact]
    public async Task A_started_snapshot_completes_before_the_next_cells_frame_of_its_pane()
    {
        await using var session = await OpenAsync();
        var first = session.SnapshotLines(session.PaneA, 0);
        Assert.NotEmpty(first);
        Assert.DoesNotContain(first, line => line.Complete);

        // The pane now gives cells frames. A new paint must not go before
        // the rest of the snapshot that the client started to get.
        session.RuntimeA.FramesOn = true;
        session.RuntimeA.Emit("NEWER");
        var mark = session.Sink.Count;
        await session.PaintAsync(session.PaneA);
        Assert.Empty(session.CellsLines(session.PaneA, mark));

        await session.AckUntilCompleteAsync(session.PaneA, first);
        await Poll.UntilAsync(
            async () =>
            {
                if (session.CellsLines(session.PaneA, 0).Count > 0)
                    return true;
                await session.AckAllAsync();
                return false;
            },
            "a cells frame after the snapshot");

        var cells = session.CellsLines(session.PaneA, 0);
        var lines = session.Sink.Lines;
        var completeAt = lines.ToList().FindIndex(line =>
            line.Contains("\"kind\":\"snapshot\"", StringComparison.Ordinal)
            && line.Contains("\"complete\":true", StringComparison.Ordinal)
            && line.Contains(session.PaneA, StringComparison.Ordinal));
        var cellsAt = lines.ToList().FindIndex(line =>
            line.Contains("\"kind\":\"cells\"", StringComparison.Ordinal)
            && line.Contains(session.PaneA, StringComparison.Ordinal));
        Assert.True(completeAt >= 0 && cellsAt > completeAt, $"complete at {completeAt}, cells at {cellsAt}");
        Assert.NotEmpty(cells);
    }

    [Fact]
    public async Task A_large_cells_frame_goes_in_slices_and_the_client_applies_each_slice_in_order()
    {
        await using var session = await OpenAsync(frames: true);
        using var client = new ClientSide(session.PaneA);
        var first = session.CellsSlices(session.PaneA, 0);
        // A pane without the focus puts one slice in front of the writer, so
        // an echo of another pane never waits for a budget of its slices.
        Assert.Single(first);
        var generation = first[0].Generation;
        Assert.All(first, l => Assert.Equal(generation, l.Generation));
        Assert.True(first.Sum(l => l.Rows) < SliceRuntime.LargeRows, $"{first.Sum(l => l.Rows)} rows in {first.Count} lines");
        Assert.True(first.Sum(l => l.Bytes) <= Budget + MaxLine(first), $"{first.Sum(l => l.Bytes)} bytes");

        // The echo of pane B goes before the next slice of pane A. Pane A
        // puts one slice at a time in front of the writer, so the echo waits
        // for one slice at most.
        var mark = session.Sink.Count;
        await session.TypeAsync(session.PaneB, "e");
        session.RuntimeB.Emit("ECHO");
        await session.PaintAsync(session.PaneB);
        var echo = session.CellsSlices(session.PaneB, mark);
        Assert.NotEmpty(echo);
        var ahead = session.CellsSlices(session.PaneA, mark).Count(slice => slice.Index < echo[0].Index);
        Assert.True(ahead <= 1, $"{ahead} slices of pane A went between the key and its echo");

        // A new paint of pane A waits behind the rest of its frame.
        session.RuntimeA.Emit("AFTER");
        await session.PaintAsync(session.PaneA);
        Assert.DoesNotContain(session.CellsSlices(session.PaneA, mark), slice => slice.Generation != generation);

        // The client acks each slice when it reads it, with its end row.
        List<CellsSlice> Slices(int from) =>
            session.CellsSlices(session.PaneA, from).Where(l => l.Generation == generation).ToList();
        await session.AckEachLineAsync(
            session.PaneA,
            generation,
            from => [.. Slices(from).Select(l => (l.Index, l.Bytes, l.RowEnd))],
            seen => seen.Count > 0 && seen[^1].RowEnd >= SliceRuntime.LargeRows);
        var all = Slices(first[0].Index);

        Assert.Equal(SliceRuntime.LargeRows, all.Sum(l => l.Rows));
        Assert.True(all.Count > 4, $"{all.Count} slices");
        Assert.All(client.Apply(all.Select(l => l.Payload)), Assert.True);
        Assert.False(client.Live.BlitReanchorPending);

        // The next frame of pane A comes after the last slice, and chains on it.
        var after = new List<CellsSlice>();
        await Poll.UntilAsync(
            async () =>
            {
                after = session.CellsSlices(session.PaneA, 0).Where(l => l.Generation > generation).ToList();
                if (after.Any(l => l.Text.Contains("AFTER", StringComparison.Ordinal)))
                    return true;
                await session.AckAllAsync();
                return false;
            },
            "a frame of pane A after its slices");

        Assert.True(after[0].Index > all[^1].Index);
        Assert.All(client.Apply(after.Select(l => l.Payload)), Assert.True);
        Assert.False(client.Live.BlitReanchorPending);
    }

    private static int MaxLine(IReadOnlyList<SliceLine> lines) => lines.Count == 0 ? 0 : lines.Max(l => l.Bytes);

    private static int MaxLine(IReadOnlyList<CellsSlice> lines) => lines.Count == 0 ? 0 : lines.Max(l => l.Bytes);

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static AttachEndpointHello Hello() =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = "slice-client",
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
            OptionalCapabilities = [AttachEndpointProtocol.RenderCreditCapability],
        };

    private static async Task<Session> OpenAsync(bool frames = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-credit-slice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("credit-slice"));
        state.UpdateSession(s => s with { Name = "credit-slice", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var journal = new FileRuntimeEventJournal(paths, new SqliteJournalManifestStore(paths), state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new SliceFactory { Frames = frames };
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            timeProvider: new ManualTimeProvider())
        {
            RenderCreditOptions = new RenderCreditOptions
            {
                StartBudgetBytes = Budget,
                MinBudgetBytes = Budget,
                MaxBudgetBytes = Budget,
                MaxFocusedBudgetBytes = Budget,
                SliceBytes = Slice,
            },
        };
        var sink = new LaneSink("credit-slice");
        var session = new Session(dir, paths, cp, sink);
        try
        {
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject { ["command"] = "/bin/cat", ["cwd"] = dir }),
                CancellationToken.None);
            session.PaneA = create.GetProperty("pane_id").GetString()!;
            var split = await cp.DispatchAsync(
                ProtocolMethods.PaneSplit,
                Json(new JsonObject
                {
                    ["pane_id"] = session.PaneA,
                    ["direction"] = "right",
                    ["command"] = "/bin/cat",
                    ["cwd"] = dir,
                }),
                CancellationToken.None);
            session.PaneB = split.GetProperty("pane_id").GetString()!;
            session.RuntimeA = factory.Created.Single(r => r.Id.Value == session.PaneA);
            session.RuntimeB = factory.Created.Single(r => r.Id.Value == session.PaneB);

            await cp.HandleAttachHelloAsync(Hello(), sink, CancellationToken.None);
            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "on",
                    ClientId = "slice-client",
                    Active = true,
                    GeometryRevision = 1,
                },
                sink,
                CancellationToken.None);
            await session.ObserveAsync();
            // The first slices of pane A spend the budget. The first frame
            // of pane B waits for credit.
            await Poll.UntilAsync(
                () => frames
                    ? session.CellsSlices(session.PaneA, 0).Count > 0
                    : session.SnapshotLines(session.PaneA, 0).Count > 0,
                "first slices of pane A");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed record SliceLine(int Index, int Bytes, bool Complete, long Generation, TerminalRenderSnapshotPayload Payload)
    {
        public int RowEnd => Payload.RowEnd;
    }

    private sealed record CellsSlice(int Index, int Bytes, long Generation, int Rows, string Text, JsonElement Payload)
    {
        public int RowEnd => RenderRows.CellsRowEnd(Payload);
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

        public void Dispose() => _tty.Dispose();
    }

    private sealed class Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp, LaneSink sink)
        : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _leases = new(StringComparer.Ordinal);

        public ControlPlaneService Cp { get; } = cp;

        public LaneSink Sink { get; } = sink;

        public string PaneA { get; set; } = "";

        public string PaneB { get; set; } = "";

        public SliceRuntime RuntimeA { get; set; } = null!;

        public SliceRuntime RuntimeB { get; set; } = null!;

        public RenderCreditState Credit() =>
            Cp.RenderCreditStateFor(Sink.ConnectionId) ?? throw new InvalidOperationException("no render credit");

        public async Task PaintAsync(string paneId)
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(paneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(paneId);
        }

        public async Task TypeAsync(string paneId, string text)
        {
            if (!_leases.TryGetValue(paneId, out var lease))
            {
                var claimed = await Cp.DispatchAsync(
                    ProtocolMethods.RuntimeLeaseClaim,
                    Json(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["scope"] = "input",
                        ["ttl_ms"] = 60_000,
                        ["takeover"] = false,
                    }),
                    Sink,
                    CancellationToken.None);
                lease = claimed.GetProperty("lease_id").GetString()!;
                _leases[paneId] = lease;
            }

            await Cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                Json(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["lease_id"] = lease,
                    ["encoding"] = "utf8",
                    ["data"] = text,
                }),
                Sink,
                CancellationToken.None);
        }

        /// <summary>Acks the lines of the pane up to this generation and end row, as the client does.</summary>
        public Task AckAsync(string paneId, long generation, int? rowEnd = null)
        {
            var entry = new JsonObject
            {
                ["pane_id"] = paneId,
                ["generation"] = generation,
            };
            if (rowEnd is { } end)
                entry["row_end"] = end;
            return Cp.DispatchAsync(
                ProtocolMethods.RenderAck,
                Json(new JsonObject { ["acks"] = new JsonArray(entry) }),
                Sink,
                CancellationToken.None);
        }

        /// <summary>
        /// Acks the lines that the client read, one at a time, with the end
        /// row of each, as the attach render loop does. After each ack, the
        /// bytes that the mux sent and the client did not ack stay at most
        /// the budget and one line. Returns when <paramref name="done"/> is
        /// true for the lines of the generation.
        /// </summary>
        public async Task<List<(int Index, int Bytes, int RowEnd)>> AckEachLineAsync(
            string paneId,
            long generation,
            Func<int, List<(int Index, int Bytes, int RowEnd)>> linesFrom,
            Func<List<(int Index, int Bytes, int RowEnd)>, bool> done)
        {
            var seen = new List<(int Index, int Bytes, int RowEnd)>(linesFrom(0));
            var acked = 0;
            await Poll.UntilAsync(
                async () =>
                {
                    // The credit retry runs on another task. Read the lines
                    // that it sent after the last ack, then ack one more.
                    var mark = seen.Count == 0 ? 0 : seen[^1].Index + 1;
                    seen.AddRange(linesFrom(mark));
                    var unacked = seen.Skip(acked).Sum(l => l.Bytes);
                    var largest = seen.Count == 0 ? 0 : seen.Max(l => l.Bytes);
                    Assert.True(
                        unacked <= Budget + largest,
                        $"{unacked} bytes sent and not acked after {acked} acks");
                    if (done(seen))
                        return true;
                    if (acked < seen.Count)
                    {
                        await AckAsync(paneId, generation, seen[acked].RowEnd);
                        acked++;
                    }

                    return false;
                },
                () => $"{seen.Count} lines, {acked} acked");

            return seen;
        }

        /// <summary>Acks the newest render generation of each pane that the sink holds.</summary>
        public async Task AckAllAsync()
        {
            foreach (var paneId in new[] { PaneA, PaneB })
            {
                var newest = 0L;
                foreach (var line in Sink.Lines)
                {
                    if (TryRender(line, paneId, out var payload)
                        && payload.TryGetProperty("generation", out var g))
                    {
                        newest = Math.Max(newest, g.GetInt64());
                    }
                }

                if (newest > 0)
                    await AckAsync(paneId, newest);
            }
        }

        /// <summary>
        /// Acks each snapshot slice as the client reads it, until the complete
        /// slice of the batch comes. Returns every slice of the batch.
        /// </summary>
        public async Task<List<SliceLine>> AckUntilCompleteAsync(string paneId, IReadOnlyList<SliceLine> first)
        {
            var generation = first[0].Generation;
            List<SliceLine> Slices(int mark) =>
                SnapshotLines(paneId, mark).Where(l => l.Generation == generation).ToList();
            await AckEachLineAsync(
                paneId,
                generation,
                mark => [.. Slices(mark).Select(l => (l.Index, l.Bytes, l.RowEnd))],
                seen => seen.Count > 0 && seen[^1].RowEnd >= SliceRuntime.LargeRows);
            var all = Slices(first[0].Index);
            Assert.Contains(all, l => l.Complete);
            return all;
        }

        public List<SliceLine> SnapshotLines(string paneId, int lineMark)
        {
            var list = new List<SliceLine>();
            var lines = Sink.Lines;
            for (var i = lineMark; i < lines.Count; i++)
            {
                if (!TryRender(lines[i], paneId, out var payload)
                    || payload.GetProperty("kind").GetString() != TerminalRenderSnapshotPayload.KindSnapshot)
                {
                    continue;
                }

                var typed = payload.Deserialize(ProtocolJsonContext.Default.TerminalRenderSnapshotPayload)!;
                list.Add(new SliceLine(i, Encoding.UTF8.GetByteCount(lines[i]), typed.Complete, typed.Generation, typed));
            }

            return list;
        }

        public List<CellsSlice> CellsSlices(string paneId, int lineMark)
        {
            var list = new List<CellsSlice>();
            var lines = Sink.Lines;
            for (var i = lineMark; i < lines.Count; i++)
            {
                if (!TryRender(lines[i], paneId, out var payload)
                    || payload.GetProperty("kind").GetString() != TerminalRenderCellsPayload.KindCells)
                {
                    continue;
                }

                var rows = 0;
                var text = new StringBuilder();
                if (payload.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rowsEl.EnumerateArray())
                    {
                        rows++;
                        if (row.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
                            text.Append(t.GetString());
                    }
                }

                list.Add(new CellsSlice(
                    i,
                    Encoding.UTF8.GetByteCount(lines[i]),
                    payload.GetProperty("generation").GetInt64(),
                    rows,
                    text.ToString(),
                    payload));
            }

            return list;
        }

        public List<string> CellsLines(string paneId, int lineMark)
        {
            var list = new List<string>();
            var lines = Sink.Lines;
            for (var i = lineMark; i < lines.Count; i++)
            {
                if (TryRender(lines[i], paneId, out var payload)
                    && payload.GetProperty("kind").GetString() == TerminalRenderCellsPayload.KindCells)
                {
                    list.Add(lines[i]);
                }
            }

            return list;
        }

        private static bool TryRender(string line, string paneId, out JsonElement payload)
        {
            payload = default;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("params", out var parms)
                || !parms.TryGetProperty("type", out var type)
                || type.GetString() != ProtocolEventTypes.TerminalRender
                || !parms.TryGetProperty("payload", out var body)
                || body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !body.TryGetProperty("pane_id", out var pane)
                || pane.GetString() != paneId)
            {
                return false;
            }

            payload = body.Clone();
            return true;
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
                    ["pane_ids"] = new JsonArray(PaneA, PaneB),
                }),
                Sink,
                CancellationToken.None);
            foreach (var paneId in new[] { PaneA, PaneB })
            {
                await Cp.DispatchAsync(
                    ProtocolMethods.TerminalObserve,
                    Json(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["subscription_id"] = subId,
                    }),
                    Sink,
                    CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
        }
    }

    private sealed class SliceFactory : IPaneRuntimeFactory
    {
        public List<SliceRuntime> Created { get; } = [];

        public bool Frames { get; init; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            // The first pane is large, the second small.
            var large = Created.Count == 0;
            var runtime = new SliceRuntime(
                options.Id,
                large ? SliceRuntime.LargeCols : 10,
                large ? SliceRuntime.LargeRows : 1)
            {
                FramesOn = Frames,
            };
            Created.Add(runtime);
            return runtime;
        }
    }

    /// <summary>
    /// A pane that gives JSON snapshots of a fixed grid. Every row shows the
    /// latest tag. With <see cref="FramesOn"/>, it also gives cells frames.
    /// </summary>
    private sealed class SliceRuntime(PaneId id, int cols, int rows) : IPaneRuntime, IPaneVtSnapshot
    {
        public const int LargeCols = 80;
        public const int LargeRows = 30;

        private readonly object _gate = new();
        private string _tag = "START";
        private long _feed = 1;

        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_400;
        public long FeedGeneration => Volatile.Read(ref _feed);
        public PaneFeedPaintDecision LastFeedPaintDecision => new(FeedGeneration, true);
        public volatile bool FramesOn;

        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
#pragma warning disable CS0067
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Emit(string tag)
        {
            lock (_gate)
                _tag = tag;
            Interlocked.Increment(ref _feed);
            OutputReceived?.Invoke(this, Encoding.UTF8.GetBytes(tag + "\r\n"));
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => Tag;
        public string ReadRecentText(int maxLines) => Tag;
        public string ReadRecentUnwrappedText(int maxLines) => Tag;
        public string ReadDetectionText() => Tag;

        private string Tag
        {
            get
            {
                lock (_gate)
                    return _tag;
            }
        }

        private string RowText(int row)
        {
            var text = (Tag + " row " + row + " ").PadRight(cols, 'x');
            return text[..cols];
        }

        public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
        {
            feedGeneration = FeedGeneration;
            var cells = new JsonArray();
            for (var r = 0; r < rows; r++)
            {
                var row = new JsonArray();
                foreach (var ch in RowText(r))
                    row.Add(new JsonObject { ["text"] = ch.ToString(), ["width"] = 1 });
                cells.Add(row);
            }

            json = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["provider"] = "ghostty",
                ["cols"] = cols,
                ["rows"] = rows,
                ["cursor"] = new JsonObject { ["col"] = 0, ["row"] = 0, ["visible"] = true },
                ["activeScreen"] = "main",
                ["cells"] = cells,
            }.ToJsonString();
            return true;
        }

        private static readonly string[] Colors = ["#ff0000", "#00ff00", "#0000ff", "#ffff00"];

        /// <summary>The cells grid. The colour changes every two cells, so a full frame is large.</summary>
        private VtFrame Frame()
        {
            var grid = new List<IReadOnlyList<VtCellView>>(rows);
            for (var r = 0; r < rows; r++)
            {
                var row = new List<VtCellView>(cols);
                var c = 0;
                foreach (var ch in RowText(r))
                {
                    row.Add(new VtCellView(ch.ToString(), 1, false, Colors[(c / 2) % Colors.Length], null, c % 4 == 0, false, false, false, false, false, false));
                    c++;
                }

                grid.Add(row);
            }

            return new VtFrame(
                Id.Value,
                cols,
                rows,
                grid,
                new VtFrameCursor(0, 0, true, 0),
                new VtFrameModes(false, true, false, false, "", false),
                0,
                0,
                1);
        }

        public bool TryCaptureAttachSnapshot(out VtAttachSnapshot? snapshot, out long feedGeneration)
        {
            feedGeneration = FeedGeneration;
            snapshot = new VtAttachSnapshot(Frame(), new VtScrollRegion(0, rows - 1), 1);
            return true;
        }

        public bool TryCaptureAttachFrame(out VtFrame? frame, out long feedGeneration)
        {
            feedGeneration = FeedGeneration;
            frame = FramesOn ? Frame() : null;
            return frame is not null;
        }

        public bool TryCaptureLivePaintFrame(
            out VtFrame? frame,
            out long feedGeneration,
            out PaneVtPaintKind kind,
            out VtDirtyRowPatch? patch)
        {
            patch = null;
            kind = PaneVtPaintKind.Full;
            feedGeneration = FeedGeneration;
            frame = FramesOn ? Frame() : null;
            return frame is not null;
        }

        public bool TryGetScrollMetrics(out int offset, out int maxOffset)
        {
            offset = 0;
            maxOffset = 0;
            return true;
        }

        public bool TryGetScrollOrigin(out int offset)
        {
            offset = 0;
            return true;
        }

        public bool TrySetScrollOrigin(int offset) => offset == 0;

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

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

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
