using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.AgentIntelligence;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// In-process (no socket) checks that runtime.health surfaces journal allocator state.
/// </summary>
public class RuntimeHealthJournalTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public RuntimeHealthJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h03-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try
        {
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Health_next_seq_tracks_journal_after_appends()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("health"));
        state.UpdateSession(s => s with { Name = "health", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        var save = await store.SaveAsync(state.Snapshot());
        Assert.True(save.IsOk, save.IsOk ? null : save.Error.Message);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        var recovered = await journal.RecoverAsync();
        Assert.True(recovered.IsOk, recovered.IsOk ? null : recovered.Error.Message);

        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);

        var h0 = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        Assert.True(h0.GetProperty("ready").GetBoolean());
        // 1-based allocator: exclusive from_seq=0 means full history.
        Assert.Equal(1, h0.GetProperty("journal").GetProperty("next_seq").GetInt64());
        Assert.True(h0.GetProperty("journal").GetProperty("replay_complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, h0.GetProperty("journal").GetProperty("replay_error").ValueKind);
        var caps = h0.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains(ProtocolCapabilities.Leases, caps);
        Assert.Contains(ProtocolCapabilities.Events, caps);
        Assert.Contains(ProtocolCapabilities.Binding, caps);
        // In-process tests inject no Ghostty probe; provider is still ghostty.
        var vt = h0.GetProperty("vt");
        Assert.Equal(VtFloorDefaults.Provider, vt.GetProperty("provider").GetString());
        Assert.False(vt.TryGetProperty("ghostty_version", out _));
        Assert.False(vt.TryGetProperty("fallback_reason", out _));

        await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, """{"pane_id":"p1"}""");
        await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, """{"pane_id":"p1"}""");

        // Control plane SyncJournalFlags is on emit path; health reads journal directly.
        var h1 = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        Assert.Equal(3, h1.GetProperty("journal").GetProperty("next_seq").GetInt64());
        Assert.True(h1.GetProperty("journal").GetProperty("replay_complete").GetBoolean());
        Assert.True(h1.GetProperty("journal").GetProperty("bytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, h1.GetProperty("journal").GetProperty("replay_error").ValueKind);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Health_exposes_replay_error_when_recovery_incomplete()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("health-err"));
        state.UpdateSession(s => s with { Name = "health-err", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.PaneLifecycle, """{"pane_id":"p1"}""")).IsOk);

        var open = await manifests.GetOpenSegmentAsync(state.SessionId.Value);
        Assert.True(open.IsOk);
        Assert.NotNull(open.Value);
        var abs = Path.Combine(_dir, open.Value!.RelativePath);
        var snapshot = await File.ReadAllBytesAsync(abs);
        await journal.DisposeAsync();

        // Crash image: complete records, no footer + incomplete tail.
        await File.WriteAllBytesAsync(abs, snapshot);
        await using (var fs = new FileStream(abs, FileMode.Append, FileAccess.Write))
        {
            Span<byte> bad = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bad, 500);
            fs.Write(bad);
            fs.Write(new byte[] { 1, 2, 3 });
        }

        await manifests.UpsertSegmentAsync(open.Value! with
        {
            Closed = false,
            ChecksumSha256 = null,
            ClosedAt = null,
        });

        var journal2 = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        var recovered = await journal2.RecoverAsync();
        Assert.True(recovered.IsOk);
        Assert.False(recovered.Value.ReplayComplete);
        Assert.False(string.IsNullOrEmpty(recovered.Value.ReplayError));

        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal2,
            subscriptions: hub);

        var health = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        var j = health.GetProperty("journal");
        Assert.False(j.GetProperty("replay_complete").GetBoolean());
        Assert.Equal(JsonValueKind.String, j.GetProperty("replay_error").ValueKind);
        Assert.False(string.IsNullOrEmpty(j.GetProperty("replay_error").GetString()));
        Assert.False(health.GetProperty("ready").GetBoolean());

        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task Health_reports_hypa_pty_host_on_unix_default_path()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("health-pty-default"));
        state.UpdateSession(s => s with { Name = "health-pty-default", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var previous = Environment.GetEnvironmentVariable("HYPA_PTY_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", null);

            // Production factory (FromEnvironment). Health must follow the factory.
            var factory = new PaneRuntimeFactory(new PaneIntelligencePipeline());
            var cp = new ControlPlaneService(
                state,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: new EventSubscriptionHub());

            var health = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
            var pty = health.GetProperty("pty");
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("process-io", pty.GetProperty("provider").GetString());
                Assert.False(pty.GetProperty("interactive").GetBoolean());
            }
            else
            {
                Assert.Equal("hypa-pty-host", pty.GetProperty("provider").GetString());
                Assert.True(pty.GetProperty("interactive").GetBoolean());
            }

            var vt = health.GetProperty("vt");
            Assert.Equal(VtFloorDefaults.Provider, vt.GetProperty("provider").GetString());
            Assert.Equal(VtFloorDefaults.DefaultCols, vt.GetProperty("cols").GetInt32());
            Assert.Equal(VtFloorDefaults.DefaultRows, vt.GetProperty("rows").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_PTY_PROVIDER", previous);
            await journal.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_reports_injected_hypa_pty_host_selection()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("health-pty-host"));
        state.UpdateSession(s => s with { Name = "health-pty-host", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var opts = new PtyProviderOptions { Provider = PtyProviderKind.HypaPtyHost };
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: new PtyProcessFactory(opts));

        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            ptyProvider: opts.ProviderWireName,
            ptyInteractive: opts.Interactive);

        var health = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        var pty = health.GetProperty("pty");
        Assert.Equal("hypa-pty-host", pty.GetProperty("provider").GetString());
        Assert.True(pty.GetProperty("interactive").GetBoolean());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Health_ready_is_false_when_shutting_down()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("health-down"));
        state.UpdateSession(s => s with { Name = "health-down", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        Assert.True(journal.GetHealth().ReplayComplete);

        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());

        var before = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        Assert.True(before.GetProperty("ready").GetBoolean());

        await cp.ShutdownAsync(CancellationToken.None);
        var after = await cp.DispatchAsync(ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        Assert.False(after.GetProperty("ready").GetBoolean());

        await journal.DisposeAsync();
    }
}
