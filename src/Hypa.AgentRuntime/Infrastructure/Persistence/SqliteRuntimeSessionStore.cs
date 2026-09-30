using System.Globalization;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Data.Sqlite;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// SQLite adapter for the agent-runtime session graph (Appendix A).
/// </summary>
public sealed class SqliteRuntimeSessionStore : IRuntimeSessionStore, IAsyncDisposable
{
    private readonly RuntimeStatePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteRuntimeSessionStore(RuntimeStatePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<RuntimeResult<SessionState?>> TryLoadAsync(
        string sessionName, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<SessionState?>.Ok(null);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureLayoutColumnsAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneExtrasColumnsAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneAuthorityColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneHiddenColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneParentColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneAgentNameColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureWorkspaceOrderColumnsAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureWorkspaceWorktreeColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureWorkspaceDefaultPanePendingColumnAsync(conn, ct)
                    .ConfigureAwait(false);

                await using var sessionCmd = conn.CreateCommand();
                sessionCmd.CommandText = """
                    SELECT session_id, name, lifecycle_state, placement, placement_generation,
                           started_at, updated_at, replay_complete, replay_error, focused_workspace_id,
                           binding_json, governed, process_tenant_id, process_run_id
                    FROM sessions
                    WHERE name = @name
                    LIMIT 1;
                    """;
                sessionCmd.Parameters.AddWithValue("@name", sessionName);
                await using var reader = await sessionCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return RuntimeResult<SessionState?>.Ok(null);

                var sessionId = reader.GetString(0);
                var name = reader.GetString(1);
                var lifecycle = reader.GetString(2);
                var placement = reader.GetString(3);
                var placementGen = reader.GetInt32(4);
                var startedAt = ParseTimestamp(reader.GetString(5));
                var updatedAt = ParseTimestamp(reader.GetString(6));
                var replayComplete = reader.GetInt64(7) != 0;
                var replayError = reader.IsDBNull(8) ? null : reader.GetString(8);
                var focusedWorkspaceRaw = reader.IsDBNull(9) ? null : reader.GetString(9);
                var sessionBindingJson = reader.FieldCount > 10 && !reader.IsDBNull(10)
                    ? reader.GetString(10)
                    : null;
                var governed = reader.FieldCount > 11 && !reader.IsDBNull(11) && reader.GetInt64(11) != 0;
                var processTenantId = reader.FieldCount > 12 && !reader.IsDBNull(12)
                    ? reader.GetString(12)
                    : null;
                var processRunId = reader.FieldCount > 13 && !reader.IsDBNull(13)
                    ? reader.GetString(13)
                    : null;
                await reader.DisposeAsync().ConfigureAwait(false);

                long nextSeq = 0;
                await using (var cursorCmd = conn.CreateCommand())
                {
                    cursorCmd.CommandText =
                        "SELECT next_seq FROM event_cursor WHERE session_id = @sid";
                    cursorCmd.Parameters.AddWithValue("@sid", sessionId);
                    var seqObj = await cursorCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (seqObj is long l)
                        nextSeq = l;
                    else if (seqObj is int i)
                        nextSeq = i;
                }

                var workspaces = await LoadWorkspacesAsync(conn, sessionId, ct).ConfigureAwait(false);
                var tabs = await LoadTabsAsync(conn, workspaces.Keys, ct).ConfigureAwait(false);
                var panes = await LoadPanesAsync(conn, workspaces.Keys, ct).ConfigureAwait(false);

                // Rebuild tab occupancy from pane records. Hidden ids leave every
                // on a tab that does not own the pane.
                var tabsWithPanes = new Dictionary<string, TabState>(StringComparer.Ordinal);
                foreach (var (tabId, tab) in tabs)
                    tabsWithPanes[tabId] = AppState.NormalizeTabOccupancy(tab, panes);

                foreach (var (paneId, pane) in panes.ToList())
                {
                    if (pane.Args.Count > 0)
                        continue;
                    if (!tabsWithPanes.TryGetValue(pane.TabId.Value, out var tab) || tab.LayoutRoot is null)
                        continue;
                    var leaf = LayoutTreeOperations.Leaves(tab.LayoutRoot)
                        .FirstOrDefault(l => l.PaneId?.Value == paneId);
                    if (leaf is null || leaf.Command.Count <= 1)
                        continue;
                    panes[paneId] = pane with { Args = leaf.Command.Skip(1).ToArray() };
                }

                var workspacesWithTabs = new Dictionary<string, WorkspaceState>(StringComparer.Ordinal);
                // Deterministic walk: workspace_id ascending (also used as focus fallback).
                foreach (var (wsId, ws) in workspaces.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var tabIds = tabsWithPanes.Values
                        .Where(t => t.WorkspaceId.Value == wsId)
                        .OrderBy(t => t.Ordinal)
                        .Select(t => t.Id)
                        .ToList();
                    var focusedTab = ws.FocusedTabId is { } ft && tabIds.Any(t => t.Value == ft.Value)
                        ? ft
                        : tabIds.FirstOrDefault();
                    workspacesWithTabs[wsId] = ws with
                    {
                        TabIds = tabIds,
                        FocusedTabId = focusedTab.Value is null ? null : focusedTab,
                    };
                }

                // Prefer durable focused_workspace_id when it still exists; else first by id.
                WorkspaceId? focused = null;
                if (!string.IsNullOrEmpty(focusedWorkspaceRaw) &&
                    workspacesWithTabs.ContainsKey(focusedWorkspaceRaw))
                {
                    focused = new WorkspaceId(focusedWorkspaceRaw);
                }
                else if (workspacesWithTabs.Count > 0)
                {
                    focused = workspacesWithTabs
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .First().Value.Id;
                }

                var state = new SessionState
                {
                    Id = new SessionId(sessionId),
                    Name = name,
                    LifecycleState = lifecycle,
                    Placement = placement,
                    PlacementGeneration = placementGen,
                    StartedAt = startedAt,
                    UpdatedAt = updatedAt,
                    ReplayComplete = replayComplete,
                    ReplayError = replayError,
                    NextEventSeq = nextSeq,
                    Workspaces = workspacesWithTabs,
                    Tabs = tabsWithPanes,
                    Panes = panes,
                    FocusedWorkspaceId = focused,
                    Binding = DeserializeBinding(sessionBindingJson),
                    Governed = governed,
                    ProcessTenantId = processTenantId,
                    ProcessRunId = processRunId,
                };

                return RuntimeResult<SessionState?>.Ok(state);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<SessionState?>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (JsonException ex)
        {
            var prefix = ex.Message.Contains("worktree_json", StringComparison.Ordinal)
                ? "corrupt worktree_json: "
                : "corrupt agent_authority_json: ";
            return RuntimeResult<SessionState?>.Fail(RuntimePersistenceError.Db(prefix + ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<SessionState?>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<SessionState?>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> SaveAsync(
        SessionState state,
        CancellationToken ct = default,
        bool removeMissingPanes = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _paths.EnsureDirectory();
                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureLayoutColumnsAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneExtrasColumnsAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneAuthorityColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneHiddenColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneParentColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsurePaneAgentNameColumnAsync(conn, ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.EnsureWorkspaceDefaultPanePendingColumnAsync(conn, ct)
                    .ConfigureAwait(false);

                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                await UpsertSessionAsync(conn, tx, state, ct).ConfigureAwait(false);
                // Journal (IJournalManifestStore) is the sole writer of event_cursor and
                // runtime_meta.event_seq. Graph SaveAsync must never overwrite the allocator
                // watermark — a concurrent PersistGraphAsync can snapshot a stale NextEventSeq.
                // NextEventSeq on SessionState is read-only denormalized state for inspectors.

                foreach (var ws in state.Workspaces.Values)
                    await UpsertWorkspaceAsync(conn, tx, state.Id.Value, ws, ct).ConfigureAwait(false);

                // Tabs before panes (FK).
                foreach (var tab in state.Tabs.Values.OrderBy(t => t.Ordinal))
                    await UpsertTabAsync(conn, tx, tab, ct).ConfigureAwait(false);

                foreach (var pane in state.Panes.Values)
                    await UpsertPaneAsync(conn, tx, pane, ct).ConfigureAwait(false);

                if (removeMissingPanes)
                {
                    // Flag means "sync graph deletions" (panes, tabs, workspaces).
                    // FK order: scrollback/occupants → panes → tabs → workspaces.
                    await DeleteMissingPanesAsync(conn, tx, state, ct).ConfigureAwait(false);
                    await DeleteMissingTabsAsync(conn, tx, state, ct).ConfigureAwait(false);
                    await DeleteMissingWorkspacesAsync(conn, tx, state, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    public async Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(
        PaneId paneId, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await SqliteRuntimeSchemaMigrator.ApplyPragmasAsync(conn, ct).ConfigureAwait(false);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                // scrollback_segments reference panes — remove first.
                await using (var sb = conn.CreateCommand())
                {
                    sb.Transaction = tx;
                    sb.CommandText = "DELETE FROM scrollback_segments WHERE pane_id = @id";
                    sb.Parameters.AddWithValue("@id", paneId.Value);
                    await sb.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var occ = conn.CreateCommand())
                {
                    occ.Transaction = tx;
                    occ.CommandText = "DELETE FROM occupants WHERE pane_id = @id";
                    occ.Parameters.AddWithValue("@id", paneId.Value);
                    await occ.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var pane = conn.CreateCommand())
                {
                    pane.Transaction = tx;
                    pane.CommandText = "DELETE FROM panes WHERE pane_id = @id";
                    pane.Parameters.AddWithValue("@id", paneId.Value);
                    await pane.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    public async Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_paths.DatabasePath))
                    return RuntimeResult<string>.Fail(RuntimePersistenceError.Db("runtime.db not found"));

                await using var conn = Open();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM runtime_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", RuntimePersistenceSchema.RuntimeSessionIdKey);
                var value = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(value))
                    return RuntimeResult<string>.Fail(RuntimePersistenceError.Db("runtime_session_id missing"));
                return RuntimeResult<string>.Ok(value);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (SqliteException ex)
        {
            return RuntimeResult<string>.Fail(RuntimePersistenceError.Db(ex.Message));
        }
        catch (IOException ex)
        {
            return RuntimeResult<string>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return RuntimeResult<string>.Fail(RuntimePersistenceError.Access(ex.Message));
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection Open() => new($"Data Source={_paths.DatabasePath}");

    private static async Task UpsertSessionAsync(
        SqliteConnection conn, SqliteTransaction tx, SessionState state, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sessions(
              session_id, name, lifecycle_state, placement, placement_generation,
              started_at, updated_at, replay_complete, replay_error, focused_workspace_id,
              binding_json, governed, process_tenant_id, process_run_id)
            VALUES (
              @session_id, @name, @lifecycle_state, @placement, @placement_generation,
              @started_at, @updated_at, @replay_complete, @replay_error, @focused_workspace_id,
              @binding_json, @governed, @process_tenant_id, @process_run_id)
            ON CONFLICT(session_id) DO UPDATE SET
              name = excluded.name,
              lifecycle_state = excluded.lifecycle_state,
              placement = excluded.placement,
              placement_generation = excluded.placement_generation,
              started_at = excluded.started_at,
              updated_at = excluded.updated_at,
              replay_complete = excluded.replay_complete,
              replay_error = excluded.replay_error,
              focused_workspace_id = excluded.focused_workspace_id,
              binding_json = excluded.binding_json,
              governed = excluded.governed,
              process_tenant_id = excluded.process_tenant_id,
              process_run_id = excluded.process_run_id;
            """;
        cmd.Parameters.AddWithValue("@session_id", state.Id.Value);
        cmd.Parameters.AddWithValue("@name", string.IsNullOrEmpty(state.Name) ? state.Id.Value : state.Name);
        cmd.Parameters.AddWithValue("@lifecycle_state", state.LifecycleState);
        cmd.Parameters.AddWithValue("@placement", state.Placement);
        cmd.Parameters.AddWithValue("@placement_generation", state.PlacementGeneration);
        cmd.Parameters.AddWithValue("@started_at", FormatTimestamp(state.StartedAt));
        cmd.Parameters.AddWithValue("@updated_at", FormatTimestamp(state.UpdatedAt));
        cmd.Parameters.AddWithValue("@replay_complete", state.ReplayComplete ? 1 : 0);
        cmd.Parameters.AddWithValue("@replay_error", (object?)state.ReplayError ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@focused_workspace_id",
            (object?)state.FocusedWorkspaceId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@binding_json",
            (object?)SerializeBinding(state.Binding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@governed", state.Governed ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@process_tenant_id", (object?)state.ProcessTenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@process_run_id", (object?)state.ProcessRunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertWorkspaceAsync(
        SqliteConnection conn, SqliteTransaction tx, string sessionId, WorkspaceState ws, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO workspaces(workspace_id, session_id, label, cwd, focused_tab_id, binding_json, ordinal, worktree_json, default_pane_pending)
            VALUES (@workspace_id, @session_id, @label, @cwd, @focused_tab_id, @binding_json, @ordinal, @worktree_json, @default_pane_pending)
            ON CONFLICT(workspace_id) DO UPDATE SET
              session_id = excluded.session_id,
              label = excluded.label,
              cwd = excluded.cwd,
              focused_tab_id = excluded.focused_tab_id,
              binding_json = excluded.binding_json,
              ordinal = excluded.ordinal,
              worktree_json = excluded.worktree_json,
              default_pane_pending = excluded.default_pane_pending;
            """;
        cmd.Parameters.AddWithValue("@workspace_id", ws.Id.Value);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@label", (object?)ws.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cwd", ws.Cwd);
        cmd.Parameters.AddWithValue("@focused_tab_id", (object?)ws.FocusedTabId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@binding_json", (object?)SerializeBinding(ws.Binding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ordinal", ws.Ordinal);
        cmd.Parameters.AddWithValue("@worktree_json", (object?)SerializeWorktree(ws.Worktree) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@default_pane_pending", ws.DefaultPanePending ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertTabAsync(
        SqliteConnection conn, SqliteTransaction tx, TabState tab, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tabs(tab_id, workspace_id, label, ordinal, focused_pane_id, layout_json, zoomed, zoomed_pane_id, custom_label)
            VALUES (@tab_id, @workspace_id, @label, @ordinal, @focused_pane_id, @layout_json, @zoomed, @zoomed_pane_id, @custom_label)
            ON CONFLICT(tab_id) DO UPDATE SET
              workspace_id = excluded.workspace_id,
              label = excluded.label,
              ordinal = excluded.ordinal,
              focused_pane_id = excluded.focused_pane_id,
              layout_json = excluded.layout_json,
              zoomed = excluded.zoomed,
              zoomed_pane_id = excluded.zoomed_pane_id,
              custom_label = excluded.custom_label;
            """;
        cmd.Parameters.AddWithValue("@tab_id", tab.Id.Value);
        cmd.Parameters.AddWithValue("@workspace_id", tab.WorkspaceId.Value);
        cmd.Parameters.AddWithValue("@label", (object?)tab.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ordinal", tab.Ordinal);
        cmd.Parameters.AddWithValue(
            "@focused_pane_id", (object?)tab.FocusedPaneId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "@layout_json", (object?)tab.LayoutRoot?.ToCanonicalJson(includePaneId: true) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@zoomed", tab.Zoomed ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@zoomed_pane_id", (object?)tab.ZoomedPaneId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@custom_label", tab.CustomLabel ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertPaneAsync(
        SqliteConnection conn, SqliteTransaction tx, PaneState pane, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO panes(
              pane_id, tab_id, workspace_id, label, cwd, command, args_json, cols, rows,
              lifecycle_state, occupant_generation, pid, exit_code,
              agent_kind, agent_status, binding_json, created_at, updated_at, agent_message,
              seen, right_click, agent_authority_json, hidden, parent_pane_id, agent_name)
            VALUES (
              @pane_id, @tab_id, @workspace_id, @label, @cwd, @command, @args_json, @cols, @rows,
              @lifecycle_state, @occupant_generation, @pid, @exit_code,
              @agent_kind, @agent_status, @binding_json, @created_at, @updated_at, @agent_message,
              @seen, @right_click, @agent_authority_json, @hidden, @parent_pane_id, @agent_name)
            ON CONFLICT(pane_id) DO UPDATE SET
              tab_id = excluded.tab_id,
              workspace_id = excluded.workspace_id,
              label = excluded.label,
              cwd = excluded.cwd,
              command = excluded.command,
              args_json = excluded.args_json,
              cols = excluded.cols,
              rows = excluded.rows,
              lifecycle_state = excluded.lifecycle_state,
              occupant_generation = excluded.occupant_generation,
              pid = excluded.pid,
              exit_code = excluded.exit_code,
              agent_kind = excluded.agent_kind,
              agent_status = excluded.agent_status,
              binding_json = excluded.binding_json,
              created_at = excluded.created_at,
              updated_at = excluded.updated_at,
              agent_message = excluded.agent_message,
              seen = excluded.seen,
              right_click = excluded.right_click,
              agent_authority_json = excluded.agent_authority_json,
              hidden = excluded.hidden,
              parent_pane_id = excluded.parent_pane_id,
              agent_name = excluded.agent_name;
            """;
        cmd.Parameters.AddWithValue("@pane_id", pane.Id.Value);
        cmd.Parameters.AddWithValue("@tab_id", pane.TabId.Value);
        cmd.Parameters.AddWithValue("@workspace_id", pane.WorkspaceId.Value);
        cmd.Parameters.AddWithValue("@label", pane.Label);
        cmd.Parameters.AddWithValue("@cwd", pane.Cwd);
        cmd.Parameters.AddWithValue("@command", pane.Command);
        cmd.Parameters.AddWithValue("@args_json", SerializeArgs(pane.Args));
        cmd.Parameters.AddWithValue("@cols", pane.Cols);
        cmd.Parameters.AddWithValue("@rows", pane.Rows);
        cmd.Parameters.AddWithValue("@lifecycle_state", pane.LifecycleState);
        cmd.Parameters.AddWithValue("@occupant_generation", pane.OccupantGeneration);
        cmd.Parameters.AddWithValue("@pid", (object?)pane.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@exit_code", (object?)pane.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@agent_kind", (object?)pane.AgentKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@agent_status", pane.AgentStatus.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@binding_json", (object?)SerializeBinding(pane.Binding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", FormatTimestamp(pane.CreatedAt));
        cmd.Parameters.AddWithValue("@updated_at", FormatTimestamp(pane.UpdatedAt));
        cmd.Parameters.AddWithValue("@agent_message", (object?)pane.AgentMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seen", pane.Seen ? 1 : 0);
        cmd.Parameters.AddWithValue("@right_click",
            string.IsNullOrWhiteSpace(pane.RightClick) ? PaneRightClick.Hypa : pane.RightClick);
        cmd.Parameters.AddWithValue("@agent_authority_json",
            (object?)SerializePaneAuthority(pane) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hidden", pane.Placement == PanePlacement.Hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@parent_pane_id", (object?)pane.ParentPaneId?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@agent_name", (object?)pane.AgentName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteMissingPanesAsync(
        SqliteConnection conn, SqliteTransaction tx, SessionState state, CancellationToken ct)
    {
        var keep = state.Panes.Keys.ToHashSet(StringComparer.Ordinal);
        var existing = new List<string>();
        await using (var list = conn.CreateCommand())
        {
            list.Transaction = tx;
            list.CommandText = """
                SELECT p.pane_id FROM panes p
                INNER JOIN workspaces w ON w.workspace_id = p.workspace_id
                WHERE w.session_id = @sid
                """;
            list.Parameters.AddWithValue("@sid", state.Id.Value);
            await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0));
        }

        foreach (var id in existing)
        {
            if (keep.Contains(id))
                continue;

            await using var sb = conn.CreateCommand();
            sb.Transaction = tx;
            sb.CommandText = "DELETE FROM scrollback_segments WHERE pane_id = @id";
            sb.Parameters.AddWithValue("@id", id);
            await sb.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var occ = conn.CreateCommand();
            occ.Transaction = tx;
            occ.CommandText = "DELETE FROM occupants WHERE pane_id = @id";
            occ.Parameters.AddWithValue("@id", id);
            await occ.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var pane = conn.CreateCommand();
            pane.Transaction = tx;
            pane.CommandText = "DELETE FROM panes WHERE pane_id = @id";
            pane.Parameters.AddWithValue("@id", id);
            await pane.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteMissingTabsAsync(
        SqliteConnection conn, SqliteTransaction tx, SessionState state, CancellationToken ct)
    {
        var keep = state.Tabs.Keys.ToHashSet(StringComparer.Ordinal);
        var existing = new List<string>();
        await using (var list = conn.CreateCommand())
        {
            list.Transaction = tx;
            list.CommandText = """
                SELECT t.tab_id FROM tabs t
                INNER JOIN workspaces w ON w.workspace_id = t.workspace_id
                WHERE w.session_id = @sid
                """;
            list.Parameters.AddWithValue("@sid", state.Id.Value);
            await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0));
        }

        foreach (var id in existing)
        {
            if (keep.Contains(id))
                continue;

            await using var tab = conn.CreateCommand();
            tab.Transaction = tx;
            tab.CommandText = "DELETE FROM tabs WHERE tab_id = @id";
            tab.Parameters.AddWithValue("@id", id);
            await tab.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteMissingWorkspacesAsync(
        SqliteConnection conn, SqliteTransaction tx, SessionState state, CancellationToken ct)
    {
        var keep = state.Workspaces.Keys.ToHashSet(StringComparer.Ordinal);
        var existing = new List<string>();
        await using (var list = conn.CreateCommand())
        {
            list.Transaction = tx;
            list.CommandText = """
                SELECT workspace_id FROM workspaces
                WHERE session_id = @sid
                """;
            list.Parameters.AddWithValue("@sid", state.Id.Value);
            await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0));
        }

        foreach (var id in existing)
        {
            if (keep.Contains(id))
                continue;

            await using var ws = conn.CreateCommand();
            ws.Transaction = tx;
            ws.CommandText = "DELETE FROM workspaces WHERE workspace_id = @id";
            ws.Parameters.AddWithValue("@id", id);
            await ws.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, WorkspaceState>> LoadWorkspacesAsync(
        SqliteConnection conn, string sessionId, CancellationToken ct)
    {
        var map = new Dictionary<string, WorkspaceState>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id, label, cwd, focused_tab_id, binding_json, ordinal, worktree_json, default_pane_pending
            FROM workspaces WHERE session_id = @sid
            ORDER BY ordinal ASC, workspace_id ASC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var label = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var cwd = reader.GetString(2);
            var focusedTab = reader.IsDBNull(3) ? null : reader.GetString(3);
            var bindingJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            var ordinal = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var worktreeJson = reader.FieldCount > 6 && !reader.IsDBNull(6)
                ? reader.GetString(6)
                : null;
            var defaultPanePending = reader.FieldCount > 7
                && !reader.IsDBNull(7)
                && reader.GetInt64(7) != 0;
            map[id] = new WorkspaceState
            {
                Id = new WorkspaceId(id),
                Ordinal = ordinal,
                Label = label,
                Cwd = cwd,
                FocusedTabId = focusedTab is null ? null : new TabId(focusedTab),
                Binding = DeserializeBinding(bindingJson),
                Worktree = DeserializeWorktree(worktreeJson),
                DefaultPanePending = defaultPanePending,
            };
        }

        return map;
    }

    private static async Task<Dictionary<string, TabState>> LoadTabsAsync(
        SqliteConnection conn, IEnumerable<string> workspaceIds, CancellationToken ct)
    {
        var map = new Dictionary<string, TabState>(StringComparer.Ordinal);
        var ids = workspaceIds.ToList();
        if (ids.Count == 0)
            return map;

        foreach (var wsId in ids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT tab_id, workspace_id, label, ordinal, focused_pane_id, layout_json, zoomed, zoomed_pane_id, custom_label
                FROM tabs WHERE workspace_id = @ws
                ORDER BY ordinal ASC
                """;
            cmd.Parameters.AddWithValue("@ws", wsId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var tabId = reader.GetString(0);
                var workspaceId = reader.GetString(1);
                var label = reader.IsDBNull(2) ? "main" : reader.GetString(2);
                var ordinal = reader.GetInt32(3);
                var focusedPane = reader.FieldCount > 4 && !reader.IsDBNull(4)
                    ? reader.GetString(4)
                    : null;
                var layoutJson = reader.FieldCount > 5 && !reader.IsDBNull(5)
                    ? reader.GetString(5)
                    : null;
                var zoomed = reader.FieldCount > 6 && !reader.IsDBNull(6) && reader.GetInt64(6) != 0;
                var zoomedPane = reader.FieldCount > 7 && !reader.IsDBNull(7)
                    ? reader.GetString(7)
                    : null;
                var customLabel = reader.FieldCount > 8 && !reader.IsDBNull(8)
                    && reader.GetInt64(8) != 0;
                map[tabId] = new TabState
                {
                    Id = new TabId(tabId),
                    WorkspaceId = new WorkspaceId(workspaceId),
                    Label = label,
                    Ordinal = ordinal,
                    FocusedPaneId = focusedPane is null ? null : new PaneId(focusedPane),
                    LayoutRoot = LayoutNode.Parse(layoutJson),
                    Zoomed = zoomed,
                    ZoomedPaneId = zoomedPane is null ? null : new PaneId(zoomedPane),
                    CustomLabel = customLabel,
                };
            }
        }

        return map;
    }

    private static async Task<Dictionary<string, PaneState>> LoadPanesAsync(
        SqliteConnection conn, IEnumerable<string> workspaceIds, CancellationToken ct)
    {
        var map = new Dictionary<string, PaneState>(StringComparer.Ordinal);
        foreach (var wsId in workspaceIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT pane_id, tab_id, workspace_id, label, cwd, command, cols, rows,
                       lifecycle_state, occupant_generation, pid, exit_code,
                       agent_kind, agent_status, binding_json, created_at, updated_at, agent_message,
                       args_json, seen, right_click, agent_authority_json, hidden, parent_pane_id, agent_name
                FROM panes WHERE workspace_id = @ws
                """;
            cmd.Parameters.AddWithValue("@ws", wsId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var paneId = reader.GetString(0);
                // clamp legacy/DEFAULT 0 to first-occupant identity 1 on load.
                var storedGen = reader.GetInt32(9);
                var authority = reader.FieldCount > 21 && !reader.IsDBNull(21)
                    ? DeserializePaneAuthority(reader.GetString(21))
                    : null;
                map[paneId] = new PaneState
                {
                    Id = new PaneId(paneId),
                    TabId = new TabId(reader.GetString(1)),
                    WorkspaceId = new WorkspaceId(reader.GetString(2)),
                    Label = reader.GetString(3),
                    Cwd = reader.GetString(4),
                    Command = reader.GetString(5),
                    Args = reader.FieldCount > 18 && !reader.IsDBNull(18)
                        ? DeserializeArgs(reader.GetString(18))
                        : [],
                    Cols = reader.GetInt32(6),
                    Rows = reader.GetInt32(7),
                    LifecycleState = reader.GetString(8),
                    OccupantGeneration = storedGen <= 0 ? 1 : storedGen,
                    Pid = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    ExitCode = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    AgentKind = reader.IsDBNull(12) ? null : reader.GetString(12),
                    AgentStatus = ParseAgentStatus(reader.GetString(13)),
                    Binding = DeserializeBinding(reader.IsDBNull(14) ? null : reader.GetString(14)),
                    CreatedAt = ParseTimestamp(reader.GetString(15)),
                    UpdatedAt = ParseTimestamp(reader.GetString(16)),
                    AgentMessage = reader.FieldCount > 17 && !reader.IsDBNull(17)
                        ? reader.GetString(17)
                        : null,
                    Seen = reader.FieldCount <= 19 || reader.IsDBNull(19) || reader.GetInt32(19) != 0,
                    RightClick = reader.FieldCount > 20 && !reader.IsDBNull(20)
                        ? reader.GetString(20)
                        : PaneRightClick.Hypa,
                    // Never report alive from disk alone — restore path reconciles via probe.
                    IsAlive = false,
                    AgentAuthority = authority?.Authority,
                    AgentSession = authority?.AgentSession,
                    AgentAuthoritySequences = authority?.Sequences
                        ?? new Dictionary<string, long>(StringComparer.Ordinal),
                    Placement = reader.FieldCount > 22 && !reader.IsDBNull(22) && reader.GetInt64(22) != 0
                        ? PanePlacement.Hidden
                        : PanePlacement.Tiled,
                    ParentPaneId = reader.FieldCount > 23 && !reader.IsDBNull(23)
                        ? new PaneId(reader.GetString(23))
                        : null,
                    AgentName = reader.FieldCount > 24 && !reader.IsDBNull(24)
                        ? reader.GetString(24)
                        : null,
                };
            }
        }

        return map;
    }

    private static string? SerializePaneAuthority(PaneState pane)
    {
        var dto = PaneAuthorityStorageDto.FromDomain(pane);
        if (dto is null)
            return null;
        return JsonSerializer.Serialize(dto, RuntimeStorageJsonContext.Default.PaneAuthorityStorageDto);
    }

    private static PaneAuthorityLoad? DeserializePaneAuthority(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var dto = JsonSerializer.Deserialize(json, RuntimeStorageJsonContext.Default.PaneAuthorityStorageDto)
            ?? throw new JsonException("agent_authority_json deserialized to null");
        return dto.ToDomain();
    }

    private static string? SerializeWorktree(Hypa.AgentRuntime.Domain.Worktrees.WorktreeSpaceMembership? membership)
    {
        var dto = WorktreeSpaceMembershipStorageDto.FromDomain(membership);
        if (dto is null)
            return null;
        return JsonSerializer.Serialize(dto, RuntimeStorageJsonContext.Default.WorktreeSpaceMembershipStorageDto);
    }

    private static Hypa.AgentRuntime.Domain.Worktrees.WorktreeSpaceMembership? DeserializeWorktree(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize(json, RuntimeStorageJsonContext.Default.WorktreeSpaceMembershipStorageDto)
                ?? throw new JsonException("corrupt worktree_json");
            if (string.IsNullOrWhiteSpace(dto.Key)
                || string.IsNullOrWhiteSpace(dto.RepoRoot)
                || string.IsNullOrWhiteSpace(dto.CheckoutPath))
            {
                throw new JsonException("corrupt worktree_json");
            }

            return dto.ToDomain();
        }
        catch (JsonException ex)
        {
            throw new JsonException("corrupt worktree_json", ex);
        }
    }

    private static string? SerializeBinding(AtomicBinding? binding)
    {
        var dto = AtomicBindingStorageDto.FromDomain(binding);
        if (dto is null)
            return null;
        return JsonSerializer.Serialize(dto, RuntimeStorageJsonContext.Default.AtomicBindingStorageDto);
    }

    private static AtomicBinding? DeserializeBinding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize(json, RuntimeStorageJsonContext.Default.AtomicBindingStorageDto);
            return dto?.ToDomain();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeArgs(IReadOnlyList<string> args) =>
        JsonSerializer.Serialize(args.ToList(), RuntimeStorageJsonContext.Default.ListString);

    private static IReadOnlyList<string> DeserializeArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize(json, RuntimeStorageJsonContext.Default.ListString) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AgentStatus ParseAgentStatus(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "working" => AgentStatus.Working,
            "blocked" => AgentStatus.Blocked,
            "idle" => AgentStatus.Idle,
            "done" => AgentStatus.Done,
            _ => AgentStatus.Unknown,
        };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DurableTimestampParser.ParseOrSentinel(value);
}
