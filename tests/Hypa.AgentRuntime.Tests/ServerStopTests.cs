using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

[Collection("ProcessSpawnTests")]
public sealed class ServerStopTests : IDisposable
{
    private readonly string _dir;
    private readonly string _socketPath;
    private readonly RuntimeStatePaths _paths;

    public ServerStopTests()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _dir = Path.Combine(Path.GetTempPath(), "h33-stop-" + id);
        Directory.CreateDirectory(_dir);
        _socketPath = Path.Combine(_dir, "s.sock");
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try { SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Server_stop_shuts_host_after_rpc_result()
    {
        var host = new RecordingHostStop();
        var (server, cp) = await StartAsync(host);
        await using var _ = server;
        await using var client = new ControlPlaneClient(_socketPath);
        await client.ConnectAsync();

        var result = await client.CallAsync(ProtocolMethods.ServerStop);
        Assert.True(result.GetProperty("ok").GetBoolean());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (host.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(1, host.Count);
        Assert.True(cp.GetType().GetField(
                "_shuttingDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            is not null);
    }

    [Fact]
    public async Task Detach_unsubscribe_and_socket_close_do_not_call_server_stop()
    {
        var host = new RecordingHostStop();
        var (server, _) = await StartAsync(host);
        await using var _ = server;
        await using var client = new ControlPlaneClient(_socketPath);
        await client.ConnectAsync();

        var sub = await client.CallAsync(
            ProtocolMethods.EventsSubscribe,
            new JsonObject { ["types"] = new JsonArray("control"), ["live"] = true, ["from_seq"] = 0 });
        var subId = sub.GetProperty("subscription_id").GetString();
        await client.CallAsync(
            ProtocolMethods.EventsUnsubscribe,
            new JsonObject { ["subscription_id"] = subId });
        await client.DisposeAsync();

        await Task.Delay(80);
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public async Task In_process_dispatch_stops_after_result()
    {
        var host = new RecordingHostStop();
        var (_, cp) = await StartAsync(host);
        var result = await cp.DispatchAsync(ProtocolMethods.ServerStop, parameters: null, CancellationToken.None);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, host.Count);
    }

    private async Task<(UnixSocketServer Server, ControlPlaneService Cp)> StartAsync(IRuntimeHostStop host)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("h33stop"));
        state.UpdateSession(s => s with { Name = "h33stop", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub,
            hostStop: host);
        var server = new UnixSocketServer(cp, _socketPath);
        await server.StartAsync(CancellationToken.None);
        return (server, cp);
    }

    private sealed class RecordingHostStop : IRuntimeHostStop
    {
        public int Count;

        public void RequestStop() => Interlocked.Increment(ref Count);
    }
}
