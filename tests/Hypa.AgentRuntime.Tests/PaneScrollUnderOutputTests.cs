using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A person scrolls up through a pane that writes a line every 4 ms. The
/// mux and the pane VT are real. The attach applies each <c>pane.scroll</c>
/// reply at once, and each <c>pane.scroll_changed</c> 250 ms late, as when
/// the event waits in the receive queue behind the frames of the busy pane.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class PaneScrollUnderOutputTests : IDisposable
{
    private static readonly TimeSpan EventLag = TimeSpan.FromMilliseconds(250);

    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private readonly string? _lib;

    public PaneScrollUnderOutputTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-scroll-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
        _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [SkippableFact]
    public async Task Wheel_up_over_steady_output_never_lowers_the_offset_and_the_view_keeps_its_row_after()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var (cp, journal) = await NewAsync();
        try
        {
            var created = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["command"] = "/bin/sh",
                    ["args"] = new JsonArray("-c", "exec cat"),
                    ["cwd"] = _dir,
                }),
                CancellationToken.None);
            var paneId = created.GetProperty("pane_id").GetString()!;
            var runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(paneId));
            Assert.IsType<GhosttyVtEngine>(runtime.VtEngine);
            var line = 0;
            var prime = new StringBuilder();
            for (; line < 300; line++)
                prime.Append("line ").Append(line).Append("\r\n");
            runtime.PublishFedOutputForTests(prime.ToString());

            var events = new Sink("c_attach");
            await SubscribeAsync(cp, events, journal.NextSeq - 1);
            var live = NewLive(paneId);
            var port = new DispatchPort(cp);

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    runtime.PublishFedOutputForTests("line " + Interlocked.Increment(ref line) + "\r\n");
                    await Pace.For(4).ConfigureAwait(false);
                }
            });

            var offsets = new OffsetRecorder(live, paneId);
            var delivered = 0;
            void Deliver(bool all)
            {
                foreach (var payload in events.Take(ref delivered, all ? TimeSpan.Zero : EventLag))
                {
                    using var doc = JsonDocument.Parse(payload);
                    AttachSession.TryApplyPaneScrollChanged(live, doc.RootElement);
                    offsets.Sample();
                }
            }

            var delivery = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Deliver(all: false);
                    await Pace.For(5).ConfigureAwait(false);
                }
            });
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    offsets.Sample();
                    await Pace.For(1).ConfigureAwait(false);
                }
            });

            // Each wheel step moves the view up: its top row is an older line
            // than the top row after the step before.
            var wheel = Stopwatch.StartNew();
            var steps = 0;
            var topLine = int.MaxValue;
            var viewDown = new List<string>();
            while (wheel.Elapsed < TimeSpan.FromSeconds(2))
            {
                await AttachSession.ScrollPaneHistoryAsync(live, port, paneId, 3, CancellationToken.None);
                offsets.Sample();
                steps++;
                Assert.True(runtime.TryCaptureSnapshotJson(out var view, out _));
                var top = LineNumber(TopRow(view));
                if (top > topLine)
                    viewDown.Add(topLine + " to " + top);
                topLine = top;
                await Pace.For(30);
            }

            // The wheel stopped. The output goes on for 100 more lines, and
            // the view stays on the same history row.
            Assert.True(runtime.TryCaptureSnapshotJson(out var atStop, out _));
            var linesAtStop = Volatile.Read(ref line);
            await Poll.UntilAsync(() => Volatile.Read(ref line) >= linesAtStop + 100, "100 more lines of output");
            Assert.True(runtime.TryCaptureSnapshotJson(out var later, out _));
            await stop.CancelAsync();
            await Task.WhenAll(writer, delivery, sampler);
            await Poll.UntilAsync(() => cp.ScrollEmitsAreIdleForTest, "the scroll emits to end");
            await cp.WaitReliableEmitsQuiescentForTestsAsync();
            Deliver(all: true);

            Assert.True(steps >= 30, $"{steps} wheel steps");
            Assert.True(offsets.Drops.Count == 0, "the attach offset went down: " + string.Join(", ", offsets.Drops));
            Assert.True(viewDown.Count == 0, "a wheel-up step moved the view down: " + string.Join(", ", viewDown));
            Assert.True(offsets.Last >= 3 * steps, $"offset {offsets.Last} after {steps} steps of 3 rows");
            Assert.Equal(TopRow(atStop), TopRow(later));
            Assert.StartsWith("line ", TopRow(later), StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            await journal.DisposeAsync();
        }
    }

    private async Task<(ControlPlaneService Cp, FileRuntimeEventJournal Journal)> NewAsync()
    {
        Assert.True((await new SqliteRuntimeSchemaMigrator(_paths).MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("scroll-out"));
        state.UpdateSession(s => s with { Name = "scroll-out", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var journal = new FileRuntimeEventJournal(_paths, new SqliteJournalManifestStore(_paths), state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = _lib }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        return (cp, journal);
    }

    private static async Task SubscribeAsync(ControlPlaneService cp, Sink sink, long fromSeq)
    {
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject
            {
                ["from_seq"] = fromSeq,
                ["types"] = new JsonArray(ProtocolEventTypes.PaneScrollChanged),
                ["live"] = true,
            }),
            sink,
            CancellationToken.None);
        var subId = sub.GetProperty("subscription_id").GetString()!;
        await cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
    }

    private static AttachLiveState NewLive(string paneId)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PaneId = paneId,
            SessionName = "scroll-out",
        };
    }

    private static string TopRow(string snapshotJson)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        var text = new StringBuilder();
        foreach (var cell in doc.RootElement.GetProperty("cells")[0].EnumerateArray())
        {
            if (cell.ValueKind == JsonValueKind.Object && cell.TryGetProperty("text", out var t))
                text.Append(t.GetString());
            else if (cell.ValueKind == JsonValueKind.String)
                text.Append(cell.GetString());
        }

        return text.ToString().Trim();
    }

    private static int LineNumber(string row)
    {
        Assert.StartsWith("line ", row, StringComparison.Ordinal);
        return int.Parse(row.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static JsonElement Json(JsonObject value) => JsonDocument.Parse(value.ToJsonString()).RootElement;

    /// <summary>Reads the offset that the attach stores, and keeps each drop.</summary>
    private sealed class OffsetRecorder(AttachLiveState live, string paneId)
    {
        private readonly object _gate = new();

        public List<string> Drops { get; } = [];

        public int Last { get; private set; }

        public void Sample()
        {
            lock (_gate)
            {
                live.TryGetPaneScrollMetrics(paneId, out var offset, out _, out _);
                if (offset < Last)
                    Drops.Add(Last + " to " + offset);
                Last = offset;
            }
        }
    }

    private sealed class DispatchPort(ControlPlaneService cp) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            cp.DispatchAsync(method, Json(parameters ?? []), ct);
    }

    /// <summary>A client connection that keeps each scroll payload with the time it arrived.</summary>
    private sealed class Sink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<(long At, string Payload)> _payloads = [];

        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.GetString() == ProtocolEventTypes.PaneScrollChanged)
            {
                var payload = p.GetProperty("payload");
                var json = payload.ValueKind == JsonValueKind.String ? payload.GetString()! : payload.GetRawText();
                lock (_gate)
                    _payloads.Add((Stopwatch.GetTimestamp(), json));
            }

            return Task.CompletedTask;
        }

        /// <summary>The payloads after <paramref name="next"/> that arrived at least <paramref name="age"/> ago.</summary>
        public List<string> Take(ref int next, TimeSpan age)
        {
            var taken = new List<string>();
            lock (_gate)
            {
                while (next < _payloads.Count && Stopwatch.GetElapsedTime(_payloads[next].At) >= age)
                    taken.Add(_payloads[next++].Payload);
            }

            return taken;
        }
    }
}
