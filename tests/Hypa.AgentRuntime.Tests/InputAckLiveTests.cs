using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live <see cref="ControlPlaneService"/> with a <c>/bin/cat</c> pane. The
/// client sends <c>pane.send_keys</c> with a <c>seq</c>. Each live cells body
/// for that connection carries <c>input_ack</c>: the highest seq whose PTY
/// write completed before the capture of the body started.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class InputAckLiveTests
{
    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Each_echo_body_acks_at_least_the_seq_of_the_key_it_echoes()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        const string keys = "QWERTY";
        for (var i = 0; i < keys.Length; i++)
        {
            var seq = i + 1;
            var mark = session.Sink.Count;
            await session.SendKeyAsync(keys[i].ToString(), seq);
            var echo = await session.WaitForEchoAsync(mark, keys[i]);
            Assert.True(echo.TryGetProperty("input_ack", out var ack), echo.GetRawText());
            Assert.True(ack.GetInt64() >= seq, $"key {keys[i]} seq {seq}: {echo.GetRawText()}");
        }
    }

    [SkippableFact]
    public async Task A_capture_that_starts_before_a_write_completes_does_not_ack_that_write()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", 1);
        await session.WaitForEchoAsync(mark, 'Q');

        var actor = session.Cp.InputActorForTests(session.PaneId);
        Assert.NotNull(actor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.BeforeWriteForTests = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        await session.SendKeyAsync("W", 2);
        await Poll.GuardAsync(entered.Task, "write of seq 2 held");

        // Other output dirties the pane while write 2 is held. Its capture
        // starts before write 2 completes.
        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("Z");
        await session.PaintAsync();
        var held = await session.WaitForEchoAsync(mark, 'Z');
        Assert.True(held.TryGetProperty("input_ack", out var heldAck), held.GetRawText());
        Assert.Equal(1, heldAck.GetInt64());

        actor.BeforeWriteForTests = null;
        mark = session.Sink.Count;
        release.TrySetResult();
        var echo = await session.WaitForEchoAsync(mark, 'W');
        Assert.True(echo.GetProperty("input_ack").GetInt64() >= 2, echo.GetRawText());
    }

    [SkippableFact]
    public async Task An_echo_that_arrives_before_the_ack_record_is_not_posted_without_its_ack()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", 1);
        await session.WaitForEchoAsync(mark, 'Q');

        var actor = session.Cp.InputActorForTests(session.PaneId);
        Assert.NotNull(actor);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.AfterWriteForTests = async () =>
        {
            written.TrySetResult();
            await release.Task;
        };

        // Write 2 completes, and its record is held. cat echoes W into the VT.
        mark = session.Sink.Count;
        await session.SendKeyAsync("W", 2);
        await Poll.GuardAsync(written.Task, "write of seq 2 completed");
        await Poll.UntilAsync(() => session.Runtime.ReadVisibleText().Contains('W'), "cat echoed W");
        await session.PaintAsync();
        Assert.DoesNotContain(CellsOn(session.Sink, mark), body => RowText(body).Contains('W'));

        actor.AfterWriteForTests = null;
        release.TrySetResult();
        var echo = await session.WaitForEchoAsync(mark, 'W');
        Assert.True(echo.GetProperty("input_ack").GetInt64() >= 2, echo.GetRawText());
        foreach (var body in CellsOn(session.Sink, mark).Where(body => RowText(body).Contains('W')))
            Assert.True(body.GetProperty("input_ack").GetInt64() >= 2, body.GetRawText());
    }

    [SkippableFact]
    public async Task A_paint_held_past_one_render_tick_posts_without_input_ack()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", 1);
        await session.WaitForEchoAsync(mark, 'Q');

        var actor = session.Cp.InputActorForTests(session.PaneId);
        Assert.NotNull(actor);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.AfterWriteForTests = async () =>
        {
            written.TrySetResult();
            await release.Task;
        };

        mark = session.Sink.Count;
        await session.SendKeyAsync("W", 2);
        await Poll.GuardAsync(written.Task, "write of seq 2 completed");
        await Poll.UntilAsync(() => session.Runtime.ReadVisibleText().Contains('W'), "cat echoed W");
        await session.PaintAsync();
        Assert.DoesNotContain(CellsOn(session.Sink, mark), body => RowText(body).Contains('W'));

        // A write that does not end must not freeze the pane. After one
        // render tick the paint posts. Its ack is unknown, so it has none.
        session.Time.Advance(TimeSpan.FromMilliseconds(20));
        try
        {
            var body = await session.WaitForEchoAsync(mark, 'W');
            Assert.False(body.TryGetProperty("input_ack", out _), body.GetRawText());
        }
        finally
        {
            actor.AfterWriteForTests = null;
            release.TrySetResult();
        }
    }

    [SkippableFact]
    public async Task A_write_that_ends_after_a_bypassed_paint_gets_an_ack_only_body_that_closes_its_key()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        using var client = new ClientSide(session.PaneId, new FrozenClock());
        var meter = client.Live.Latency;
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", OpenKey(meter, session.PaneId, 'Q'));
        await session.WaitForEchoAsync(mark, 'Q');

        var actor = session.Cp.InputActorForTests(session.PaneId);
        Assert.NotNull(actor);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.AfterWriteForTests = async () =>
        {
            written.TrySetResult();
            await release.Task;
        };

        mark = session.Sink.Count;
        await session.SendKeyAsync("W", OpenKey(meter, session.PaneId, 'W'));
        await Poll.GuardAsync(written.Task, "write of seq 2 completed");
        await Poll.UntilAsync(() => session.Runtime.ReadVisibleText().Contains('W'), "cat echoed W");
        await session.PaintAsync();
        session.Time.Advance(TimeSpan.FromMilliseconds(20));
        var bypass = await session.WaitForEchoAsync(mark, 'W');
        Assert.False(bypass.TryGetProperty("input_ack", out _), bypass.GetRawText());
        Assert.Equal(0, session.Cp.InputAckOnlyPostsForTests);

        actor.AfterWriteForTests = null;
        release.TrySetResult();
        var ackOnly = await WaitForAckOnlyAsync(session.Sink, mark);
        Assert.Equal(2, ackOnly.GetProperty("input_ack").GetInt64());
        Assert.False(ackOnly.TryGetProperty("rows", out _), ackOnly.GetRawText());
        Assert.False(ackOnly.TryGetProperty("cursor", out _), ackOnly.GetRawText());
        Assert.Equal(bypass.GetProperty("generation").GetInt64(), ackOnly.GetProperty("generation").GetInt64());

        // The client applies every body in order. The ack-only body closes
        // the key that the bypassed paint showed, so no key stays open.
        client.Apply(CellsOn(session.Sink, 0));
        var report = meter.Report();
        Assert.Equal(2, report.KeysAcked);
        Assert.Equal(0, report.OpenKeys);
        Assert.Equal(0, report.StaleKey);

        // The ack-only body does not change the pane frame or its chain.
        Assert.True(client.Live.TryGetPaneFrame(session.PaneId, out var frame));
        Assert.Contains("QW", VtCellUnpacker.RowText(frame!, 0), StringComparison.Ordinal);
        Assert.Equal(bypass.GetProperty("generation").GetInt64(), client.Live.LastAppliedBlit(session.PaneId).Generation);

        // One owed ack gives one body.
        session.Runtime.PublishFedOutputForTests("Z");
        await session.WaitForEchoAsync(mark, 'Z');
        Assert.Equal(1, session.Cp.InputAckOnlyPostsForTests);
    }

    [SkippableFact]
    public async Task An_ack_only_body_that_finds_the_writer_full_posts_after_the_drain()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", 1);
        await session.WaitForEchoAsync(mark, 'Q');

        var actor = session.Cp.InputActorForTests(session.PaneId);
        Assert.NotNull(actor);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actor.AfterWriteForTests = async () =>
        {
            written.TrySetResult();
            await release.Task;
        };

        mark = session.Sink.Count;
        await session.SendKeyAsync("W", 2);
        await Poll.GuardAsync(written.Task, "write of seq 2 completed");
        await Poll.UntilAsync(() => session.Runtime.ReadVisibleText().Contains('W'), "cat echoed W");
        await session.PaintAsync();
        session.Time.Advance(TimeSpan.FromMilliseconds(20));
        await session.WaitForEchoAsync(mark, 'W');

        // The writer is full when the write ends: the ack stays owed.
        session.Sink.RefuseAckOnly = true;
        actor.AfterWriteForTests = null;
        release.TrySetResult();
        await Poll.UntilAsync(() => session.Sink.RefusedAckOnly > 0, "ack-only body refused");
        Assert.DoesNotContain(CellsOn(session.Sink, mark), body => body.TryGetProperty("ack_only", out _));

        session.Sink.RefuseAckOnly = false;
        session.Cp.OnClientWriterDrained(session.Sink);
        var ackOnly = await WaitForAckOnlyAsync(session.Sink, mark);
        Assert.Equal(2, ackOnly.GetProperty("input_ack").GetInt64());
    }

    [SkippableFact]
    public async Task An_ack_only_body_for_a_frame_the_client_does_not_show_closes_no_key()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        using var client = new ClientSide(session.PaneId, new FrozenClock());
        client.Apply(CellsOn(session.Sink, 0));
        var meter = client.Live.Latency;
        var seq = OpenKey(meter, session.PaneId, 'Q');
        var applied = client.Live.LastAppliedBlit(session.PaneId);
        Assert.True(applied.Generation > 0);

        client.Apply([JsonSerializer.SerializeToElement(
            new TerminalRenderCellsPayload
            {
                Kind = TerminalRenderCellsPayload.KindCells,
                PaneId = session.PaneId,
                AckOnly = true,
                Generation = applied.Generation + 5,
                InputAck = seq,
            },
            ProtocolJsonContext.Default.TerminalRenderCellsPayload)]);
        Assert.Equal(1, meter.Report().OpenKeys);
        Assert.Equal(applied, client.Live.LastAppliedBlit(session.PaneId));

        client.Apply([JsonSerializer.SerializeToElement(
            new TerminalRenderCellsPayload
            {
                Kind = TerminalRenderCellsPayload.KindCells,
                PaneId = session.PaneId,
                AckOnly = true,
                Generation = applied.Generation,
                InputAck = seq,
            },
            ProtocolJsonContext.Default.TerminalRenderCellsPayload)]);
        Assert.Equal(0, meter.Report().OpenKeys);
        Assert.Equal(applied, client.Live.LastAppliedBlit(session.PaneId));
    }

    [SkippableFact]
    public async Task Echoes_that_carry_their_ack_post_no_ack_only_body()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        const string keys = "ASDFG";
        for (var i = 0; i < keys.Length; i++)
        {
            var mark = session.Sink.Count;
            await session.SendKeyAsync(keys[i].ToString(), i + 1);
            await session.WaitForEchoAsync(mark, keys[i]);
        }

        Assert.Equal(0, session.Cp.InputAckOnlyPostsForTests);
        Assert.DoesNotContain(CellsOn(session.Sink, 0), body => body.TryGetProperty("ack_only", out _));
    }

    /// <summary>
    /// A meter clock that does not move, so a key never reaches the stale
    /// age while the test waits on the real mux.
    /// </summary>
    private sealed class FrozenClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => 1;
    }

    /// <summary>The steps of the attach input loop and sender for one key.</summary>
    private static long OpenKey(AttachLatencyMeter meter, string paneId, char key)
    {
        var bytes = new[] { (byte)key };
        meter.NoteStdinRead(bytes, meter.Now());
        var seq = meter.NextSeq();
        meter.NoteSent(paneId, seq, [meter.StampKeys(bytes)]);
        return seq;
    }

    private static async Task<JsonElement> WaitForAckOnlyAsync(LaneSink sink, int mark)
    {
        JsonElement found = default;
        await Poll.UntilAsync(
            () =>
            {
                foreach (var body in CellsOn(sink, mark))
                {
                    if (body.TryGetProperty("ack_only", out var ackOnly) && ackOnly.GetBoolean())
                    {
                        found = body;
                        return true;
                    }
                }

                return false;
            },
            () => $"an ack-only body: {string.Join('\n', sink.Lines.Skip(mark))}");
        return found;
    }

    [SkippableFact]
    public async Task An_observer_that_sent_no_seq_gets_no_input_ack()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var plain = await session.AddObserverAsync("input-ack-plain");

        var mark = session.Sink.Count;
        var plainMark = plain.Count;
        await session.SendKeyAsync("Q", 1);
        var echo = await session.WaitForEchoAsync(mark, 'Q');
        Assert.Equal(1, echo.GetProperty("input_ack").GetInt64());

        var plainEcho = await WaitForEchoAsync(session, plain, plainMark, 'Q');
        Assert.False(plainEcho.TryGetProperty("input_ack", out _), plainEcho.GetRawText());
        foreach (var line in plain.Lines)
            Assert.DoesNotContain("\"input_ack\"", line, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Keys_without_seq_put_no_input_ack_on_the_wire()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        await session.SendKeyAsync("Q", seq: null);
        var echo = await session.WaitForEchoAsync(mark, 'Q');
        Assert.False(echo.TryGetProperty("input_ack", out _), echo.GetRawText());
        foreach (var line in session.Sink.Lines)
            Assert.DoesNotContain("\"input_ack\"", line, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Latency_report_measures_key_to_paint_for_a_local_pane()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        using var client = new ClientSide(session.PaneId);
        client.Apply(CellsOn(session.Sink, 0));
        var meter = client.Live.Latency;
        const string keys = "ASDF";
        foreach (var key in keys)
        {
            // The attach input loop and sender do these steps for a real key.
            var bytes = new[] { (byte)key };
            meter.NoteStdinRead(bytes, meter.Now());
            var seq = meter.NextSeq();
            meter.NoteSent(session.PaneId, seq, [meter.StampKeys(bytes)]);
            var mark = session.Sink.Count;
            await session.SendKeyAsync(key.ToString(), seq);
            await session.WaitForEchoAsync(mark, key);
            client.Apply(CellsOn(session.Sink, mark));
        }

        var reply = AttachSession.ApplyLatencyAction(
            client.Live,
            new AttachSemanticRequest { Action = AttachSemanticActions.LatencyReport });
        Assert.Equal(AttachSemanticOutcomes.Applied, reply.Outcome);
        var latency = Assert.IsType<AttachLatencyReport>(reply.Latency);
        Assert.Equal(keys.Length, latency.KeysRead);
        Assert.Equal(keys.Length, latency.KeysAcked);
        Assert.Equal(0, latency.OpenKeys);
        Assert.True(latency.PredictionCounters.ContainsKey("made"));
        Assert.True(latency.PredictionCounters.ContainsKey("reset.synchronized_output"));
        Assert.Equal(keys.Length, latency.KeyToPaint.Count);
        Assert.True(latency.KeyToPaint.MaxUs > 0);
        Assert.True(latency.Hops["apply"].Count >= keys.Length);
        Assert.True(latency.Hops["compose"].Count >= keys.Length);
        Assert.True(latency.Hops["encode"].Count >= keys.Length);
        Assert.True(latency.Hops["host-write"].Count >= keys.Length);
    }

    private async Task<Session> OpenAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-input-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("input-ack"));
        state.UpdateSession(s => s with { Name = "input-ack", LifecycleState = SessionLifecycle.Ready });
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
        // The clock moves only when a test moves it, so a held paint stays
        // held until its write ends.
        var time = new ManualTimeProvider();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            timeProvider: time);
        var session = new Session(dir, paths, cp, new LaneSink("input-ack")) { Time = time };
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
            await session.ObserveAsync(session.Sink);
            var claimed = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject
                {
                    ["pane_id"] = session.PaneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 60_000,
                    ["takeover"] = false,
                }),
                session.Sink,
                CancellationToken.None);
            Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
            session.LeaseId = claimed.GetProperty("lease_id").GetString()!;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<JsonElement> WaitForEchoAsync(
        Session session,
        LaneSink sink,
        int mark,
        char glyph)
    {
        JsonElement found = default;
        await Poll.UntilAsync(
            async () =>
            {
                foreach (var body in CellsOn(sink, mark))
                {
                    if (RowText(body).Contains(glyph))
                    {
                        found = body;
                        return true;
                    }
                }

                await session.PaintAsync();
                return false;
            },
            () => $"a cells body that shows {glyph}: {string.Join('\n', sink.Lines.Skip(mark))}");
        return found;
    }

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

    private static List<JsonElement> CellsOn(LaneSink sink, int lineMark)
    {
        var list = new List<JsonElement>();
        foreach (var line in sink.Lines.Skip(lineMark))
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("params", out var parms))
                continue;
            if (!parms.TryGetProperty("type", out var type)
                || type.GetString() != ProtocolEventTypes.TerminalRender)
                continue;
            if (!parms.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("kind", out var kind)
                || kind.GetString() != TerminalRenderCellsPayload.KindCells)
                continue;
            list.Add(payload.Clone());
        }

        return list;
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    /// <summary>
    /// The attach client side: a chrome-off live state over an in-memory TTY.
    /// Its meter closes keys when a written frame acks them.
    /// </summary>
    private sealed class ClientSide : IDisposable
    {
        private readonly UnixRawTerminal _tty;

        public ClientSide(string paneId, TimeProvider? meterTime = null)
        {
            _tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
                Latency = new AttachLatencyMeter(meterTime),
            };
        }

        public AttachLiveState Live { get; }

        public void Apply(IEnumerable<JsonElement> bodies)
        {
            foreach (var body in bodies)
            {
                var cells = body.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
                Assert.NotNull(cells);
                AttachSession.TryWriteCells(_tty, Live, cells, reanchorBeforePaint: true);
            }
        }

        public void Dispose() => _tty.Dispose();
    }

    private sealed class Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp, LaneSink sink)
        : IAsyncDisposable
    {
        public ControlPlaneService Cp { get; } = cp;

        public LaneSink Sink { get; } = sink;

        public ManualTimeProvider Time { get; init; } = null!;

        public PaneRuntime Runtime { get; set; } = null!;

        public string PaneId { get; set; } = "";

        public string LeaseId { get; set; } = "";

        public async Task SendKeyAsync(string text, long? seq)
        {
            var body = new JsonObject
            {
                ["pane_id"] = PaneId,
                ["lease_id"] = LeaseId,
                ["encoding"] = "utf8",
                ["data"] = text,
            };
            if (seq is { } value)
                body["seq"] = value;
            await Cp.DispatchAsync(ProtocolMethods.PaneSendKeys, Json(body), Sink, CancellationToken.None);
        }

        public Task<JsonElement> WaitForEchoAsync(int mark, char glyph) =>
            InputAckLiveTests.WaitForEchoAsync(this, Sink, mark, glyph);

        /// <summary>Paint the pending frame and wait until every sink has it.</summary>
        public async Task PaintAsync()
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        public async Task ObserveAsync(LaneSink sink)
        {
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                sink,
                CancellationToken.None);
        }

        public async Task<LaneSink> AddObserverAsync(string connectionId)
        {
            var sink = new LaneSink(connectionId);
            await ObserveAsync(sink);
            return sink;
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

        /// <summary>When true, the ordered lane is full for an ack-only body.</summary>
        public volatile bool RefuseAckOnly;

        private int _refusedAckOnly;

        public int RefusedAckOnly => Volatile.Read(ref _refusedAckOnly);

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
            if (RefuseAckOnly && json.Contains("\"ack_only\":true", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refusedAckOnly);
                return WriterLaneResult.Full;
            }

            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
