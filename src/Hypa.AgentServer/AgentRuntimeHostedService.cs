using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.ControlPlane;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hypa.AgentServer;

public sealed record RuntimeOptions(
    string SessionName,
    string Cwd,
    string SocketPath,
    string StateDirectory)
{
    /// <summary>Set only when the user passed <c>--cwd</c>. Implicit launch dir is not a workspace cwd.</summary>
    public string? ExplicitCwd { get; init; }
}

public sealed class AgentRuntimeHostedService : IHostedService
{
    private readonly UnixSocketServer _socket;
    private readonly AppState _state;
    private readonly IControlPlaneService _controlPlane;
    private readonly IRuntimeSchemaMigrator _migrator;
    private readonly IRuntimeSessionStore _store;
    private readonly IRuntimeEventJournal _journal;
    private readonly IProcessLivenessProbe _liveness;
    private readonly RuntimeOptions _options;
    private readonly AttachClientConfig _attachConfig;
    private readonly IAgentManifestCatalog? _agentManifests;
    private readonly ILogger<AgentRuntimeHostedService> _logger;
    private readonly IProcessLogSink _processLog;
    private string? _statusPath;

    public AgentRuntimeHostedService(
        UnixSocketServer socket,
        AppState state,
        IControlPlaneService controlPlane,
        IRuntimeSchemaMigrator migrator,
        IRuntimeSessionStore store,
        IRuntimeEventJournal journal,
        IProcessLivenessProbe liveness,
        RuntimeOptions options,
        AttachClientConfig attachConfig,
        ILogger<AgentRuntimeHostedService> logger,
        IAgentManifestCatalog? agentManifests = null,
        IProcessLogSink? processLog = null)
    {
        _socket = socket;
        _state = state;
        _controlPlane = controlPlane;
        _migrator = migrator;
        _store = store;
        _journal = journal;
        _liveness = liveness;
        _options = options;
        _attachConfig = attachConfig;
        _logger = logger;
        _agentManifests = agentManifests;
        _processLog = processLog ?? NullProcessLogSink.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var migrate = await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        if (!migrate.IsOk)
        {
            _logger.LogCritical(
                "Runtime schema migration failed: {Code} {Message}",
                migrate.Error.Code, migrate.Error.Message);
            throw new InvalidOperationException(
                $"persistence_unavailable: {migrate.Error.Message}");
        }

        var load = await _store.TryLoadAsync(_options.SessionName, cancellationToken)
            .ConfigureAwait(false);
        if (!load.IsOk)
        {
            _logger.LogCritical(
                "Failed to load session graph: {Code} {Message}",
                load.Error.Code, load.Error.Message);
            throw new InvalidOperationException(
                $"persistence_unavailable: {load.Error.Message}");
        }

        if (load.Value is null)
        {
            if (_state.ListWorkspaces().Count == 0)
            {
                var workspaceCwd = ResolveInitialWorkspaceCwd(
                    _attachConfig,
                    _options.ExplicitCwd,
                    Environment.CurrentDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                _state.CreateWorkspace(workspaceCwd, label: "default");
                _logger.LogInformation("Created default workspace at {Cwd}", workspaceCwd);
            }

            _state.UpdateSession(s => s with
            {
                Name = _options.SessionName,
                LifecycleState = SessionLifecycle.Ready,
                Placement = "local",
                PlacementGeneration = 0,
                ReplayComplete = true,
            });
        }
        else
        {
            var restored = SessionGraphRestorer.ReconcileDeadPids(load.Value, _liveness);
            restored = SessionGraphRestorer.DropFailedDefaultPaneOrphans(restored);
            restored = SessionGraphRestorer.MarkReadyForLiveHost(restored);
            if (string.IsNullOrEmpty(restored.Name))
                restored = restored with { Name = _options.SessionName };
            _state.Replace(restored);
            if (_state.ListWorkspaces().Count == 0)
            {
                var workspaceCwd = ResolveInitialWorkspaceCwd(
                    _attachConfig,
                    _options.ExplicitCwd,
                    Environment.CurrentDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                _state.CreateWorkspace(workspaceCwd, label: "default");
                _logger.LogInformation("Created default workspace at {Cwd}", workspaceCwd);
            }

            _logger.LogInformation(
                "Restored session graph session_id={SessionId} workspaces={Ws} panes={Panes} session_state={State}",
                restored.Id.Value, _state.ListWorkspaces().Count, restored.Panes.Count, restored.LifecycleState);
        }

        var save = await _store.SaveAsync(_state.Snapshot(), cancellationToken, removeMissingPanes: true)
            .ConfigureAwait(false);
        if (!save.IsOk)
        {
            _logger.LogCritical(
                "Failed to persist session graph on start: {Code} {Message}",
                save.Error.Code, save.Error.Message);
            throw new InvalidOperationException(
                $"persistence_unavailable: {save.Error.Message}");
        }

        var journalHealth = await _journal.RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (!journalHealth.IsOk)
        {
            _logger.LogCritical(
                "Journal recovery failed: {Code} {Message}",
                journalHealth.Error.Code, journalHealth.Error.Message);
            throw new InvalidOperationException(
                $"persistence_unavailable: {journalHealth.Error.Message}");
        }

        var jh = journalHealth.Value;
        _state.UpdateSession(s => s with
        {
            NextEventSeq = jh.NextSeq,
            ReplayComplete = jh.ReplayComplete,
            ReplayError = jh.ReplayError,
        });

        // runs before HeadlessServer::new / ready. Spawn after Replace and
        // journal NextSeq so pane.lifecycle seq is not stale, before the socket.
        await _controlPlane.RestoreSpawnAsync(cancellationToken).ConfigureAwait(false);

        var saveFlags = await _store.SaveAsync(_state.Snapshot(), cancellationToken)
            .ConfigureAwait(false);
        if (!saveFlags.IsOk)
        {
            _logger.LogCritical(
                "Failed to persist journal flags: {Code} {Message}",
                saveFlags.Error.Code, saveFlags.Error.Message);
            throw new InvalidOperationException(
                $"persistence_unavailable: {saveFlags.Error.Message}");
        }

        await _socket.StartAsync(cancellationToken).ConfigureAwait(false);
        // Keep RestoreSpawn before the socket (bootstrap.rs:47-78). Type
        // the pending official resume after the socket accepts connections.
        await _controlPlane.FlushOfficialAgentResumesAsync(cancellationToken)
            .ConfigureAwait(false);
        await _controlPlane.FlushPluginStartupHooksAsync(cancellationToken)
            .ConfigureAwait(false);

        if (_attachConfig.Update.ManifestCheck && _agentManifests is not null)
        {
            var catalog = _agentManifests;
            _ = Task.Run(() => catalog.CheckRemoteUpdates(true), CancellationToken.None);
        }

        try
        {
            _statusPath = Path.Combine(
                Path.GetDirectoryName(_options.SocketPath) ?? ".",
                "runtime.status.json");
            await File.WriteAllTextAsync(
                _statusPath,
                $$"""
                {"session":"{{_options.SessionName}}","socket":"{{_options.SocketPath.Replace("\\", "\\\\")}}","cwd":"{{_options.Cwd.Replace("\\", "\\\\")}}","pid":{{Environment.ProcessId}}}
                """,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write status file");
        }

        ProcessLogLifecycle.Startup(
            _processLog,
            ProcessLogEvents.SubsystemMux,
            _state.SessionId.Value);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _controlPlane.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control plane shutdown failed");
        }

        await _socket.DisposeAsync().ConfigureAwait(false);

        if (_statusPath is not null)
        {
            try
            {
                if (File.Exists(_statusPath))
                    File.Delete(_statusPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove status file");
            }
        }

        _logger.LogInformation("hypa mux stopped");
        ProcessLogLifecycle.Shutdown(
            _processLog,
            ProcessLogEvents.SubsystemMux,
            _state.SessionId.Value);
        if (_processLog is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* sink errors must not fail shutdown */ }
        }
    }

    /// <summary>
    /// First workspace: explicit <c>--cwd</c> wins; otherwise <c>terminal.new_cwd</c>
    /// </summary>
    internal static string ResolveInitialWorkspaceCwd(
        AttachClientConfig config,
        string? explicitCwd,
        string processCwd,
        string? home)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TerminalSpawnPolicy.ResolveNewCwd(
            config.Terminal,
            explicitCwd,
            sourceCwd: null,
            processCwd,
            home);
    }
}
