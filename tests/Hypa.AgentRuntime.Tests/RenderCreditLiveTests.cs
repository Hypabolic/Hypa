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
/// A live <see cref="ControlPlaneService"/> with two <c>/bin/cat</c> panes on
/// one attach connection. Pane A writes bulk output and pane B has the focus:
/// the connection sent input to pane B. The connection offers render credit
/// in its hello, with a fixed small budget, and acks frames with
/// <c>render.ack</c>.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class RenderCreditLiveTests
{
    private const int Budget = 2048;

    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Credit_that_comes_back_sends_one_delta_to_the_current_state_and_the_client_applies_it()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true);
        using var clientA = new ClientSide(session.PaneA);
        Assert.All(clientA.Apply(session.CellsOf(session.PaneA, 0)), Assert.True);

        var spentAt = await session.SpendBudgetAsync();
        Assert.All(clientA.Apply(session.CellsOf(session.PaneA, spentAt.Mark)), Assert.True);

        // Pane A changes two times while the budget is spent. The mux sends
        // no frame of pane A and keeps the latest one deferred.
        var mark = session.Sink.Count;
        session.RuntimeA.PublishFedOutputForTests("MARK-X\r\n");
        await session.PaintAsync(session.PaneA);
        session.RuntimeA.PublishFedOutputForTests("MARK-Y\r\n");
        await session.PaintAsync(session.PaneA);
        Assert.Empty(session.CellsOf(session.PaneA, mark));

        await session.AckAsync(session.PaneA, spentAt.Generation);
        var sent = await Poll.UntilAsync(
            () => session.CellsOf(session.PaneA, mark) is { Count: > 0 } list ? list : null,
            "pane A frame after the ack");
        // A second paint has nothing new to send.
        await session.PaintAsync(session.PaneA);
        sent = session.CellsOf(session.PaneA, mark);
        var delta = Assert.Single(sent);
        Assert.Equal(spentAt.Generation, delta.GetProperty("base_generation").GetInt64());
        Assert.False(delta.TryGetProperty("full", out var full) && full.GetBoolean(), delta.GetRawText());
        Assert.False(delta.TryGetProperty("reanchor", out var re) && re.GetBoolean(), delta.GetRawText());
        Assert.Contains("MARK-Y", RowText(delta), StringComparison.Ordinal);

        Assert.All(clientA.Apply(sent), Assert.True);
        Assert.False(clientA.Live.BlitReanchorPending);
        Assert.Equal(delta.GetProperty("generation").GetInt64(), clientA.Live.LastAppliedBlit(session.PaneA).Generation);
    }

    [SkippableFact]
    public async Task A_spent_budget_of_another_pane_does_not_hold_back_the_focused_pane_or_a_control_reply()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true);
        await session.SpendBudgetAsync();

        var mark = session.Sink.Count;
        session.RuntimeA.PublishFedOutputForTests("MORE-A\r\n");
        await session.PaintAsync(session.PaneA);
        session.RuntimeB.PublishFedOutputForTests("ECHO-B");
        await session.PaintAsync(session.PaneB);
        Assert.Empty(session.CellsOf(session.PaneA, mark));
        var echo = Assert.Single(session.CellsOf(session.PaneB, mark));
        Assert.Contains("ECHO-B", RowText(echo), StringComparison.Ordinal);

        var ping = await session.Cp.DispatchAsync(ProtocolMethods.Ping, null, session.Sink, CancellationToken.None)
            .WaitAsync(Poll.HangGuard);
        Assert.True(ping.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task Input_to_a_pane_gives_its_echo_the_focused_budget_before_an_ack_names_it()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true, focusPaneB: false);
        // The connection sent no input yet, so no pane has the focus.
        Assert.Null(session.Credit().FocusedPaneId);
        await session.SpendBudgetAsync();

        var mark = session.Sink.Count;
        await session.TypeAsync(session.PaneB, "Q");
        Assert.Equal(session.PaneB, session.Credit().FocusedPaneId);
        await session.WaitForEchoAsync(session.PaneB, mark, "Q");
    }

    [SkippableFact]
    public async Task An_ack_that_comes_after_input_never_moves_the_focus_away_from_the_echo()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true, focusPaneB: false);
        var spentAt = await session.SpendBudgetAsync();

        var mark = session.Sink.Count;
        await session.TypeAsync(session.PaneB, "Z");
        Assert.Equal(session.PaneB, session.Credit().FocusedPaneId);
        // An ack that the client wrote before that input arrives after it.
        // It names pane A in the field that an older client sent. The mux
        // reads no focus from an ack.
        await session.Cp.DispatchAsync(
            ProtocolMethods.RenderAck,
            Json(new JsonObject
            {
                ["acks"] = new JsonArray(new JsonObject
                {
                    ["pane_id"] = session.PaneA,
                    ["generation"] = session.CellsOf(session.PaneA, 0)[0].GetProperty("generation").GetInt64(),
                }),
                ["focused_pane_id"] = session.PaneA,
            }),
            session.Sink,
            CancellationToken.None);
        Assert.Equal(session.PaneB, session.Credit().FocusedPaneId);

        session.RuntimeA.PublishFedOutputForTests("STILL-A\r\n");
        await session.PaintAsync(session.PaneA);
        await session.WaitForEchoAsync(session.PaneB, mark, "Z");
        // Pane A still waits for credit: its budget is spent.
        Assert.Empty(session.CellsOf(session.PaneA, mark));
        Assert.True(session.Credit().UnackedBytes > 0);
        _ = spentAt;
    }

    [SkippableFact]
    public async Task Credit_comes_back_when_no_ack_arrives_before_the_timeout()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true);
        await session.SpendBudgetAsync();

        var mark = session.Sink.Count;
        session.RuntimeA.PublishFedOutputForTests("LATE-A\r\n");
        await session.PaintAsync(session.PaneA);
        Assert.Empty(session.CellsOf(session.PaneA, mark));

        var state = session.Credit();
        Assert.Equal(0, state.FailOpens);
        session.Time.Advance(TimeSpan.FromMilliseconds(500));
        await session.PaintAsync(session.PaneA);
        Assert.Empty(session.CellsOf(session.PaneA, mark));

        session.Time.Advance(TimeSpan.FromMilliseconds(600));
        var sent = await Poll.UntilAsync(
            () => session.CellsOf(session.PaneA, mark) is { Count: > 0 } list ? list : null,
            "pane A frame after the ack timeout");
        Assert.Contains("LATE-A", RowText(sent[^1]), StringComparison.Ordinal);
        Assert.Equal(1, session.Credit().FailOpens);
    }

    [SkippableFact]
    public async Task A_client_that_does_not_offer_credit_gets_every_frame_without_an_ack()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: false);
        Assert.Null(session.Cp.RenderCreditStateFor(session.Sink.ConnectionId));
        using var clientA = new ClientSide(session.PaneA);
        Assert.All(clientA.Apply(session.CellsOf(session.PaneA, 0)), Assert.True);

        var mark = session.Sink.Count;
        for (var i = 0; i < 6; i++)
        {
            session.RuntimeA.PublishFedOutputForTests(Screenful("NOCREDIT" + i));
            await session.PaintAsync(session.PaneA);
        }

        var sent = session.CellsOf(session.PaneA, mark);
        Assert.Equal(6, sent.Count);
        // With credit, the mux would stop after the first frames that pass the budget.
        var bytes = sent.Sum(body => Encoding.UTF8.GetByteCount(body.GetRawText()));
        Assert.True(bytes > Budget, $"sent {bytes} bytes");
        Assert.All(clientA.Apply(sent), Assert.True);
        Assert.DoesNotContain(session.Sink.Lines, line => line.Contains(ProtocolMethods.RenderAck, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_welcome_lists_render_credit_and_a_hello_without_it_turns_credit_off()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(offerCredit: true);
        Assert.Contains(
            AttachEndpointProtocol.RenderCreditCapability,
            session.Welcome.Capabilities,
            StringComparer.Ordinal);
        Assert.NotNull(session.Cp.RenderCreditStateFor(session.Sink.ConnectionId));

        await session.Cp.HandleAttachHelloAsync(Hello("credit-client", offerCredit: false), session.Sink, CancellationToken.None);
        Assert.Null(session.Cp.RenderCreditStateFor(session.Sink.ConnectionId));
    }

    private static string Screenful(string tag)
    {
        var text = new StringBuilder();
        for (var row = 0; row < 22; row++)
            text.Append(tag).Append(' ').Append(row).Append(' ').Append(new string('x', 50)).Append("\r\n");
        return text.ToString();
    }

    private static AttachEndpointHello Hello(string clientId, bool offerCredit) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = clientId,
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
            OptionalCapabilities = offerCredit ? [AttachEndpointProtocol.RenderCreditCapability] : null,
        };

    private static string RowText(JsonElement body)
    {
        if (!body.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return "";
        var text = new StringBuilder();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        }

        return text.ToString();
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private async Task<Session> OpenAsync(bool offerCredit, bool focusPaneB = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-render-credit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("render-credit"));
        state.UpdateSession(s => s with { Name = "render-credit", LifecycleState = SessionLifecycle.Ready });
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
        var time = new ManualTimeProvider();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            timeProvider: time)
        {
            RenderCreditOptions = new RenderCreditOptions
            {
                StartBudgetBytes = Budget,
                MinBudgetBytes = Budget,
                MaxBudgetBytes = Budget,
                MaxFocusedBudgetBytes = Budget,
            },
        };
        var sink = new LaneSink("render-credit");
        var session = new Session(dir, paths, cp, sink) { Time = time };
        try
        {
            session.PaneA = await CreatePaneAsync(cp, dir);
            session.PaneB = await SplitPaneAsync(cp, session.PaneA, dir);
            session.RuntimeA = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneA));
            session.RuntimeB = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneB));

            var welcome = await cp.HandleAttachHelloAsync(Hello("credit-client", offerCredit), sink, CancellationToken.None);
            session.Welcome = welcome.Deserialize(ProtocolJsonContext.Default.AttachEndpointWelcome)!;
            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "on",
                    ClientId = "credit-client",
                    Active = true,
                    GeometryRevision = 1,
                },
                sink,
                CancellationToken.None);
            await session.ObserveAsync();
            await Poll.UntilAsync(
                () => session.CellsOf(session.PaneA, 0).Count > 0 && session.CellsOf(session.PaneB, 0).Count > 0,
                "first frames of both panes");
            if (offerCredit && focusPaneB)
            {
                // Input gives pane B the focus.
                var mark = session.Sink.Count;
                await session.TypeAsync(session.PaneB, "f");
                await session.WaitForEchoAsync(session.PaneB, mark, "f");
                Assert.Equal(session.PaneB, session.Credit().FocusedPaneId);
            }

            if (offerCredit)
                await session.AckAllAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> CreatePaneAsync(ControlPlaneService cp, string dir)
    {
        var create = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["command"] = "/bin/cat",
                ["cwd"] = dir,
            }),
            CancellationToken.None);
        return create.GetProperty("pane_id").GetString()!;
    }

    private static async Task<string> SplitPaneAsync(ControlPlaneService cp, string paneId, string dir)
    {
        var split = await cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["direction"] = "right",
                ["command"] = "/bin/cat",
                ["cwd"] = dir,
            }),
            CancellationToken.None);
        return split.GetProperty("pane_id").GetString()!;
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
        public ControlPlaneService Cp { get; } = cp;

        public LaneSink Sink { get; } = sink;

        public ManualTimeProvider Time { get; init; } = null!;

        public AttachEndpointWelcome Welcome { get; set; } = null!;

        public string PaneA { get; set; } = "";

        public string PaneB { get; set; } = "";

        public PaneRuntime RuntimeA { get; set; } = null!;

        public PaneRuntime RuntimeB { get; set; } = null!;

        public RenderCreditState Credit() =>
            Cp.RenderCreditStateFor(Sink.ConnectionId) ?? throw new InvalidOperationException("no render credit");

        public async Task PaintAsync(string paneId)
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(paneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(paneId);
        }

        /// <summary>
        /// Pane A writes full screens until a paint sends no frame of pane A:
        /// the next frame does not fit in the budget. Returns the line mark
        /// before the first write and the generation of the last frame that
        /// the mux sent.
        /// </summary>
        public async Task<(int Mark, long Generation)> SpendBudgetAsync()
        {
            var mark = Sink.Count;
            var spent = false;
            for (var i = 0; i < 8 && !spent; i++)
            {
                var before = Sink.Count;
                RuntimeA.PublishFedOutputForTests(Screenful("BULK" + i));
                await PaintAsync(PaneA);
                spent = CellsOf(PaneA, before).Count == 0;
            }

            Assert.True(spent, $"unacked {Credit().UnackedBytes} budget {Credit().Budget}");
            Assert.True(Credit().UnackedBytes > 0);
            var sent = CellsOf(PaneA, mark);
            Assert.NotEmpty(sent);
            return (mark, sent[^1].GetProperty("generation").GetInt64());
        }

        private readonly Dictionary<string, string> _leases = new(StringComparer.Ordinal);

        public async Task<string> ClaimInputAsync(string paneId)
        {
            if (_leases.TryGetValue(paneId, out var held))
                return held;
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
            Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
            var lease = claimed.GetProperty("lease_id").GetString()!;
            _leases[paneId] = lease;
            return lease;
        }

        public Task AckAsync(string paneId, long generation) =>
            Cp.DispatchAsync(
                ProtocolMethods.RenderAck,
                Json(new JsonObject
                {
                    ["acks"] = new JsonArray(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["generation"] = generation,
                    }),
                }),
                Sink,
                CancellationToken.None);

        /// <summary>Sends <paramref name="text"/> to the pane as input of this connection.</summary>
        public async Task TypeAsync(string paneId, string text)
        {
            var lease = await ClaimInputAsync(paneId);
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

        /// <summary>Paints the pane until a cells frame after the mark shows <paramref name="text"/>.</summary>
        public async Task WaitForEchoAsync(string paneId, int mark, string text)
        {
            await Poll.UntilAsync(
                async () =>
                {
                    if (CellsOf(paneId, mark).Any(body => RowText(body).Contains(text, StringComparison.Ordinal)))
                        return true;
                    await PaintAsync(paneId);
                    return false;
                },
                "an echo frame of " + paneId);
        }

        /// <summary>Acks the latest frame of each pane.</summary>
        public async Task AckAllAsync()
        {
            var acks = new JsonArray();
            foreach (var paneId in new[] { PaneA, PaneB })
            {
                var cells = CellsOf(paneId, 0);
                if (cells.Count > 0)
                {
                    acks.Add(new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["generation"] = cells[^1].GetProperty("generation").GetInt64(),
                    });
                }
            }

            await Cp.DispatchAsync(
                ProtocolMethods.RenderAck,
                Json(new JsonObject { ["acks"] = acks }),
                Sink,
                CancellationToken.None);
            Assert.Equal(0, Credit().UnackedBytes);
            Assert.Equal(0, Credit().FocusedUnackedBytes);
        }

        public List<JsonElement> CellsOf(string paneId, int lineMark)
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
                    || pane.GetString() != paneId)
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
