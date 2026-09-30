using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class MuxRequestLogTests : IDisposable
{
    private readonly string _dir;

    public MuxRequestLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-rpc-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            SqliteTestCleanup.ReleaseAndDelete(_dir, Path.Combine(_dir, "runtime.db"));
        }
        catch
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [SkippableFact]
    public async Task Tab_focus_start_and_complete_share_request_id_and_seq()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket");
        var sock = Path.Combine(_dir, "s.sock");
        var logs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var paths = new RuntimeStatePaths { StateDirectory = _dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("rpc"));
        state.UpdateSession(s => s with { Name = "rpc", LifecycleState = SessionLifecycle.Ready });
        var ws = state.CreateWorkspace("/tmp/ws");
        var tab = state.CreateTab(ws.Id, focus: false);
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        await using var server = new UnixSocketServer(
            cp,
            sock,
            UnixSocketServerOptions.Default,
            processLog: logs,
            sessionId: state.SessionId.Value);
        await server.StartAsync(CancellationToken.None);
        await using var client = new ControlPlaneClient(sock);
        await client.ConnectAsync();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var attachLogs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = attachLogs,
            SessionName = state.SessionId.Value,
            TabId = tab.Id.Value,
            WorkspaceId = ws.Id.Value,
        };
        var port = new LoggingAttachCommandPort(new ControlPlaneAttachCommandPort(client), live);
        var focused = await port.CallAsync(
            ProtocolMethods.TabFocus,
            new JsonObject { ["tab_id"] = tab.Id.Value },
            CancellationToken.None);
        Assert.Equal(tab.Id.Value, focused.GetProperty("tab_id").GetString());

        var start = logs.Records.Single(r => r.Event == ProcessLogEvents.ApiRequestStart
            && r.Method == ProtocolMethods.TabFocus);
        var complete = logs.Records.Single(r => r.Event == ProcessLogEvents.ApiRequestComplete
            && r.Method == ProtocolMethods.TabFocus);
        Assert.Equal(start.RequestId, complete.RequestId);
        Assert.True(start.ChangesUi);
        Assert.False(string.IsNullOrWhiteSpace(start.AttachClientId));
        Assert.Equal(start.AttachClientId, complete.AttachClientId);
        Assert.True(complete.Seq is > 0);
        Assert.Equal(state.SessionId.Value, start.SessionId);

        var attachRequested = attachLogs.Records.Single(r => r.Event == ProcessLogEvents.TabRequested);
        var attachOutcome = attachLogs.Records.Single(r => r.Event == ProcessLogEvents.TabOutcome);
        Assert.Equal(start.RequestId, attachRequested.RequestId);
        Assert.Equal(start.RequestId, attachOutcome.RequestId);
        Assert.Equal(start.RequestId, client.LastRequestId);

        var range = await journal.ReadRangeAsync(0, null, 50, CancellationToken.None);
        Assert.True(range.IsOk);
        var tabLife = range.Value.Single(r => r.Type == ProtocolEventTypes.TabLifecycle);
        using var payload = JsonDocument.Parse(tabLife.PayloadJson);
        Assert.Equal(start.RequestId, payload.RootElement.GetProperty("request_id").GetString());
        Assert.Equal(complete.Seq, tabLife.Seq);

        var paneReadBefore = logs.Records.Count;
        await client.CallAsync(ProtocolMethods.PaneList, new JsonObject());
        Assert.DoesNotContain(
            logs.Records.Skip(paneReadBefore),
            r => r.Method == ProtocolMethods.PaneRead && r.Level == ProcessLogLevel.Information);
        Assert.DoesNotContain(
            logs.Records,
            r => r.Method == ProtocolMethods.PaneList && r.Level == ProcessLogLevel.Information
                && r.Event == ProcessLogEvents.ApiRequestStart);

        try
        {
            await client.CallAsync(ProtocolMethods.PaneRead, new JsonObject { ["pane_id"] = "missing" });
        }
        catch (ControlPlaneException)
        {
        }

        Assert.DoesNotContain(
            logs.Records,
            r => r.Method == ProtocolMethods.PaneRead
                && r.Level == ProcessLogLevel.Information
                && (r.Event == ProcessLogEvents.ApiRequestStart
                    || r.Event == ProcessLogEvents.ApiRequestComplete));
        Assert.Contains(
            logs.Records,
            r => r.Event == ProcessLogEvents.ApiRequestFail
                && r.Method == ProtocolMethods.PaneRead
                && r.Level == ProcessLogLevel.Warning);

        try
        {
            await client.CallAsync(ProtocolMethods.TabFocus, new JsonObject { ["tab_id"] = "missing" });
        }
        catch (ControlPlaneException)
        {
        }

        Assert.Contains(
            logs.Records,
            r => r.Event == ProcessLogEvents.ApiRequestFail
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.Method == ProtocolMethods.TabFocus
                && !string.IsNullOrWhiteSpace(r.Err)
                && !r.Err.TrimStart().StartsWith('{'));

        await cp.ShutdownAsync(CancellationToken.None);
        await hub.DisposeAsync();
        await journal.DisposeAsync();
    }

    [SkippableFact]
    public async Task Concurrent_tab_focus_stamps_each_call_request_id()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket");
        var sock = Path.Combine(_dir, "c.sock");
        var logs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var paths = new RuntimeStatePaths { StateDirectory = _dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("rpc2"));
        state.UpdateSession(s => s with { Name = "rpc2", LifecycleState = SessionLifecycle.Ready });
        var ws = state.CreateWorkspace("/tmp/ws");
        var tabA = state.CreateTab(ws.Id, focus: false);
        var tabB = state.CreateTab(ws.Id, focus: false);
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        await using var server = new UnixSocketServer(
            cp,
            sock,
            UnixSocketServerOptions.Default,
            processLog: logs,
            sessionId: state.SessionId.Value);
        await server.StartAsync(CancellationToken.None);
        await using var client = new ControlPlaneClient(sock);
        await client.ConnectAsync();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var attachLogs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = attachLogs,
            SessionName = state.SessionId.Value,
            TabId = tabA.Id.Value,
            WorkspaceId = ws.Id.Value,
        };
        var port = new LoggingAttachCommandPort(new ControlPlaneAttachCommandPort(client), live);
        await Task.WhenAll(
            port.CallAsync(
                ProtocolMethods.TabFocus,
                new JsonObject { ["tab_id"] = tabA.Id.Value },
                CancellationToken.None),
            port.CallAsync(
                ProtocolMethods.TabFocus,
                new JsonObject { ["tab_id"] = tabB.Id.Value },
                CancellationToken.None));

        var requested = attachLogs.Records
            .Where(r => r.Event == ProcessLogEvents.TabRequested && r.Action == "focus")
            .ToList();
        Assert.Equal(2, requested.Count);
        Assert.Equal(2, requested.Select(r => r.RequestId).Distinct().Count());

        var range = await journal.ReadRangeAsync(0, null, 50, CancellationToken.None);
        Assert.True(range.IsOk);
        foreach (var rec in requested)
        {
            Assert.False(string.IsNullOrWhiteSpace(rec.RequestId));
            Assert.Contains(
                logs.Records,
                r => r.Event == ProcessLogEvents.ApiRequestStart
                    && r.Method == ProtocolMethods.TabFocus
                    && r.RequestId == rec.RequestId);
            var life = range.Value.Single(r =>
            {
                if (r.Type != ProtocolEventTypes.TabLifecycle)
                    return false;
                using var doc = JsonDocument.Parse(r.PayloadJson);
                return doc.RootElement.GetProperty("tab_id").GetString() == rec.TabId
                    && doc.RootElement.GetProperty("action").GetString() == "focused"
                    && doc.RootElement.GetProperty("request_id").GetString() == rec.RequestId;
            });
            Assert.NotNull(life);
        }

        await cp.ShutdownAsync(CancellationToken.None);
        await hub.DisposeAsync();
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Disposed_client_tab_focus_still_writes_rejected_outcome()
    {
        await using var client = new ControlPlaneClient(Path.Combine(_dir, "missing.sock"));
        await client.DisposeAsync();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var attachLogs = new CapturingProcessLogSink(ProcessLogLevel.Debug);
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = attachLogs,
            SessionName = "sess",
            AttachClientId = "c1",
            TabId = "t1",
            WorkspaceId = "w1",
        };
        var port = new LoggingAttachCommandPort(new ControlPlaneAttachCommandPort(client), live);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.CallAsync(
                ProtocolMethods.TabFocus,
                new JsonObject { ["tab_id"] = "t2" },
                CancellationToken.None));
        Assert.Contains(
            attachLogs.Records,
            r => r.Event == ProcessLogEvents.TabRequested && r.Action == "focus");
        Assert.Contains(
            attachLogs.Records,
            r => r.Event == ProcessLogEvents.TabOutcome
                && r.Action == "focus"
                && r.Outcome == ProcessLogEvents.OutcomeRejected);
    }
}
