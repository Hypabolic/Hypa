using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// <c>runtime.lease.claim</c> replies after the reserve and publish of its
/// <c>lease.changed</c> records. It does not wait for the journal commit, which one
/// queue serves for every reliable event. The journal here blocks each
/// <c>lease.changed</c> commit until the test opens it, so no disk timing is involved.
/// </summary>
public sealed class LeaseClaimCommitOffPathTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public LeaseClaimCommitOffPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-lease-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task A_claim_replies_while_its_commit_is_blocked()
    {
        await using var fixture = await NewFixtureAsync();

        var claim = await fixture.ClaimAsync(new Connection("c_a"), takeover: false).WaitAsync(Poll.HangGuard);

        Assert.Equal("granted", claim.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(claim.GetProperty("lease_id").GetString()));
        // The reply came first: the commit of the grant is still held.
        Assert.Single(fixture.Journal.LeaseReserved);
        Assert.Equal(0, fixture.Journal.LeaseCommitsReturned);
    }

    [Fact]
    public async Task Two_claims_in_a_row_commit_in_reservation_order()
    {
        await using var fixture = await NewFixtureAsync();

        var first = await fixture.ClaimAsync(new Connection("c_a"), takeover: false).WaitAsync(Poll.HangGuard);
        var second = await fixture.ClaimAsync(new Connection("c_b"), takeover: true).WaitAsync(Poll.HangGuard);
        Assert.Equal("granted", first.GetProperty("outcome").GetString());
        Assert.Equal("granted", second.GetProperty("outcome").GetString());
        Assert.Equal(0, fixture.Journal.LeaseCommitsReturned);

        // Grant A, then the takeover release of A and the grant of B.
        var reserved = fixture.Journal.LeaseReserved;
        Assert.Equal(3, reserved.Count);
        Assert.Equal(reserved.OrderBy(s => s), reserved);
        Assert.Equal(reserved, fixture.Journal.LeaseCommitReads);

        fixture.Journal.OpenLeaseCommits();
        await Poll.UntilAsync(
            () => fixture.Journal.LeaseCommitsReturned == 3,
            "the three lease commits to return");

        var range = await fixture.Journal.ReadRangeAsync(0, classes: null, budget: 200);
        Assert.True(range.IsOk);
        var stored = range.Value.Where(r => r.Type == ProtocolEventTypes.LeaseChanged).ToList();
        Assert.Equal(reserved, stored.Select(r => r.Seq));
        Assert.Equal(
            new[] { "grant", "takeover_release", "takeover_grant" },
            stored.Select(r => PolicyDecision(r.PayloadJson)).ToArray());
    }

    [Fact]
    public async Task A_failed_commit_is_logged_and_the_claim_still_succeeds()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Journal.FailLeaseCommits = true;

        var claim = await fixture.ClaimAsync(new Connection("c_a"), takeover: false).WaitAsync(Poll.HangGuard);
        Assert.Equal("granted", claim.GetProperty("outcome").GetString());

        fixture.Journal.OpenLeaseCommits();
        await Poll.UntilAsync(
            () => fixture.Logger.Entries.Any(e => e.Message.Contains("journal commit failed", StringComparison.Ordinal)),
            "the commit failure log");
        var (level, message) = fixture.Logger.Entries
            .Single(e => e.Message.Contains("journal commit failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(ProtocolEventTypes.LeaseChanged, message, StringComparison.Ordinal);
        Assert.Contains(fixture.Journal.LeaseReserved.Single().ToString(), message, StringComparison.Ordinal);
    }

    private static string PolicyDecision(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("policy_decision").GetString() ?? "";
    }

    private async Task<Fixture> NewFixtureAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("lease-commit"));
        state.UpdateSession(s => s with { Name = "lease-commit", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(_dir, label: "default");
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var inner = new FileRuntimeEventJournal(_paths, new SqliteJournalManifestStore(_paths), state.SessionId.Value);
        Assert.True((await inner.RecoverAsync()).IsOk);
        var journal = new LeaseGatedJournal(inner);
        var logger = new CapturingLogger();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            logger: logger,
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var create = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject { ["command"] = "echo", ["cwd"] = _dir }.ToJsonString()).RootElement,
            CancellationToken.None);
        return new Fixture(cp, journal, inner, logger, create.GetProperty("pane_id").GetString()!);
    }

    private sealed class Fixture(
        ControlPlaneService cp,
        LeaseGatedJournal journal,
        FileRuntimeEventJournal inner,
        CapturingLogger logger,
        string paneId) : IAsyncDisposable
    {
        public LeaseGatedJournal Journal { get; } = journal;

        public CapturingLogger Logger { get; } = logger;

        public Task<JsonElement> ClaimAsync(IClientConnection connection, bool takeover) =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = LeaseScopes.Input,
                    ["takeover"] = takeover,
                }.ToJsonString()).RootElement,
                connection,
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            // An emit that still waits on a held commit would stall shutdown.
            Journal.OpenLeaseCommits();
            await cp.ShutdownAsync(CancellationToken.None);
            await inner.DisposeAsync();
        }
    }

    /// <summary>
    /// The file journal, with each <c>lease.changed</c> commit result held until
    /// <see cref="OpenLeaseCommits"/>. Other records commit as usual.
    /// The real disk write still runs in reservation order.
    /// </summary>
    private sealed class LeaseGatedJournal(FileRuntimeEventJournal inner) : IRuntimeEventJournal
    {
        private readonly object _gate = new();
        private readonly List<long> _reserved = [];
        private readonly List<long> _commitReads = [];
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _commitsReturned;

        public bool FailLeaseCommits { get; set; }

        public IReadOnlyList<long> LeaseReserved
        {
            get
            {
                lock (_gate)
                    return _reserved.ToArray();
            }
        }

        public IReadOnlyList<long> LeaseCommitReads
        {
            get
            {
                lock (_gate)
                    return _commitReads.ToArray();
            }
        }

        public int LeaseCommitsReturned => Volatile.Read(ref _commitsReturned);

        public void OpenLeaseCommits() => _open.TrySetResult();

        public RuntimeEventRecord? ReserveReliable(
            EventClass @class,
            string type,
            string payloadJson,
            DateTimeOffset occurredAt)
        {
            var record = inner.ReserveReliable(@class, type, payloadJson, occurredAt);
            if (record is not null && type == ProtocolEventTypes.LeaseChanged)
            {
                lock (_gate)
                    _reserved.Add(record.Seq);
            }

            return record;
        }

        public Task<RuntimeResult<RuntimeUnit>> CommitReservedAsync(
            RuntimeEventRecord record,
            CancellationToken ct = default)
        {
            if (record.Type != ProtocolEventTypes.LeaseChanged)
                return inner.CommitReservedAsync(record, ct);

            lock (_gate)
                _commitReads.Add(record.Seq);
            return HeldAsync();

            async Task<RuntimeResult<RuntimeUnit>> HeldAsync()
            {
                var result = await inner.CommitReservedAsync(record, ct);
                await _open.Task;
                Interlocked.Increment(ref _commitsReturned);
                return FailLeaseCommits
                    ? RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io("held commit failed"))
                    : result;
            }
        }

        public Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default) =>
            inner.RecoverAsync(ct);

        public Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
            EventClass @class,
            EventReliability reliability,
            string type,
            string payloadJson,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default) =>
            inner.AppendAsync(@class, reliability, type, payloadJson, occurredAt, ct);

        public Task<RuntimeResult<RuntimeEventRecord>> AppendUtf8Async(
            EventClass @class,
            EventReliability reliability,
            string type,
            ReadOnlyMemory<byte> payloadUtf8,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default) =>
            inner.AppendUtf8Async(@class, reliability, type, payloadUtf8, occurredAt, ct);

        public Task<RuntimeResult<RuntimeUnit>> FlushManifestAsync(CancellationToken ct = default) =>
            inner.FlushManifestAsync(ct);

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
            long fromSeqExclusive,
            IReadOnlySet<EventClass>? classes,
            int budget,
            CancellationToken ct = default) =>
            inner.ReadRangeAsync(fromSeqExclusive, classes, budget, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default) =>
            inner.CloseOpenSegmentAsync(ct);

        public JournalHealth GetHealth() => inner.GetHealth();

        public long NextSeq => inner.NextSeq;

        public void RunExclusive(Action action) => inner.RunExclusive(action);
    }

    private sealed class Connection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
