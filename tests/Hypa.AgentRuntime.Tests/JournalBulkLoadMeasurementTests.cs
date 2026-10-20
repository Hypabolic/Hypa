using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A measurement on the production journal sync (<c>F_FULLFSYNC</c> on macOS,
/// <c>fsync</c> on Linux). A load task reserves reliable
/// <c>pane.scroll_changed</c> records at the rate of panes with bulk output.
/// A probe task calls <c>runtime.lease.claim</c> and appends a reliable record
/// that waits for its commit, four times each second.
/// <para>
/// It runs only when <c>HYPA_JOURNAL_LOAD_MEASURE=1</c>, because it uses real
/// disk syncs and wall time. <c>HYPA_JOURNAL_LOAD_SECONDS</c> (default 60) and
/// <c>HYPA_JOURNAL_LOAD_RATE</c> (records each second, default 50) set the load.
/// One pane that follows its output made 45 to 52 records each second before
/// the scroll records were coalesced, and makes 10 each second now.
/// </para>
/// </summary>
public sealed class JournalBulkLoadMeasurementTests(ITestOutputHelper output) : IDisposable
{
    private const string OptIn = "HYPA_JOURNAL_LOAD_MEASURE";
    private static readonly TimeSpan ClaimBound = TimeSpan.FromMilliseconds(100);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hypa-journal-load-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        var paths = new RuntimeStatePaths { StateDirectory = _dir };
        SqliteTestCleanup.ReleaseAndDelete(_dir, paths.DatabasePath);
    }

    [SkippableFact]
    public async Task Lease_claims_stay_under_the_bound_while_the_journal_commits_a_bulk_load()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(OptIn) == "1", $"set {OptIn}=1 to run this measurement");
        var seconds = ReadInt("HYPA_JOURNAL_LOAD_SECONDS", 60);
        var rate = ReadInt("HYPA_JOURNAL_LOAD_RATE", 50);

        Directory.CreateDirectory(_dir);
        var paths = new RuntimeStatePaths { StateDirectory = _dir };
        Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("journal-load"));
        state.UpdateSession(s => s with { Name = "journal-load", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var sync = new CountingSync(DurableFileSync.Production);
        var journal = new FileRuntimeEventJournal(
            paths, new SqliteJournalManifestStore(paths), state.SessionId.Value, durableSync: sync);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var created = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject { ["command"] = "echo", ["cwd"] = _dir }),
            CancellationToken.None);
        var paneId = created.GetProperty("pane_id").GetString()!;
        var startSeq = journal.NextSeq;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var loadRecords = 0;
        var load = Task.Run(async () =>
        {
            // Bursts each 100 ms, as frames arrive from a busy pane.
            var perTick = Math.Max(1, rate / 10);
            var clock = Stopwatch.StartNew();
            var tick = 0;
            while (!stop.IsCancellationRequested)
            {
                for (var i = 0; i < perTick; i++)
                {
                    var payload = "{\"pane_id\":\"" + paneId + "\",\"offset\":0,\"max_offset\":" + (loadRecords + 1)
                        + ",\"rows\":50}";
                    if (journal.ReserveReliable(
                            EventClass.Lifecycle, ProtocolEventTypes.PaneScrollChanged, payload, DateTimeOffset.UtcNow)
                        is not null)
                    {
                        loadRecords++;
                    }
                }

                tick++;
                await Pace.For(TimeSpan.FromMilliseconds(100 * tick) - clock.Elapsed).ConfigureAwait(false);
            }
        });

        var claims = new List<double>();
        var commits = new List<double>();
        // Two clients take the lease from each other, so each claim is a new grant.
        var connections = new[] { new Connection("c_load_a"), new Connection("c_load_b") };
        while (!stop.IsCancellationRequested)
        {
            var claimClock = Stopwatch.StartNew();
            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = LeaseScopes.Input,
                    ["takeover"] = true,
                }),
                connections[claims.Count % 2],
                CancellationToken.None);
            claims.Add(claimClock.Elapsed.TotalMilliseconds);
            Assert.Equal("granted", claim.GetProperty("outcome").GetString());

            var commitClock = Stopwatch.StartNew();
            var appended = await journal.AppendAsync(
                EventClass.Control, EventReliability.Reliable, ProtocolEventTypes.LeaseChanged, "{\"probe\":true}");
            commits.Add(commitClock.Elapsed.TotalMilliseconds);
            Assert.True(appended.IsOk, appended.IsOk ? null : appended.Error.Message);
            await Pace.For(250);
        }

        await load;
        var drainClock = Stopwatch.StartNew();
        var last = await journal.AppendAsync(
            EventClass.Control, EventReliability.Reliable, ProtocolEventTypes.LeaseChanged, "{\"probe\":\"last\"}");
        Assert.True(last.IsOk, last.IsOk ? null : last.Error.Message);
        var drainMs = drainClock.Elapsed.TotalMilliseconds;
        var reliable = journal.NextSeq - startSeq;
        await cp.ShutdownAsync(CancellationToken.None);
        var disposeClock = Stopwatch.StartNew();
        await journal.DisposeAsync();
        var disposeMs = disposeClock.Elapsed.TotalMilliseconds;

        output.WriteLine(
            $"sync={DurableFileSync.FileSyncKind} seconds={seconds} rate={rate}/s load_records={loadRecords} "
            + $"reliable_records={reliable} syncs={sync.Calls} records_per_sync={(double)reliable / Math.Max(1, sync.Calls):F1} "
            + $"sync_ms_sum={sync.TotalMs:F0} sync_ms_max={sync.MaxMs:F1}");
        output.WriteLine("lease.claim ms " + Summary(claims));
        output.WriteLine("awaited commit ms " + Summary(commits));
        output.WriteLine($"drain after load ms={drainMs:F1} dispose ms={disposeMs:F1}");

        Assert.True(
            claims.Max() <= ClaimBound.TotalMilliseconds,
            $"a lease claim took {claims.Max():F1} ms under the load");
    }

    private static string Summary(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return $"n={sorted.Count} p50={At(0.50):F1} p99={At(0.99):F1} max={sorted[^1]:F1}";
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static JsonElement Json(JsonObject obj) => JsonDocument.Parse(obj.ToJsonString()).RootElement;

    /// <summary>The production sync, with a count and the time of each call.</summary>
    private sealed class CountingSync(IDurableFileSync inner) : IDurableFileSync
    {
        private readonly object _gate = new();

        public int Calls { get; private set; }

        public double TotalMs { get; private set; }

        public double MaxMs { get; private set; }

        public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
        {
            var clock = Stopwatch.StartNew();
            inner.SyncFileThenDirectory(fileHandle, filePath);
            var ms = clock.Elapsed.TotalMilliseconds;
            lock (_gate)
            {
                Calls++;
                TotalMs += ms;
                MaxMs = Math.Max(MaxMs, ms);
            }
        }
    }

    private sealed class Connection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
