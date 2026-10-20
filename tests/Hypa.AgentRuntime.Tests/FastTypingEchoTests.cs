using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live <see cref="ControlPlaneService"/> on a manual clock with two
/// <c>/bin/cat</c> panes on one connection. Pane A gets keys through
/// <c>pane.send_keys</c>, and the test writes the shell echo of each key in
/// two reads. Pane B writes bulk output. The PTY input of both panes drops
/// the bytes, so only the test writes pane output.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class FastTypingEchoTests
{
    private const int Keys = 12;
    private const int KeyGapMs = 10;
    private const int StepMs = 1;

    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Echo_of_each_key_in_a_fast_burst_reaches_the_writer_without_a_render_tick()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.RuntimeA.PublishFedOutputForTests("$ ");
        await session.PaintAsync(session.PaneA);
        session.RuntimeB.PublishFedOutputForTests("bulk start\r\n");
        await session.PaintAsync(session.PaneB);
        await session.AdvanceAsync(100);

        var start = session.Time.GetUtcNow();
        var bulkMark = session.Sink.Count;
        var bulkFrames = new List<DateTimeOffset>();
        var typed = new StringBuilder();
        var bulkLine = 0;
        for (var key = 0; key < Keys; key++)
        {
            var ch = (char)('a' + key);
            typed.Append(ch);
            await session.TypeAsync(session.PaneA, ch.ToString());

            // Read 1: the echo of the character. The clock does not move, so
            // the frame reaches the writer with no render tick wait.
            var mark = session.Sink.Count;
            session.RuntimeA.PublishFedOutputForTests(ch.ToString());
            await session.Cp.WaitOutputDeliveredForTestsAsync(session.PaneA);
            var echo = session.CellsOf(session.PaneA, mark);
            Assert.True(echo.Count > 0, $"key {key}: no frame for the echo read");
            Assert.Contains(ch.ToString(), RowText(echo[^1]), StringComparison.Ordinal);
            Assert.Equal(2 + typed.Length, echo[^1].GetProperty("cursor").GetProperty("col").GetInt32());

            // Read 2: the shell redisplays the line with a new style.
            mark = session.Sink.Count;
            session.RuntimeA.PublishFedOutputForTests("\r$ \u001b[1m" + typed + "\u001b[0m");
            await session.Cp.WaitOutputDeliveredForTestsAsync(session.PaneA);
            Assert.True(
                session.CellsOf(session.PaneA, mark).Count > 0,
                $"key {key}: no frame for the redisplay read");

            // Pane B writes bulk output while the clock moves to the next key.
            for (var ms = 0; ms < KeyGapMs; ms += StepMs)
            {
                session.RuntimeB.PublishFedOutputForTests($"bulk line {bulkLine++}\r\n");
                await session.Cp.WaitOutputDeliveredForTestsAsync(session.PaneB);
                await session.AdvanceAsync(StepMs);
                var now = session.Time.GetUtcNow();
                var count = session.CellsOf(session.PaneB, bulkMark).Count;
                while (bulkFrames.Count < count)
                    bulkFrames.Add(now);
            }
        }

        // Pane B is not in an echo window: at most one frame for each render
        // tick. A frame is seen up to one step after it posts.
        var elapsed = session.Time.GetUtcNow() - start;
        Assert.NotEmpty(bulkFrames);
        Assert.True(
            bulkFrames.Count <= (int)(elapsed / PaneRenderCoalescer.RenderTick) + 1,
            $"{bulkFrames.Count} bulk frames in {elapsed.TotalMilliseconds} ms");
        for (var i = 1; i < bulkFrames.Count; i++)
        {
            var gap = bulkFrames[i] - bulkFrames[i - 1];
            Assert.True(
                gap >= PaneRenderCoalescer.RenderTick - TimeSpan.FromMilliseconds(StepMs),
                $"bulk frame {i} came {gap.TotalMilliseconds} ms after the frame before it");
        }
    }

    private static string RowText(JsonElement body)
    {
        var text = new StringBuilder();
        if (!body.TryGetProperty("rows", out var rows))
            return "";
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        }

        return text.ToString();
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private async Task<Session> OpenAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-fast-typing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("fast-typing"));
        state.UpdateSession(s => s with { Name = "fast-typing", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: new DroppedInputPtyFactory(TestPaneFactories.ProcessIo()),
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
            timeProvider: time);
        var session = new Session(dir, paths, cp, new LaneSink("fast-typing"), time);
        try
        {
            session.PaneA = await CreatePaneAsync(cp, dir);
            session.PaneB = await SplitPaneAsync(cp, session.PaneA, dir);
            session.RuntimeA = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneA));
            session.RuntimeB = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneB));
            await session.ObserveAsync();
            await Poll.UntilAsync(
                () => session.CellsOf(session.PaneA, 0).Count > 0 && session.CellsOf(session.PaneB, 0).Count > 0,
                "first frames of both panes");
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

    private sealed class Session(
        string dir,
        RuntimeStatePaths paths,
        ControlPlaneService cp,
        LaneSink sink,
        ManualTimeProvider time)
        : IAsyncDisposable
    {
        private string? _lease;

        public ControlPlaneService Cp { get; } = cp;

        public LaneSink Sink { get; } = sink;

        public ManualTimeProvider Time { get; } = time;

        public string PaneA { get; set; } = "";

        public string PaneB { get; set; } = "";

        public PaneRuntime RuntimeA { get; set; } = null!;

        public PaneRuntime RuntimeB { get; set; } = null!;

        public async Task PaintAsync(string paneId)
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(paneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(paneId);
        }

        /// <summary>
        /// Move the clock. Wait for the paint of a coalescer tick that falls
        /// inside the move, and for its delivery.
        /// </summary>
        public async Task AdvanceAsync(int ms)
        {
            Time.Advance(TimeSpan.FromMilliseconds(ms));
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneA);
            await Cp.WaitOutputDeliveredForTestsAsync(PaneB);
        }

        /// <summary>Sends <paramref name="text"/> to pane A as keyboard input of this connection.</summary>
        public async Task TypeAsync(string paneId, string text)
        {
            _lease ??= await ClaimInputAsync(paneId);
            await Cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                Json(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["lease_id"] = _lease,
                    ["encoding"] = "utf8",
                    ["data"] = text,
                }),
                Sink,
                CancellationToken.None);
        }

        private async Task<string> ClaimInputAsync(string paneId)
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
            Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
            return claimed.GetProperty("lease_id").GetString()!;
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

    /// <summary>
    /// A process-io PTY whose input write completes at once and drops the
    /// bytes. The child still runs, but it never sees the keys, so it writes
    /// no echo of its own.
    /// </summary>
    private sealed class DroppedInputPtyFactory(IPtyProcessFactory inner) : IPtyProcessFactory
    {
        public PtyProviderOptions Options => inner.Options;

        public IPtyProcess Spawn(
            string fileName,
            IReadOnlyList<string> args,
            string cwd,
            int cols,
            int rows,
            IReadOnlyDictionary<string, string>? env = null) =>
            new DroppedInputPty(inner.Spawn(fileName, args, cwd, cols, rows, env));
    }

    private sealed class DroppedInputPty(IPtyProcess inner) : IPtyProcess
    {
        private readonly DroppedInputStream _input = new();

        public int Pid => inner.Pid;

        public bool IsRunning => inner.IsRunning;

        public int? ExitCode => inner.ExitCode;

        public Stream StandardInput => _input;

        public Stream StandardOutput => inner.StandardOutput;

        public void Resize(int cols, int rows) => inner.Resize(cols, rows);

        public Task WaitForExitAsync(CancellationToken ct) => inner.WaitForExitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            _input.Dispose();
            await inner.DisposeAsync();
        }
    }

    private sealed class DroppedInputStream : Stream, IPtyInputProgress
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask<int> WriteAcceptedAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            ValueTask.FromResult(data.Length);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
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
