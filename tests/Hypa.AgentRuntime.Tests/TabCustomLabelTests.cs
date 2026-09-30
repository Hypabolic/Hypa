using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class TabCustomLabelTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public TabCustomLabelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-tab-label-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public void Create_and_rename_carry_custom_label_without_guessing_text()
    {
        var state = new AppState(SessionId.New("tab-label"));
        var ws = state.CreateWorkspace("/tmp/ws");
        var bootstrap = state.ListTabs(ws.Id)[0];
        Assert.Equal("main", bootstrap.Label);
        Assert.False(bootstrap.CustomLabel);

        var auto = state.CreateTab(ws.Id);
        Assert.StartsWith("tab-", auto.Label, StringComparison.Ordinal);
        Assert.False(auto.CustomLabel);

        var named = state.CreateTab(ws.Id, "main");
        Assert.Equal("main", named.Label);
        Assert.True(named.CustomLabel);

        var renamed = state.RenameTab(auto.Id, "main");
        Assert.NotNull(renamed);
        Assert.Equal("main", renamed!.Label);
        Assert.True(renamed.CustomLabel);
    }

    [Fact]
    public async Task Persistence_round_trips_custom_label()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var store = new SqliteRuntimeSessionStore(_paths);
        var sessionId = SessionId.New("tab-label-store");
        var wsId = WorkspaceId.New();
        var autoId = TabId.New();
        var namedId = TabId.New();
        var original = new SessionState
        {
            Id = sessionId,
            Name = "tab-label-store",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            StartedAt = DateTimeOffset.Parse("2026-09-08T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-09-08T00:01:00Z"),
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp/ws",
                    FocusedTabId = namedId,
                    TabIds = [autoId, namedId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [autoId.Value] = new TabState
                {
                    Id = autoId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    CustomLabel = false,
                },
                [namedId.Value] = new TabState
                {
                    Id = namedId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 1,
                    CustomLabel = true,
                },
            },
            Panes = new Dictionary<string, PaneState>(),
        };

        Assert.True((await store.SaveAsync(original)).IsOk);
        var load = await store.TryLoadAsync("tab-label-store");
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        Assert.False(load.Value!.Tabs[autoId.Value].CustomLabel);
        Assert.True(load.Value.Tabs[namedId.Value].CustomLabel);
        Assert.Equal("main", load.Value.Tabs[autoId.Value].Label);
        Assert.Equal("main", load.Value.Tabs[namedId.Value].Label);
    }

    [Fact]
    public async Task Migrator_adds_custom_label_on_tabs()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(tabs)";
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            names.Add(reader.GetString(1));
        Assert.Contains("custom_label", names);
    }

    [Fact]
    public void Tab_result_serializes_custom_label()
    {
        var parsed = JsonSerializer.Deserialize(
            """{"tab_id":"t1","label":"editor","custom_label":true,"zoomed":false}""",
            ProtocolJsonContext.Default.TabResult);
        Assert.NotNull(parsed);
        Assert.True(parsed!.CustomLabel);

        var encoded = JsonSerializer.Serialize(
            new TabResult { TabId = "t1", Label = "editor", CustomLabel = true, Zoomed = false },
            ProtocolJsonContext.Default.TabResult);
        Assert.Contains("\"custom_label\":true", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tab_list_protocol_carries_create_and_rename_flags()
    {
        var state = new AppState(SessionId.New("tab-list-label"));
        var ws = state.CreateWorkspace("/tmp/ws");
        var named = state.CreateTab(ws.Id, "build");
        var auto = state.ListTabs(ws.Id).First(tab => !tab.CustomLabel);
        _ = state.RenameTab(auto.Id, "main");
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector());
        var listed = await cp.HandleTabListAsync(new TabListParams(), CancellationToken.None);
        Assert.Equal(JsonValueKind.Array, listed.ValueKind);
        var byId = listed.EnumerateArray().ToDictionary(
            item => item.GetProperty("tab_id").GetString()!,
            item => item,
            StringComparer.Ordinal);
        Assert.True(byId[named.Id.Value].GetProperty("custom_label").GetBoolean());
        Assert.True(byId[auto.Id.Value].GetProperty("custom_label").GetBoolean());
        Assert.Equal("build", byId[named.Id.Value].GetProperty("label").GetString());
        Assert.Equal("main", byId[auto.Id.Value].GetProperty("label").GetString());
    }
}
