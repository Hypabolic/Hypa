using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Occupants;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane.Dispatch;
using Hypa.ControlPlane.Unix;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.ControlPlane;

/// <summary>
// / Agent-facing control plane.
/// for the MVP subset: session, workspace, pane, agent, events, health.
/// </summary>
public sealed partial class ControlPlaneService : IControlPlaneService
{
    private readonly AppState _state;
    private readonly IPaneRuntimeFactory _paneFactory;
    private readonly IIntelligencePipeline _intelligence;
    private readonly IAgentDetector _detector;
    private readonly IRuntimeSessionStore? _store;
    private readonly IPaneHistorySnapshotStore _paneHistoryStore;
    private readonly IRuntimeEventJournal? _journal;
    private readonly IEventSubscriptionHub? _subscriptions;
    private readonly ILeaseRegistry _leases;
    private readonly IAttachmentRegistry _attachments;
    private readonly IEventPayloadRedactor _redactor;
    private readonly IProcessLogSink _processLog;
    private readonly IAgentPresentationCompressor _presentation;
    private readonly IRuntimeEvidenceJournal? _evidence;
    private readonly ICheckpointService? _checkpoints;
    private readonly TimeProvider _time;
    private readonly MetadataTokenStore _metadata;
    private readonly object _metadataSweepSync = new();
    private Task _metadataSweepTask = Task.CompletedTask;
    private readonly string _ptyProvider;
    private readonly bool _ptyInteractive;
    private readonly string _vtProvider;
    private readonly string? _vtFallbackReason;
    private readonly string? _vtGhosttyVersion;
    private readonly string? _vtGhosttyBuild;
    private readonly string? _vtAbi;
    private readonly string[] _vtCapabilities;
    private readonly Dictionary<string, IPaneRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandOverlayTracker> _commandOverlays =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _overlayCloseTasks = new(StringComparer.Ordinal);
    /// <summary>
    /// Incoming replacement from <c>Create</c> until swap or fail. Shutdown disposes these
    /// because they are not in <see cref="_runtimes"/> until commit.
    /// </summary>
    private readonly Dictionary<string, IPaneRuntime> _pendingRuntimes = new(StringComparer.Ordinal);
    /// <summary>
    /// Pane ids whose occupant generation has been bumped but not yet swapped
    /// or rolled back. Close and wait treat these as uncommitted replacements.
    /// </summary>
    private readonly HashSet<string> _uncommittedReplacements = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _paneAdmitLocks = new(StringComparer.Ordinal);
    /// <summary>
    /// Serializes pane close vs terminal.observe/control so an attach cannot land on a
    /// pane that has already been cleaned up.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _paneSideEffectLocks = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    /// <summary>
    /// Serializes control-plane graph persists. Snapshot is taken only while holding
    /// this gate so concurrent exit/create paths cannot write a stale graph over a newer one.
    /// </summary>
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    /// <summary>
    /// Serializes reliable sequence allocation and ordered
    /// <see cref="IEventSubscriptionHub.PostLive"/> so session-reliable emits post
    /// in increasing seq (design §10.2). Released before fsync and the floor write.
    /// Output and Render use <see cref="_paneEmitGates"/>, not this gate.
    /// </summary>
    private readonly SemaphoreSlim _emitGate = new(1, 1);
    /// <summary>
    /// Per-pane serialize of Output redact/append/PostLive and Render seq/PostLive.
    /// Two panes do not share a gate. Never nest with <see cref="_emitGate"/>.
    /// TryRemove on close; do not Dispose (same rule as <see cref="_paneAdmitLocks"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _paneEmitGates = new(StringComparer.Ordinal);
    /// <summary>
    /// Serializes lease claim/release mutation with ordered lease.changed audit emit
    /// so concurrent takeovers cannot reorder journal transitions.
    /// </summary>
    private readonly SemaphoreSlim _leaseAuditGate = new(1, 1);
    /// <summary>
    /// Serializes binding admit + apply (binding.set, pane.create, workspace.create/focus/
    /// rename/close), metadata report (nested <see cref="_metadataEmitGate"/>), and
    /// freeze-sensitive mutations (pane.close graph remove, send_text/keys/resize/prompt
    /// write admit, agent.start occupant rewrite) so prepare's freeze under this gate
    /// cannot race post-barrier graph or terminal IO. Lock order with prepare: checkpoint
    /// → binding → metadata emit → emit (never reverse). Pane close: side-effect → binding.
    /// Agent prompt and agent.start: pane admit → binding.
    /// </summary>
    private readonly SemaphoreSlim _bindingMutationGate = new(1, 1);
    /// <summary>
    /// Serializes metadata ExpireDue/ApplyPatch payload capture and live emit.
    /// Pane/agent expire-on-read takes this gate only so <c>agent.get</c> does not
    /// wait on <c>agent.prompt</c>. Lock order: binding → this (never reverse).
    /// </summary>
    private readonly SemaphoreSlim _metadataEmitGate = new(1, 1);
    /// <summary>
    /// Serializes checkpoint prepare/export snapshot + persist so barrier_seq, freeze,
    /// and fingerprint are consistent under concurrent callers. Not held across
    /// workspace I/O so abort can unfreeze while export walks.
    /// </summary>
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly object _checkpointIoSync = new();
    private CancellationTokenSource? _checkpointIoCts;
    private readonly ConcurrentDictionary<string, OutputEmitFlight> _outputFlights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PaneOutputEmitLoop> _paneOutputLoops =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _terminalOutputRawCapByPaneId =
        new(StringComparer.Ordinal);
    private long _outputEmitChunkRents;
    private long _outputEmitChunkReturns;
    private readonly ConcurrentDictionary<string, long> _paneRenderSeq = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _paneOutputSeq = new(StringComparer.Ordinal);
    /// <summary>
    /// Last posted VT-snapshot feed generation per pane. EmitOutputAsync drops
    /// Render for a chunk whose Feed predates this epoch (already in the grid).
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _paneSnapshotEpoch = new(StringComparer.Ordinal);
    /// <summary>
    /// Per-paint snapshot id per pane. Incremented on every VT grid snapshot
    /// (attach and live) so a client cannot assemble slices from two paints.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _paneAttachSnapshotGeneration = new(StringComparer.Ordinal);
    /// <summary>Coalesce one live paint per pane per 16 ms tick.</summary>
    private readonly PaneRenderCoalescer _renderCoalescer;
    /// <summary>Coalesce one detection scan per pane per 300 ms tick.</summary>
    private readonly PaneDetectionScanner _detectionScanner;
    /// <summary>
    /// Per-pane serialize of tick and forced-exit detection scans. Tick and
    /// exit must not ReadDetectionText then UpdatePane out of order. TryRemove
    /// on close; do not Dispose (same rule as <see cref="_paneAdmitLocks"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _paneDetectionLocks = new(StringComparer.Ordinal);
    private int _persistDirty;
    /// <summary>
    /// Set when a graph save must delete missing pane/tab/workspace rows.
    /// Stays set until a save with that flag succeeds so a later persist
    /// still prunes after a failed close write.
    /// </summary>
    private int _persistRemoveMissing;
    private readonly ConcurrentDictionary<string, AgentDetectionPresence> _agentPresence =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly ControlPlaneMethodRegistry _methods;
    private readonly IPaneKeyComboEncoder _keyComboEncoder;
    private readonly IPaneProcessInfoProbe _processInfoProbe;
    private readonly IRuntimeHostStop? _hostStop;
    private readonly IServerInstallProbe? _installProbe;
    private readonly IAttachConfigRuntime _attachConfigRuntime;
    private readonly IOccupantManifestRegistry _occupants;
    private readonly IPaneVisibilityService _visibility;
    private readonly IVisibleSetPublication _visibleSets;
    private readonly IAttachSurfaceInterestPublication _attachSurfaceInterest;
    private readonly IAttachClientViewPublication _attachClientViews;
    private readonly AttachTabGeometryPublication _tabGeometry;
    private readonly IPanePlacementAuthorityService _placement;
    private readonly IOverlayPlacementFence _overlayFence;
    private readonly IPaneOverlayService _overlay;
    private readonly IOverlayVisibleSetHook? _overlayVisibleSet;
    private readonly string _cubeHome;
    private readonly string _operatorHome;
    private int _shuttingDown;
    private int _serverStopStarted;
    private int _liveHandoffStarted;
    private int _subCounter;
    private readonly ConcurrentDictionary<string, NotificationTokenBucket> _notificationBuckets = new(StringComparer.Ordinal);
    private readonly object _windowTitleGate = new();
    private string? _windowTitleOverride;
    private readonly object _agentViewGate = new();
    private AgentViewSpec? _agentViewOverride;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notificationBusyUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Offset, int MaxOffset)> _lastScroll = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PaneInputActor> _inputActors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _closedInputUndeliverable = new(StringComparer.Ordinal);
    private readonly WorkGenerationGate _workGenerationGate = new();
    private readonly ILiveCellsEncoder _liveCellsEncoder;
    private readonly IGitWorktreePort _gitWorktrees;
    private readonly object _worktreeOpGate = new();
    private readonly HashSet<string> _worktreeOpsInProgress = new(StringComparer.Ordinal);
    /// <summary>
    /// Serializes parent-workspace ensure for one control plane.
    /// Lock order: this gate, then <see cref="_bindingMutationGate"/>
    /// inside <c>WorkspaceCreateAsync</c>. Never reverse. Never hold
    /// this gate while waiting for a caller that already holds binding.
    /// </summary>
    private readonly SemaphoreSlim _parentMembershipGate = new(1, 1);
    private readonly IOfficialIntegrationService _integrations;
    private readonly IPluginHost _plugins;
    private readonly string? _runtimeSocketPath;
    private readonly string? _cliBinPath;
    private readonly string? _pluginConfigRoot;
    private readonly HashSet<string> _resumedAgentSessions = new(StringComparer.Ordinal);
    /// <summary>
    /// plan. Hypa types the command after the control socket is up.
    /// </summary>
    private readonly List<PendingOfficialAgentResume> _pendingOfficialAgentResumes = [];

    public ControlPlaneService(
        AppState state,
        IPaneRuntimeFactory paneFactory,
        IIntelligencePipeline intelligence,
        IAgentDetector detector,
        ILogger? logger = null,
        IRuntimeSessionStore? store = null,
        IRuntimeEventJournal? journal = null,
        IEventSubscriptionHub? subscriptions = null,
        string? ptyProvider = null,
        bool? ptyInteractive = null,
        ILeaseRegistry? leases = null,
        IAttachmentRegistry? attachments = null,
        TimeProvider? timeProvider = null,
        IEventPayloadRedactor? redactor = null,
        IAgentPresentationCompressor? presentation = null,
        IRuntimeEvidenceJournal? evidence = null,
        ICheckpointService? checkpoints = null,
        string? vtProvider = null,
        string? vtFallbackReason = null,
        string? vtGhosttyVersion = null,
        string? vtGhosttyBuild = null,
        string? vtAbi = null,
        IReadOnlyList<string>? vtCapabilities = null,
        IPaneKeyComboEncoder? keyComboEncoder = null,
        IPaneProcessInfoProbe? processInfoProbe = null,
        IRuntimeHostStop? hostStop = null,
        AttachClientConfig? attachConfig = null,
        IAttachConfigRuntime? attachConfigRuntime = null,
        IOccupantManifestRegistry? occupants = null,
        string? stateDirectory = null,
        string? cubeHome = null,
        string? operatorHome = null,
        IPaneVisibilityService? paneVisibility = null,
        IVisibleSetPublication? visibleSets = null,
        IAttachSurfaceInterestPublication? attachSurfaceInterest = null,
        ILiveCellsEncoder? liveCellsEncoder = null,
        IGitWorktreePort? gitWorktrees = null,
        IPanePlacementAuthorityService? placementAuthority = null,
        IOverlayPlacementFence? overlayFence = null,
        IPaneOverlayService? paneOverlay = null,
        IOverlayVisibleSetHook? overlayVisibleSet = null,
        IOfficialIntegrationService? integrations = null,
        IPluginHost? plugins = null,
        string? runtimeSocketPath = null,
        IPaneHistorySnapshotStore? paneHistoryStore = null,
        IProcessLogSink? processLog = null,
        string? cliProcessPath = null,
        string? pluginConfigRoot = null,
        IServerInstallProbe? installProbe = null)
    {
        _state = state;
        _paneFactory = paneFactory;
        _intelligence = intelligence;
        _detector = detector;
        _store = store;
        _journal = journal;
        _subscriptions = subscriptions;
        _time = timeProvider ?? TimeProvider.System;
        _metadata = new MetadataTokenStore(_time);
        _metadata.SweepRequested += OnMetadataSweepRequested;
        _leases = leases ?? new InMemoryLeaseRegistry(_time);
        _attachments = attachments ?? new InMemoryAttachmentRegistry();
        // Production default cannot be disabled. Tests may inject a double.
        _redactor = redactor ?? new DefaultEventPayloadRedactor();
        _processLog = processLog ?? NullProcessLogSink.Instance;
        _presentation = presentation ?? new FallbackAgentPresentationCompressor(intelligence);
        _evidence = evidence;
        _checkpoints = checkpoints;
        _logger = logger ?? NullLogger.Instance;
        // Health follows the pane factory when the host does not inject values.
        _ptyProvider = string.IsNullOrWhiteSpace(ptyProvider)
            ? paneFactory.PtyProvider
            : ptyProvider;
        _ptyInteractive = ptyInteractive ?? paneFactory.PtyInteractive;
        // Default Ghostty when the host does not inject a provider (unit tests / F1).
        _vtProvider = string.IsNullOrWhiteSpace(vtProvider) ? VtFloorDefaults.Provider : vtProvider;
        _vtFallbackReason = vtFallbackReason;
        _vtGhosttyVersion = vtGhosttyVersion;
        _vtGhosttyBuild = vtGhosttyBuild;
        _vtAbi = vtAbi;
        _vtCapabilities = vtCapabilities is { Count: > 0 }
            ? vtCapabilities.ToArray()
            : VtFloorDefaults.Capabilities.ToArray();
        _keyComboEncoder = keyComboEncoder ?? new VtPaneKeyComboEncoder();
        _processInfoProbe = processInfoProbe ?? NullPaneProcessInfoProbe.Instance;
        _hostStop = hostStop;
        _installProbe = installProbe;
        _attachConfigRuntime = attachConfigRuntime
            ?? new StaticAttachConfigRuntime(attachConfig ?? AttachClientConfig.Default);
        _occupants = occupants ?? BundledOccupantManifestRegistry.Default;
        _visibility = paneVisibility ?? new PaneVisibilityService(_state);
        _visibleSets = visibleSets ?? new VisibleSetPublication(_time);
        _attachSurfaceInterest = attachSurfaceInterest
            ?? new AttachSurfaceInterestPublication(muxIdentity: state.Snapshot().Id.Value);
        _attachClientViews = new AttachClientViewPublication();
        _tabGeometry = new AttachTabGeometryPublication();
        _placement = placementAuthority ?? new PanePlacementAuthorityService(_leases);
        _overlay = paneOverlay ?? new PaneOverlayService(_state);
        _overlayFence = overlayFence ?? new ControlPlaneOverlayFence(this);
        _overlayVisibleSet = overlayVisibleSet ?? new OverlayVisibleSetHook(_visibleSets);
        var stateDir = string.IsNullOrWhiteSpace(stateDirectory)
            ? Path.Combine(Path.GetTempPath(), "hypa-runtime-tests")
            : stateDirectory;
        _cubeHome = string.IsNullOrWhiteSpace(cubeHome)
            ? CubeHomePaths.Resolve(stateDir)
            : Path.GetFullPath(cubeHome);
        _operatorHome = string.IsNullOrWhiteSpace(operatorHome)
            ? ResolveProcessHome() ?? _cubeHome
            : Path.GetFullPath(operatorHome.Trim());
        _methods = new ControlPlaneMethodRegistry(this);
        _renderCoalescer = new PaneRenderCoalescer(_time, FlushCoalescedPaneAsync);
        _detectionScanner = new PaneDetectionScanner(
            _time,
            (id, generation) => RunDetectionScan(id, invokeApplyHook: true, markedGeneration: generation));
        _liveCellsEncoder = liveCellsEncoder ?? new VtCellsEncoder();
        _gitWorktrees = gitWorktrees ?? new Hypa.AgentRuntime.Infrastructure.Git.ProcessGitWorktreeAdapter();
        _integrations = integrations ?? OfficialIntegrationService.CreateSystem();
        _runtimeSocketPath = string.IsNullOrWhiteSpace(runtimeSocketPath)
            ? null
            : runtimeSocketPath.Trim();
        _cliBinPath = HypaCliPathResolver.Resolve(cliProcessPath);
        _pluginConfigRoot = string.IsNullOrWhiteSpace(pluginConfigRoot)
            ? null
            : Path.GetFullPath(pluginConfigRoot.Trim());
        _plugins = plugins ?? CreatePluginHost();
        _paneHistoryStore = paneHistoryStore ?? NullPaneHistorySnapshotStore.Instance;
        _workspaceDirectoryGit = new Hypa.AgentRuntime.Infrastructure.Config.ProcessSidebarGitStatus(time: _time);
        _workspaceDirectoryTimer = _time.CreateTimer(_ => RequestWorkspaceDirectoryRefresh(), null,
            Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1));
    }

    private IPluginHost CreatePluginHost()
    {
        var context = new ControlPlanePluginContextSource(this);
        try
        {
            return PluginHostFactory.CreateSystem(
                configRoot: _pluginConfigRoot,
                socketPath: _runtimeSocketPath,
                binPath: _cliBinPath,
                context: context);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "plugin host failed to start; continuing without plugins");
            return PluginHostFactory.CreateEmpty(context);
        }
    }

    /// <summary>Live attach config. One immutable record. Reload swaps Current.</summary>
    internal AttachClientConfig AttachConfig => _attachConfigRuntime.Current;

    /// <summary>Convenience overload for in-process callers that have no socket connection.</summary>
    public Task<JsonElement> DispatchAsync(
        string method, JsonElement? parameters, CancellationToken ct) =>
        DispatchAsync(method, parameters, connection: null, ct);

    public Task<JsonElement> DispatchAsync(
        string method,
        JsonElement? parameters,
        IClientConnection? connection,
        CancellationToken ct) =>
        DispatchAsync(method, parameters, connection, grantToken: null, ct);

    internal async Task<JsonElement> DispatchAsync(
        string method,
        JsonElement? parameters,
        IClientConnection? connection,
        string? grantToken,
        CancellationToken ct) =>
        await DispatchAsync(method, parameters, connection, grantToken, pluginConnection: false, ct)
            .ConfigureAwait(false);

    internal async Task<JsonElement> DispatchAsync(
        string method,
        JsonElement? parameters,
        IClientConnection? connection,
        string? grantToken,
        bool pluginConnection,
        CancellationToken ct)
    {
        // Fast-fail after shutdown begins. Pane registration also re-checks under
        // _gate so a create cannot land after ShutdownAsync takes its snapshot.
        if (IsShuttingDown && !string.Equals(method, ProtocolMethods.RuntimeHealth, StringComparison.Ordinal))
            throw new ControlPlaneException(
                ProtocolErrorCodes.ServerShuttingDown,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));

        ApplyGrantToken(method, parameters, grantToken, pluginConnection);

        // Live request path: ControlPlaneMethodRegistry + ProtocolJsonContext (no GetString).
        var result = await _methods.DispatchAsync(
            method, parameters, connection, AdvertisedCapabilitySet(), ct).ConfigureAwait(false);
        if (connection is null
            && string.Equals(method, ProtocolMethods.ServerStop, StringComparison.Ordinal))
        {
            await CompleteServerStopAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// deny graph mutations while the session is frozen_read_only after prepare.
    /// Allowed while frozen: snapshot/health/subscribe/read/export/export.ack/checkpoint.export/abort.
    /// Call again under the same gate as the graph write (binding/side-effect) to close TOCTOU
    /// races where prepare freezes after entry check and before mutation.
    /// </summary>
    private void EnsureNotFrozenForMutation(string operation)
    {
        var lifecycle = _state.Snapshot().LifecycleState;
        if (SessionLifecycle.IsFrozen(lifecycle))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                $"Session is frozen_read_only; {operation} is not allowed");
        }
    }

    /// <summary>
    /// Load journal range for a registered subscription before the subscribe RPC result
    /// is written. On failure unregisters and returns the error so the server can fail
    /// the RPC with an id-correlated <c>persistence_unavailable</c> response.
    /// </summary>
    public async Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> PrepareEventsSubscribeAsync(
        string subscriptionId,
        CancellationToken ct)
    {
        if (_subscriptions is null || _journal is null)
        {
            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(
                RuntimePersistenceError.Io("Event journal not configured"));
        }

        var sub = _subscriptions.Get(subscriptionId);
        if (sub is null || sub.IsClosed)
        {
            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(
                RuntimePersistenceError.Io("Subscription not found"));
        }

        var range = await _journal.ReadRangeAsync(
            sub.FromSeq, sub.Classes, sub.ReplayBudget, ct).ConfigureAwait(false);
        if (!range.IsOk)
        {
            _logger.LogWarning(
                "Subscribe replay failed for {SubId}: {Error}",
                subscriptionId, range.Error.Message);
            _subscriptions.Unregister(subscriptionId);
            return RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Fail(range.Error);
        }

        return range;
    }

    /// <summary>
    /// After the subscribe RPC success response has been written: push replay events then
    /// enable live. Safe to run on a connection-scoped task while the request reader
    /// continues (design §8.1 registration methods must not block the request loop).
    /// </summary>
    public async Task CompleteEventsSubscribeAsync(
        string subscriptionId,
        IReadOnlyList<RuntimeEventRecord> replayRecords,
        IClientConnection connection,
        CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        var sub = _subscriptions.Get(subscriptionId);
        if (sub is null || sub.IsClosed)
            return;

        // Replay uses the same class + observe filters as live fanout. terminal.output
        // and terminal.render are fail-closed until terminal.observe/control attaches a pane
        // (design §8.1 / B.3). Do not mark filtered seq as emitted so a later observe
        // still advances correctly from journal live path only. Render is live-only.
        foreach (var rec in replayRecords)
        {
            if (sub.IsClosed)
                return;
            if (!sub.MatchesSubscription(rec))
                continue;
            if (!sub.MatchesObserveFilter(rec))
                continue;
            var utf8 = EventSubscriptionHub.FormatBoundedRuntimeEventNdjsonLine(
                rec, subscriptionId, LineCapFor(connection));
            if (utf8.IsEmpty)
            {
                if (IsPaneInputRejected(rec))
                {
                    connection.CompleteWriter();
                    throw new IOException("Reject event exceeded the connection NDJSON line cap.");
                }

                sub.MarkEmitted(rec);
                continue;
            }

            await connection.WriteLineAsync(Encoding.UTF8.GetString(utf8.Span[..^1]), ct)
                .ConfigureAwait(false);
            sub.MarkEmitted(rec);
        }

        // Enable live and flush any events buffered during replay (no double emit).
        // Hold the per-subscription deliver gate so concurrent live delivery cannot
        // interleave writes and break seq order on this connection.
        if (sub.Live)
        {
            try
            {
                await sub.DeliverGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (sub.IsClosed)
                    return;
                sub.EnableLive();
                await FlushSubscribeLiveQueueAsync(sub, subscriptionId, connection, ct)
                    .ConfigureAwait(false);

                // Wake any leftover waiters, then drain again so a reliable
                // event that enqueued after the first DrainLiveQueue cannot
                // sit unflushed.
                sub.SignalQueueSpace();
                await FlushSubscribeLiveQueueAsync(sub, subscriptionId, connection, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                try { sub.DeliverGate.Release(); }
                catch (ObjectDisposedException) { /* closed mid-complete */ }
            }
        }
    }

    private async Task FlushSubscribeLiveQueueAsync(
        EventSubscription sub,
        string subscriptionId,
        IClientConnection connection,
        CancellationToken ct)
    {
        foreach (var rec in sub.DrainLiveQueue())
        {
            if (sub.IsClosed)
                return;
            if (sub.HasAlreadyEmitted(rec))
                continue;
            if (!ShouldEmitAttachQueuedRecord(connection, rec))
                continue;
            var utf8 = EventSubscriptionHub.FormatBoundedRuntimeEventNdjsonLine(
                rec, subscriptionId, LineCapFor(connection));
            if (utf8.IsEmpty)
            {
                if (IsPaneInputRejected(rec))
                {
                    connection.CompleteWriter();
                    throw new IOException("Reject event exceeded the connection NDJSON line cap.");
                }

                sub.MarkEmitted(rec);
                continue;
            }

            AttachSurfaceEmitBinding? liveBinding = null;
            if (rec.AttachEmitSurfaceRevision is { } surfaceRevision
                && rec.AttachEmitSnapshotRevision is { } snapshotRevision)
            {
                if (!TryAdmitAttachSurfaceRender(
                        connection.ConnectionId,
                        surfaceRevision,
                        snapshotRevision,
                        out liveBinding)
                    || (liveBinding is not null && !RecheckAttachSurfaceEmit(liveBinding)))
                    continue;
            }
            else if (connection.RequiresAttachRenderBinding
                && _attachSurfaceInterest.GetSnapshot(connection.ConnectionId) is not null)
            {
                continue;
            }

            var jsonLine = Encoding.UTF8.GetString(utf8.Span[..^1]);
            if (liveBinding is not null && connection is ClientConnection attachConn)
                await attachConn.WriteAttachLiveLineAsync(jsonLine, liveBinding, ct)
                    .ConfigureAwait(false);
            else
                await connection.WriteLineAsync(jsonLine, ct).ConfigureAwait(false);
            sub.MarkEmitted(rec);
        }
    }

    public void OnClientWriterDrained(IClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_subscriptions is null)
            return;
        _subscriptions.RequestLiveDrain(connection.ConnectionId);
        foreach (var sub in _subscriptions.ListForConnection(connection.ConnectionId))
        {
            foreach (var (paneId, batch) in sub.TakeDeferredJsonFulls())
                TryAdmitDeferredJsonFull(sub, paneId, batch);
            foreach (var (paneId, frame, emitBinding) in sub.TakeDeferredFrames())
                TryAdmitDeferredVtFrame(sub, paneId, frame, emitBinding);
        }
    }

    private void TryAdmitDeferredJsonFull(EventSubscription sub, string paneId, DeferredJsonFull batch)
    {
        if (!sub.Sink.UsesWriterLanes)
            return;
        if (batch.EmitBinding is { } binding && !RecheckAttachSurfaceEmit(binding))
            return;
        var admitted = sub.Sink.EnqueueOrderedRenderBatch(batch.Lines, batch.EmitBinding);
        if (admitted.IsOk)
        {
            sub.CommitFrameIdentity(paneId, batch.Identity);
            if (_subscriptions is not null
                && _subscriptions.AllLiveHaveFrame(paneId, batch.Identity)
                && TryGetRuntime(paneId, out var runtime)
                && runtime is IPaneVtSnapshot snapshot)
            {
                snapshot.CommitPostedPaint();
                _paneSnapshotEpoch[paneId] = batch.FeedGeneration;
            }
        }
        else if (admitted.IsFull)
        {
            sub.DeferJsonFull(paneId, batch);
        }
    }

    private void TryAdmitDeferredVtFrame(
        EventSubscription sub,
        string paneId,
        VtFrame frame,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        if (!sub.Sink.UsesWriterLanes)
            return;
        var baseline = sub.LastAdmittedFrame(paneId);
        // src/protocol/render_ansi.rs:88-92 encodes with prev = None.
        var reanchorGeneration = sub.ReanchorGeneration(paneId);
        var encodeBaseline = reanchorGeneration != 0 ? null : baseline;
        var encoding = _liveCellsEncoder.Encode(
            frame,
            encodeBaseline,
            paneId,
            frame.Generation,
            encodeBaseline?.Generation ?? 0,
            frame.OccupantGeneration);
        if (encoding is null)
            return;

        var (admitted, dropped) = AdmitLiveCells(
            sub,
            paneId,
            encoding.Value,
            sharedSlices: null,
            VtFrameIdentity.Token(frame),
            emitBinding);
        if (dropped)
        {
            sub.MarkReanchorPending(paneId);
            return;
        }
        if (admitted.IsOk)
            sub.CommitLastAdmittedFrame(paneId, frame, reanchorGeneration);
        else if (admitted.IsFull)
            sub.DeferLatestFrame(paneId, frame, emitBinding);
    }

    /// <summary>
    // / Admit one live cells payload.
    /// refuses an oversized frame without a panic. Slice by row under the
    /// observer line cap, or drop the frame. Do not FailClosed the attach
    /// socket. The payload UTF-8 is shared; the NDJSON envelope is per
    /// observer.
    /// </summary>
    private (WriterLaneResult Result, bool Dropped) AdmitLiveCells(
        EventSubscription sub,
        string paneId,
        in LiveCellsEncoding encoding,
        IReadOnlyList<AttachCellsPackedSlice>? sharedSlices,
        string frameIdentity,
        AttachSurfaceEmitBinding? emitBinding = null)
    {
        if (emitBinding is not null && !RecheckAttachSurfaceEmit(emitBinding))
            return (WriterLaneResult.Ok, true);

        var payload = encoding.Payload;
        var payloadUtf8 = encoding.PayloadUtf8;
        ArgumentNullException.ThrowIfNull(payloadUtf8);
        if (payload.WireBytes <= 0)
            payload = payload with { WireBytes = payloadUtf8.Length };

        var cap = sub.Sink.MaxLineBytes;
        if (cap < 256)
            cap = AttachCellsLinePacker.MaxNdjsonLineBytes;

        var seq = _paneRenderSeq.AddOrUpdate(paneId, 1L, static (_, prev) => prev + 1);
        IReadOnlyList<AttachCellsPackedSlice>? slices = sharedSlices;
        if (sharedSlices is not null && sharedSlices.Count == 0)
            return (WriterLaneResult.Ok, true);

        if (slices is null)
        {
            var record = new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Render,
                Reliability = EventReliability.Render,
                Type = ProtocolEventTypes.TerminalRender,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = "",
                PayloadUtf8 = payloadUtf8,
                PayloadUtf8Trusted = true,
                Lane = AttachCellsLinePacker.IsOrderedCellsPayload(payload)
                    ? WriterLaneNames.Ordered
                    : WriterLaneNames.Render,
                FramePaneId = paneId,
                FrameIdentity = frameIdentity,
            };
            if (emitBinding is not null)
                record = emitBinding.Stamp(record);
            var utf8 = EventSubscriptionHub.FormatRuntimeEventNdjsonLine(record, sub.SubscriptionId);
            if (utf8.Length <= cap)
            {
                var admitted = AttachCellsLinePacker.IsOrderedCellsPayload(payload)
                    ? sub.Sink.EnqueueOrderedRender(utf8, emitBinding)
                    : emitBinding is null
                        ? sub.Sink.TryEnqueueRender(utf8)
                        : sub.Sink.TryEnqueueRender(utf8, emitBinding);
                if (admitted.IsOk)
                    sub.NoteAdmittedPayloadUtf8(paneId, payloadUtf8);
                return (admitted, false);
            }
        }

        if (slices is null || slices.Count == 0)
        {
            // The complete payload UTF-8 is already in payloadUtf8. Each slice
            // UTF-8 comes from the one fit serialize.
            // src/protocol/wire.rs:1541-1558 write_message.
            slices = AttachCellsLinePacker.SliceToFitPacked(
                payload, sub.SubscriptionId, cap, skipCompleteFitCheck: true);
        }

        if (slices.Count == 0)
            return (WriterLaneResult.Ok, true);

        for (var pass = 0; pass < 2; pass++)
        {
            var lines = new ReadOnlyMemory<byte>[slices.Count];
            var fit = true;
            for (var i = 0; i < slices.Count; i++)
            {
                var sliceSeq = i == 0
                    ? seq
                    : _paneRenderSeq.AddOrUpdate(paneId, 1L, static (_, prev) => prev + 1);
                var sliceRecord = new RuntimeEventRecord
                {
                    Seq = sliceSeq,
                    Class = EventClass.Render,
                    Reliability = EventReliability.Render,
                    Type = ProtocolEventTypes.TerminalRender,
                    OccurredAt = DateTimeOffset.UtcNow,
                    PayloadJson = "",
                    PayloadUtf8 = slices[i].PayloadUtf8,
                    PayloadUtf8Trusted = true,
                    Lane = WriterLaneNames.Ordered,
                    FramePaneId = paneId,
                    FrameIdentity = frameIdentity,
                };
                if (emitBinding is not null)
                    sliceRecord = emitBinding.Stamp(sliceRecord);
                var line = EventSubscriptionHub.FormatRuntimeEventNdjsonLine(sliceRecord, sub.SubscriptionId);
                if (line.Length > cap)
                {
                    fit = false;
                    break;
                }

                lines[i] = line;
            }

            if (fit)
            {
                var admitted = emitBinding is null
                    ? sub.Sink.EnqueueOrderedRenderBatch(lines)
                    : sub.Sink.EnqueueOrderedRenderBatch(lines, emitBinding);
                if (admitted.IsOk)
                    sub.NoteAdmittedPayloadUtf8(paneId, payloadUtf8);
                return (admitted, false);
            }
            if (pass != 0)
                break;

            slices = AttachCellsLinePacker.SliceToFitPacked(
                payload, sub.SubscriptionId, cap, skipCompleteFitCheck: true);
            if (slices.Count == 0)
                return (WriterLaneResult.Ok, true);
        }

        return (WriterLaneResult.Ok, true);
    }

    public AttachClientLoss LastClientLoss { get; private set; } = AttachClientLoss.Disconnect;

    public bool ServerStopStarted => Volatile.Read(ref _serverStopStarted) != 0;

    public bool LiveHandoffStarted => Volatile.Read(ref _liveHandoffStarted) != 0;

    internal void MarkLiveHandoffStarted() =>
        Interlocked.Exchange(ref _liveHandoffStarted, 1);

    public void OnClientDisconnected(IClientConnection connection) =>
        OnClientDisconnected(connection, AttachClientLoss.Disconnect);

    public void OnClientDisconnected(IClientConnection connection, AttachClientLoss loss)
    {
        ArgumentNullException.ThrowIfNull(connection);
        LastClientLoss = loss;
        _subscriptions?.UnregisterConnection(connection.ConnectionId);
        _attachments.DropConnection(connection.ConnectionId);
        ClearAttachClientMode(connection.ConnectionId);
        _visibleSets.Remove(connection.ConnectionId);
        _attachSurfaceInterest.Remove(connection.ConnectionId);
        _attachClientViews.Unbind(connection.ConnectionId);
        ForgetPublishedShellPaneSizes(connection.ConnectionId);
        ReapplyShellTabGeometryAfterDisconnect(connection.ConnectionId);
        ReleaseOverlayOwner(connection.ConnectionId);
        var released = _leases.DropConnection(connection.ConnectionId);
        foreach (var lease in released)
        {
            // Fire-and-forget audit on disconnect path (no await on socket teardown).
            // Contain exceptions so journal/IO failures never become unobserved task faults.
            _ = SafeEmitLeaseChangedOnDisconnectAsync(lease);
        }
    }

    private async Task SafeEmitLeaseChangedOnDisconnectAsync(LeaseState lease)
    {
        try
        {
            await EmitLeaseChangedAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "lease.changed emit failed on disconnect for {LeaseId}",
                lease.LeaseId);
        }
    }

    /// <summary>
    /// After the <c>server.stop</c> result is on the wire, shut panes and the host.
    /// Idempotent. Detach and socket close do not call this.
    /// </summary>
    public async Task CompleteServerStopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _serverStopStarted, 1) != 0)
            return;

        try
        {
            await ShutdownAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _hostStop?.RequestStop();
        }
    }

    /// <summary>
    /// Best-effort disposal of every pane runtime. Host cancellation must not leave
    /// child processes alive — cleanup ignores <paramref name="ct"/> so a cancelling
    /// host token cannot abort mid-loop. Registration is gated under <see cref="_gate"/>
    /// so concurrent pane.create cannot outlive the snapshot.
    /// Durable graph is preserved: panes are marked exited/orphaned and saved, not wiped.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _ = ct; // host cancel must not abort child cleanup

        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
            return;

        await _workspaceDirectoryTimer.DisposeAsync().ConfigureAwait(false);
        PersistPaneHistory();

        // Multi-pass: any create that raced the flag either fails admission under
        // the gate or appears in a subsequent snapshot.
        var disposed = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            List<KeyValuePair<string, IPaneRuntime>> snapshot;
            lock (_gate)
            {
                snapshot = SnapshotOwnedRuntimesUnlocked();
                _runtimes.Clear();
                _pendingRuntimes.Clear();
                _uncommittedReplacements.Clear();
                _pendingOfficialAgentResumes.Clear();
            }

            foreach (var (id, runtime) in snapshot)
            {
                int? exitCode = null;
                var disposeOk = false;
                try
                {
                    runtime.OutputReceived -= OnRuntimeOutput;
                    runtime.BellReceived -= OnRuntimeBell;
                    runtime.Exited -= OnRuntimeExited;
                    exitCode = runtime.ExitCode;
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    disposeOk = true;
                    disposed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed disposing pane runtime {PaneId}", id);
                }

                try
                {
                    _intelligence.RemovePane(new PaneId(id));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Intelligence RemovePane failed for {PaneId}", id);
                }

                // Keep durable graph: mark pane exited/orphaned instead of RemovePane.
                _state.UpdatePane(new PaneId(id), p =>
                {
                    var nextStatus = StatusAfterProcessDeath(p);
                    return p with
                    {
                        IsAlive = false,
                        Pid = null,
                        ExitCode = exitCode ?? p.ExitCode,
                        LifecycleState = disposeOk || exitCode is not null
                            ? PaneLifecycle.Exited
                            : PaneLifecycle.Orphaned,
                        AgentStatus = nextStatus,
                        AgentKind = p.AgentAuthority?.Agent ?? p.AgentKind,
                        AgentMessage = p.AgentAuthority?.Message ?? p.AgentMessage,
                        Seen = SeenAfterStatus(p, nextStatus),
                        UpdatedAt = _time.GetUtcNow(),
                    };
                });
            }

            if (pass < 2)
            {
                await Task.Delay(25).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_runtimes.Count == 0 && _pendingRuntimes.Count == 0)
                        break;
                }
            }
        }

        await DisposePopupOnShutdownAsync().ConfigureAwait(false);

        // Any pane still marked alive / holding a pid without a reattached runtime is orphaned.
        foreach (var pane in _state.ListPanes())
        {
            if (!pane.IsAlive && pane.Pid is null &&
                pane.LifecycleState is PaneLifecycle.Exited or PaneLifecycle.Orphaned or PaneLifecycle.Closed)
                continue;

            _state.UpdatePane(pane.Id, p =>
            {
                var nextStatus = StatusAfterProcessDeath(p);
                return p with
                {
                    IsAlive = false,
                    Pid = null,
                    LifecycleState = p.ExitCode is not null || p.LifecycleState == PaneLifecycle.Exited
                        ? PaneLifecycle.Exited
                        : PaneLifecycle.Orphaned,
                    AgentStatus = nextStatus,
                    AgentKind = p.AgentAuthority?.Agent ?? p.AgentKind,
                    AgentMessage = p.AgentAuthority?.Message ?? p.AgentMessage,
                    Seen = SeenAfterStatus(p, nextStatus),
                    UpdatedAt = _time.GetUtcNow(),
                };
            });
        }

        // Prepared freeze must survive graceful host recycle. Export/abort remain
        // the only exits from frozen_read_only (design §10.4). Never persist stopped
        // here: MarkReadyForLiveHost would then promote the session to ready.
        _state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.IsFrozen(s.LifecycleState)
                ? SessionLifecycle.FrozenReadOnly
                : SessionLifecycle.Stopped,
        });

        foreach (var pane in _state.ListPanes())
        {
            try
            {
                await FlushTerminalOutputAsync(pane.Id.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Terminal carry flush failed for {PaneId} on shutdown", pane.Id);
            }
        }

        UnbindMetadataStore();
        try
        {
            await FlushExpiredMetadataAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metadata TTL flush failed on shutdown");
        }

        // Close open journal segment before final graph persist (journal outlives clients).
        if (_journal is not null)
        {
            try
            {
                await _journal.FlushManifestAsync(CancellationToken.None).ConfigureAwait(false);
                await _journal.CloseOpenSegmentAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed closing journal segment on shutdown");
            }

            SyncJournalFlagsToAppState();
        }

        // Final graceful-shutdown snapshot must be durable so restart restore is authoritative.
        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        _renderCoalescer.Dispose();
        _detectionScanner.Dispose();
        _metadata.Dispose();
        DisposePluginResourceCoalesce();

        _logger.LogInformation("Control plane shutdown disposed {Count} pane runtime(s)", disposed);
    }

    private bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    internal IAttachClientViewPublication AttachClientViews => _attachClientViews;

    private JsonObject SessionSnapshot(IClientConnection? connection = null)
    {
        var snap = _state.Snapshot();
        var panes = new JsonArray();
        foreach (var pane in snap.Panes.Values)
        {
            var node = PaneToJson(pane);
            // Who controls this pane's size now: the overlay owner while an
            // overlay holds the pane, else the tab geometry controller. A
            // declined pane.resize reports the same value.
            node["geometry_owner"] = _overlay.OwnerOf(pane.Id)?.AttachClientId
                ?? _tabGeometry.Controller(pane.TabId.Value)
                ?? "";
            panes.Add((JsonNode)node);
        }

        var workspaces = new JsonArray();
        foreach (var ws in _state.ListWorkspaces())
            workspaces.Add((JsonNode)WorkspaceToJson(ws));

        var tabs = new JsonArray();
        foreach (var tab in snap.Tabs.Values.OrderBy(t => t.Ordinal).ThenBy(t => t.Id.Value, StringComparer.Ordinal))
            tabs.Add((JsonNode)TabToJson(tab));

        var focusedTabId = snap.FocusedWorkspaceId is { } fw
            && snap.Workspaces.TryGetValue(fw.Value, out var focusedWs)
            ? focusedWs.FocusedTabId?.Value
            : null;
        string? muxFocusedPaneId = null;
        if (focusedTabId is not null
            && snap.Tabs.TryGetValue(focusedTabId, out var muxFocusedTab))
        {
            muxFocusedPaneId = muxFocusedTab.FocusedPaneId?.Value;
        }

        // Live keys retained + additive placement/persistence fields.
        // protocol_version stays a JSON number (ProtocolVersion.Current) on the live path.
        var snapshot = new JsonObject
        {
            ["session_id"] = snap.Id.Value,
            ["protocol_version"] = snap.ProtocolVersion,
            ["focused_workspace_id"] = snap.FocusedWorkspaceId?.Value,
            ["focused_tab_id"] = focusedTabId,
            ["mux_focused_workspace_id"] = snap.FocusedWorkspaceId?.Value,
            ["mux_focused_tab_id"] = focusedTabId,
            ["mux_focused_pane_id"] = muxFocusedPaneId,
            ["workspaces"] = workspaces,
            ["tabs"] = tabs,
            ["panes"] = panes,
            ["started_at"] = snap.StartedAt.ToString("O"),
            ["session_state"] = snap.LifecycleState,
            ["placement"] = snap.Placement,
            ["placement_generation"] = snap.PlacementGeneration,
            ["persistence_schema"] = RuntimePersistenceSchema.Version,
            ["replay_complete"] = snap.ReplayComplete,
            ["event_seq"] = _journal?.NextSeq ?? snap.NextEventSeq,
            ["attach_client_ids"] = LiveAttachClientIds(),
        };
        var titleOverride = WindowTitleOverride;
        if (titleOverride is not null)
            snapshot["window_title_override"] = titleOverride;
        WriteAgentViewSnapshot(snapshot);
        var popup = PopupSnapshotJson();
        if (popup is not null)
            snapshot["popup"] = popup;
        var projectionRevision = _attachSurfaceInterest.SessionProjectionRevision();
        if (projectionRevision == 0)
            projectionRevision = (ulong)Math.Max(1, snap.PlacementGeneration);
        snapshot["revision"] = projectionRevision;
        snapshot["projection_revision"] = projectionRevision;
        snapshot["boot_id"] = _attachSurfaceInterest.BootId;
        snapshot["worktree_directory"] = WorktreePathRules.ExpandTilde(
            AttachConfig.Worktrees.Directory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var attachClients = new JsonArray();
        var attachClientIds = new JsonArray();
        foreach (var client in ListAttachClients())
        {
            attachClients.Add((JsonNode)client);
            if (client["attach_client_id"] is JsonValue id
                && id.GetValue<string>() is { Length: > 0 } attachClientId)
            {
                attachClientIds.Add((JsonNode)JsonValue.Create(attachClientId)!);
            }
        }

        snapshot["attach_clients"] = attachClients;
        snapshot["attach_client_ids"] = attachClientIds;
        var resources = SnapshotPluginResources();
        if (resources is not null)
            snapshot["resources"] = resources;
        OverlayAttachClientView(snapshot, connection);
        return snapshot;
    }

    private void OverlayAttachClientView(JsonObject snapshot, IClientConnection? connection)
    {
        if (connection is null)
            return;
        var view = _attachClientViews.Get(connection.ConnectionId);
        if (view is null)
            return;

        snapshot["focused_workspace_id"] = view.FocusedWorkspaceId;
        snapshot["focused_tab_id"] = view.FocusedTabId();
        if (snapshot["workspaces"] is JsonArray workspaces)
        {
            foreach (var node in workspaces)
            {
                if (node is not JsonObject workspace
                    || workspace["workspace_id"]?.GetValue<string>() is not { Length: > 0 } workspaceId)
                {
                    continue;
                }

                if (view.ActiveTabIds.TryGetValue(workspaceId, out var tabId))
                    workspace["focused_tab_id"] = tabId;
            }
        }

        if (snapshot["tabs"] is JsonArray tabs)
        {
            foreach (var node in tabs)
            {
                if (node is not JsonObject tab
                    || tab["tab_id"]?.GetValue<string>() is not { Length: > 0 } tabId)
                {
                    continue;
                }

                tab["focused_pane_id"] = view.FocusedPaneIds.TryGetValue(tabId, out var paneId)
                    ? paneId
                    : null;
            }
        }
    }

    private AttachClientTopology CurrentAttachTopology() =>
        AttachClientTopologyFactory.FromState(_state);

    private void ReconcileAttachClientViews() =>
        _attachClientViews.ReconcileAll(CurrentAttachTopology());

    private void RememberAttachClientSelection(
        IClientConnection? connection,
        string? workspaceId,
        string? tabId,
        string? paneId)
    {
        if (connection is null)
            return;
        var interest = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        if (interest is null)
            return;

        var applied = _attachClientViews.ApplyFocus(
            new AttachClientViewFocusRequest
            {
                ConnectionId = connection.ConnectionId,
                ClientId = interest.ClientId,
                ConnectionGeneration = interest.ConnectionGeneration,
                WorkspaceId = workspaceId,
                TabId = tabId,
                PaneId = paneId,
            },
            CurrentAttachTopology());
        if (!applied.IsOk || !applied.Value.Changed)
            return;

        _ = _attachSurfaceInterest.ConsumeProjectionFloor(connection.ConnectionId, interest.ClientId);
    }

    private JsonArray LiveAttachClientIds()
    {
        var ids = _attachments.ListAll()
            .Select(a => a.ConnectionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var nodes = new JsonNode[ids.Length];
        for (var i = 0; i < ids.Length; i++)
            nodes[i] = JsonValue.Create(ids[i])!;
        return new JsonArray(nodes);
    }

    private async Task<JsonObject> WorkspaceCreateAsync(
        WorkspaceCreateParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("workspace.create");
        var cwd = ResolveWorkspaceCwd(p.Cwd);
        var label = RejectUnsafeLabel(EmptyToNull(p.Label));
        var explicitBinding = ReadBinding(p.Binding, p.AgentSessionId, p.RunId, p.StepId, p.TenantId, p.MemoryId, p.ProjectRoot);
        var createPane = p.CreatePane ?? true;

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        WorkspaceState ws;
        AtomicBinding? paneBinding;
        try
        {
            // Re-check freeze under the same gate as the graph write.
            EnsureAttachPaneMutation(connection, "workspace.create");
            EnsureNotFrozenForMutation("workspace.create");

            // Fail closed before mutating graph: workspace bind is session-scoped; default pane
            // inherits explicit → session and must pass pane-scoped matrix when present.
            var wsBinding = AdmitBinding(explicitBinding, paneScoped: false);
            paneBinding = null;
            if (createPane)
            {
                var inherited = explicitBinding ?? _state.Snapshot().Binding;
                paneBinding = AdmitBinding(inherited, paneScoped: true);
            }

            ws = _state.CreateWorkspace(cwd, label, wsBinding, defaultPanePending: createPane);

            if (createPane)
            {
                // Re-resolve after workspace exists (ws.Binding may now hold the admit result).
                // Process anchors are NOT pinned here — SpawnPaneAsync captures only after
                // successful RegisterPane + start so a failed default pane cannot leave sticky
                // ProcessTenantId/ProcessRunId on a workspace with no live pane.
                paneBinding = AdmitBinding(ResolveInheritedBinding(explicitBinding, ws), paneScoped: true);
            }
            else
            {
                // Workspace-only apply (no pane): pin anchors after the graph write succeeds.
                CaptureProcessAnchorsIfGoverned(wsBinding);
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        JsonObject? paneJson = null;
        if (createPane)
        {
            try
            {
                var command = p.Command ?? string.Empty;
                var args = p.Args ?? [];
                paneJson = await SpawnPaneAsync(ws, command, args, label, paneBinding, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await RollbackFailedDefaultPaneWorkspaceAsync(ws.Id, ct).ConfigureAwait(false);
                throw;
            }
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);

        await FlushExpiredMetadataAsync(ct).ConfigureAwait(false);
        StartWorkspaceDirectoryTracking();
        RequestWorkspaceDirectoryRefresh();
        var result = WorkspaceToJson(ws);
        if (paneJson is not null)
            result["pane"] = paneJson;
        return result;
    }

    /// <summary>
    /// Default-pane create writes the workspace before spawn. If spawn fails,
    /// drop that workspace so restart does not restore a row with no PTY.
    // / in-memory list in the same close path.
    /// marks dirty then saves later. Hypa refuses last-workspace close, so
    /// create a known-good replacement before removing the failed row.
    /// </summary>
    private async Task RollbackFailedDefaultPaneWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var failed = _state.GetWorkspace(workspaceId);
            if (failed is null)
                return;

            if (_state.IsLastWorkspace())
            {
                _state.CreateWorkspace(
                    failed.Cwd,
                    label: null,
                    failed.Binding,
                    defaultPanePending: false);
            }

            if (_state.CloseWorkspace(workspaceId) == CloseWorkspaceOutcome.Closed)
                _metadata.Drop("workspace", workspaceId.Value);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: false, removeMissingPanes: true).ConfigureAwait(false);
    }

    private JsonArray WorkspaceList()
    {
        var arr = new JsonArray();
        foreach (var ws in _state.ListWorkspaces())
            arr.Add((JsonNode)WorkspaceToJson(ws));
        return arr;
    }

    private JsonObject WorkspaceGet(WorkspaceGetParams p)
    {
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var ws = _state.GetWorkspace(new WorkspaceId(id))
            ?? throw new ControlPlaneException(-32004, $"Workspace not found: {id}");
        return WorkspaceToJson(ws);
    }

    private async Task<JsonObject> PaneCreateAsync(
        PaneCreateParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (IsShuttingDown)
            throw new ControlPlaneException(
                ProtocolErrorCodes.ServerShuttingDown,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
        EnsureNotFrozenForMutation("pane.create");

        var explicitBinding = ReadBinding(p.Binding, p.AgentSessionId, p.RunId, p.StepId, p.TenantId, p.MemoryId, p.ProjectRoot);
        var workspaceId = EmptyToNull(p.WorkspaceId);
        var command = p.Command ?? string.Empty;
        var args = p.Args ?? [];
        var label = RejectUnsafeLabel(EmptyToNull(p.Label));
        var placement = ParsePanePlacement(p.Placement);
        var parentProof = _placement.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = EmptyToNull(p.ParentPaneId),
            OccupantToken = EmptyToNull(p.OccupantToken),
            LeaseId = EmptyToNull(p.LeaseId),
            HolderId = connection?.ConnectionId,
        });
        if (!parentProof.IsOk)
        {
            throw new ControlPlaneException(
                parentProof.Error.Code == PlacementAuthorityError.UnprovenParent.Code
                    || parentProof.Error.Code == PlacementAuthorityError.MissingSequence.Code
                    ? ProtocolErrorCodes.InvalidParams
                    : ProtocolErrorCodes.CapabilityInvalid,
                parentProof.Error.Message);
        }

        // Workspace resolution + admit stay fail-fast under the gate; SpawnPaneAsync
        // re-admits and RegisterPane under the same gate so concurrent creates still
        // see each other's pane bindings without pinning process anchors early.
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        WorkspaceState ws;
        AtomicBinding? binding;
        var createdWorkspace = false;
        try
        {
            // Re-check freeze under the same gate as the graph write.
            EnsureAttachPaneMutation(connection, "pane.create");
            EnsureNotFrozenForMutation("pane.create");

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                var list = _state.ListWorkspaces();
                if (list.Count == 0)
                {
                    // New workspace may carry session-level bind; validate as session scope.
                    // Do NOT pin process anchors here — this path always continues to
                    // SpawnPaneAsync, which captures anchors only after successful start.
                    var wsBinding = AdmitBinding(explicitBinding, paneScoped: false);
                    ws = _state.CreateWorkspace(
                        ResolveWorkspaceCwd(p.Cwd),
                        label,
                        wsBinding,
                        defaultPanePending: true);
                    createdWorkspace = true;
                }
                else
                {
                    ws = list[0];
                }
            }
            else
            {
                ws = _state.GetWorkspace(new WorkspaceId(workspaceId))
                    ?? throw new ControlPlaneException(-32004, $"Workspace not found: {workspaceId}");
            }

            // Resolve explicit → workspace → session; validate pane-scoped matrix on write.
            binding = AdmitBinding(
                ResolveInheritedBinding(explicitBinding, ws),
                paneScoped: true);

            var tabId = ws.FocusedTabId ?? ws.TabIds.FirstOrDefault();
            if (placement != PanePlacement.Hidden
                && tabId.Value is not null
                && _state.GetTab(tabId) is { } occupied
                && occupied.PaneIds.Count > 0)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    "use pane.split");
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        try
        {
            return await SpawnPaneAsync(
                    ws, command, args, label, binding, ct,
                    placement: placement,
                    parentPaneId: parentProof.Value)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (createdWorkspace)
                await RollbackFailedDefaultPaneWorkspaceAsync(ws.Id, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Atomic-ish pane start: re-admit + RegisterPane under the binding gate (so concurrent
    /// creates see each other's pane bindings for process singularity), then start outside
    /// the gate. Post-start graph mutations (UpdatePane, CaptureProcessAnchors, PersistGraph)
    /// and fail-spawn RemovePane re-acquire the gate with <see cref="EnsureNotFrozenForMutation"/>
    /// so prepare freeze is a hard barrier. Process tenant/run anchors are captured
    /// only after a successful start so NotFound / failed spawn cannot pin sticky anchors.
    /// </summary>
    private async Task<JsonObject> SpawnPaneAsync(
        WorkspaceState ws,
        string command,
        IReadOnlyList<string> args,
        string? label,
        AtomicBinding? binding,
        CancellationToken ct,
        TabId? tabIdOverride = null,
        bool persistAfterStart = true,
        IReadOnlyDictionary<string, string>? env = null,
        string? cwd = null,
        Func<PaneId, CancellationToken, ValueTask>? beforeStart = null,
        PanePlacement placement = PanePlacement.Tiled,
        PaneId? parentPaneId = null)
    {
        if (IsShuttingDown)
            throw new ControlPlaneException(
                ProtocolErrorCodes.ServerShuttingDown,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));

        var tabId = tabIdOverride ?? ws.FocusedTabId ?? ws.TabIds.FirstOrDefault();
        if (tabId.Value is null)
            throw new ControlPlaneException(-32005, "Workspace has no tab");

        var paneId = PaneId.New();
        var cols = VtFloorDefaults.DefaultCols;
        var rows = VtFloorDefaults.DefaultRows;
        label = RejectUnsafeLabel(label);
        var resolvedLabel = label ?? (string.IsNullOrEmpty(command) ? "shell" : Path.GetFileName(command));
        var spawnCwd = string.IsNullOrWhiteSpace(cwd)
            ? ws.Cwd
            : ResolveWorkspaceCwd(cwd);

        var state = new PaneState
        {
            Id = paneId,
            TabId = tabId,
            WorkspaceId = ws.Id,
            Label = resolvedLabel,
            Cwd = spawnCwd,
            Command = command,
            Args = args,
            Cols = cols,
            Rows = rows,
            Binding = binding,
            Placement = placement,
            ParentPaneId = parentPaneId,
            IsAlive = false,
            LifecycleState = PaneLifecycle.Starting,
            AgentStatus = AgentStatus.Working,
            // Admit first occupant as generation 1 at register.
            // Concurrent agent.wait must never pin generation 0; bootstrap must not
            // look like occupant_replaced. Failed starts roll the pane back entirely.
            OccupantGeneration = 1,
        };

        // Hold binding gate through re-admit + RegisterPane so concurrent pane.create
        // singularity checks observe this pane's binding via ResolveProcessBindingAnchors
        // without capturing sticky process anchors until start succeeds. Always re-admit
        // (including null) so remote/governed fail closed if callers pass an unbound pane.
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check freeze under RegisterPane gate (prepare may freeze between admit and spawn).
            EnsureNotFrozenForMutation("pane.create");

            // workspace.close / tab.close may delete the graph between CreateWorkspace
            // and this RegisterPane. Do not throw InvalidOperationException (-32603 leak).
            if (_state.GetWorkspace(ws.Id) is null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Workspace not found: {ws.Id.Value}");
            }

            if (_state.GetTab(tabId) is null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Tab not found: {tabId.Value}");
            }

            AdmitBinding(binding, paneScoped: true);

            try
            {
                if (placement == PanePlacement.Hidden)
                    _visibility.RegisterHidden(state);
                else
                    _state.RegisterPane(state);
            }
            catch (InvalidOperationException)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Tab not found: {tabId.Value}");
            }

            if (binding is not null)
                _intelligence.BindAtomic(paneId, binding);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        var occupantToken = _placement.IssueOccupant(paneId, state.OccupantGeneration);
        var paneHome = ResolvePaneHome(env);
        IReadOnlyDictionary<string, string> spawnEnv;
        try
        {
            spawnEnv = EnsureLivePiResumeEnv(paneHome, env);
            OccupantResumeReporter.WritePiHomeExtension(paneHome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PaneStartFailed,
                "pane Pi resume reporter write failed: " + ex.Message);
        }

        var options = new PaneSpawnOptions
        {
            Id = paneId,
            Cwd = spawnCwd,
            Command = command,
            Args = args,
            // Hidden occupancy is Hypa-only; copy the identity sequence, not popup omit.
            Env = WithOccupantIdentity(WithManagedPaneId(spawnEnv, paneId), paneId, occupantToken),
            Cols = cols,
            Rows = rows,
            ScrollbackLimitBytes = AttachConfig.Advanced.ScrollbackLimitBytes,
            Terminal = AttachConfig.Terminal,
            HostTheme = SnapshotHostTheme(),
        };

        IPaneRuntime? runtime = null;
        try
        {
            runtime = _paneFactory.Create(options);
            runtime.OutputReceived += OnRuntimeOutput;
            runtime.BellReceived += OnRuntimeBell;
            runtime.Exited += OnRuntimeExited;

            RegisterRuntimeOrThrow(paneId, runtime);

            // Close-on-exit overlay must be keyed before StartAsync so a fast occupant
            // (echo / true) cannot fire Exited against an empty map.
            if (beforeStart is not null)
                await beforeStart(paneId, ct).ConfigureAwait(false);

            ApplyThemeBeforeStart(runtime);

            // PTY start stays outside the gate (may block); freeze re-checked under gate after.
            await runtime.StartAsync(ct).ConfigureAwait(false);

            if (IsShuttingDown)
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));

            // Reconcile + anchors + durable graph under the same gate as prepare freeze so
            // CaptureProcessAnchors (process_tenant_id/process_run_id are in the fingerprint)
            // and PersistGraph cannot race past frozen_read_only.
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("pane.create");

                var alive = runtime.IsAlive;
                var exit = runtime.ExitCode;
                var pid = runtime.Pid;
                StampLiveResumeTokens(paneId.Value, spawnEnv, "pane.create");
                state = _state.UpdatePane(paneId, p => p with
                {
                    IsAlive = alive,
                    ExitCode = exit,
                    Pid = alive ? pid : null,
                    LifecycleState = alive ? PaneLifecycle.Running : PaneLifecycle.Exited,
                    // Concurrent pane.report_agent may have taken authority during StartAsync.
                    AgentStatus = HoldsSemanticAuthority(p)
                        ? p.StatusAfterProcessDeath()
                        : alive ? AgentStatus.Working : AgentStatus.Done,
                    AgentKind = p.AgentAuthority?.Agent ?? p.AgentKind,
                    AgentMessage = p.AgentAuthority?.Message ?? p.AgentMessage,
                    Home = paneHome,
                    // Defense: never leave a live pane at generation 0 after start.
                    OccupantGeneration = p.OccupantGeneration <= 0 ? 1 : p.OccupantGeneration,
                }) ?? throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Pane not found: {paneId.Value}");

                // Successful apply: pin process anchors only after start (not on admit alone).
                CaptureProcessAnchorsIfGoverned(binding);
                _state.ClearDefaultPanePending(ws.Id);

                if (persistAfterStart)
                    await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            await EmitLifecycleAsync(
                ProtocolEventTypes.PaneLifecycle,
                state.Id.Value,
                state.LifecycleState,
                state.OccupantGeneration,
                ct).ConfigureAwait(false);

            if (placement == PanePlacement.Hidden)
            {
                await EmitLayoutUpdatedAsync(state.TabId.Value, state.Id.Value, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Pane {PaneId} started command={Command} cwd={Cwd} alive={Alive}",
                paneId, command, ws.Cwd, state.IsAlive);
            return PaneToJson(state, _placement.IssueParentCapability(paneId));
        }
        catch (ControlPlaneException)
        {
            await FailSpawnCleanupAsync(paneId, runtime).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pane {PaneId} failed to start", paneId);
            await FailSpawnCleanupAsync(paneId, runtime).ConfigureAwait(false);
            throw PaneStartFailure();
        }
    }

    /// <summary>
    /// starts a fresh shell in the saved cwd. Do not reattach a PID. Do not
    /// allocate a new pane id. Frozen mux start returns without spawning.
    /// </summary>
    public Task RestoreSpawnAsync(CancellationToken ct) =>
        RestoreSpawnAsync(ct, restoreHome: null, directoryExists: null);

    /// <summary>Test hook: inject restore home and directory existence.</summary>
    internal async Task RestoreSpawnAsync(
        CancellationToken ct,
        string? restoreHome,
        Func<string, bool>? directoryExists)
    {
        // Restored pane-less workspaces register no runtime; track them too.
        StartWorkspaceDirectoryTracking();
        if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
            return;

        SessionHistorySnapshot? history = null;
        if (AttachConfig.Experimental.PaneHistory)
            history = _paneHistoryStore.Load();

        var home = restoreHome ?? ResolveRestoreHome();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var workspaces = _state.ListWorkspaces();
        for (var workspaceIndex = 0; workspaceIndex < workspaces.Count; workspaceIndex++)
        {
            var tabs = _state.ListTabs(workspaces[workspaceIndex].Id);
            for (var tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
            {
                var tab = tabs[tabIndex];
                foreach (var id in tab.PaneIds.Concat(tab.HiddenPaneIds))
                {
                    if (!seen.Add(id.Value))
                        continue;
                    var pane = _state.GetPane(id);
                    if (pane is null)
                        continue;

                    if (IsShuttingDown)
                        return;

                    lock (_gate)
                    {
                        if (_runtimes.ContainsKey(pane.Id.Value))
                            continue;
                    }

                    var savedHistory = PaneHistorySnapshotter.Lookup(
                        history,
                        workspaceIndex,
                        tabIndex,
                        pane.Id.Value);
                    var startup = PaneRestoreStartupPlanner.Plan(
                        pane.AgentSession,
                        savedHistory,
                        AttachConfig.Session.ResumeAgentsOnRestore);

                    try
                    {
                        await StartRuntimeIntoExistingPaneAsync(
                            pane,
                            home,
                            directoryExists,
                            startup.InitialHistoryAnsi,
                            ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "failed to restore pane, skipping pane_id={PaneId}",
                            pane.Id.Value);
                    }
                }
            }
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
    }

    private static string? ResolveRestoreHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home.Trim();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }

    /// <summary>
    /// Start a default shell into an existing pane row. Copies the SpawnPaneAsync
    /// create/wire/register/start sequence
    /// Does not RegisterPane, does not rewrite layout, does not exec saved
    /// launch argv, does not bump occupant identity, does not RemovePane
    /// on failure.
    /// </summary>
    private async Task StartRuntimeIntoExistingPaneAsync(
        PaneState pane,
        string? home,
        Func<string, bool>? directoryExists,
        string? initialHistoryAnsi,
        CancellationToken ct)
    {
        var savedCwd = pane.Cwd;
        var cwd = TerminalSpawnPolicy.ResolveRestoreCwd(savedCwd, home, directoryExists);
        var usedHomeFallback = !string.IsNullOrWhiteSpace(home)
            && string.Equals(cwd, home, StringComparison.Ordinal)
            && !string.Equals(cwd, savedCwd, StringComparison.Ordinal);
        if (!string.Equals(cwd, savedCwd, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "saved pane cwd does not exist, falling back to HOME pane_id={PaneId} cwd={Cwd}",
                pane.Id.Value,
                savedCwd);
        }

        // not saved launch_argv. Keep command/args on the pane row.
        var (shell, shellArgs) = TerminalSpawnPolicy.ResolveEmptyCommand(
            AttachConfig.Terminal,
            Environment.GetEnvironmentVariable("SHELL"),
            OperatingSystem.IsMacOS());
        var options = new PaneSpawnOptions
        {
            Id = pane.Id,
            Cwd = cwd,
            Command = shell,
            Args = shellArgs,
            Env = WithOccupantIdentity(
                WithManagedPaneId(null, pane.Id),
                pane.Id,
                _placement.IssueOccupant(pane.Id, pane.OccupantGeneration)),
            Cols = pane.Cols,
            Rows = pane.Rows,
            ScrollbackLimitBytes = AttachConfig.Advanced.ScrollbackLimitBytes,
            Terminal = AttachConfig.Terminal,
            InitialHistoryAnsi = initialHistoryAnsi,
        };

        IPaneRuntime? runtime = null;
        var registered = false;
        try
        {
            runtime = _paneFactory.Create(options);
            runtime.OutputReceived += OnRuntimeOutput;
            runtime.BellReceived += OnRuntimeBell;
            runtime.Exited += OnRuntimeExited;
            RegisterRuntimeOrThrow(pane.Id, runtime);
            registered = true;
            await runtime.StartAsync(ct).ConfigureAwait(false);
            QueueOfficialAgentResume(pane);

            if (IsShuttingDown)
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));

            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("restore.spawn");
                var alive = runtime.IsAlive;
                var updated = _state.UpdatePane(pane.Id, p => p with
                {
                    IsAlive = alive,
                    ExitCode = runtime.ExitCode,
                    Pid = alive ? runtime.Pid : null,
                    LifecycleState = alive ? PaneLifecycle.Running : PaneLifecycle.Exited,
                    Cwd = cwd,
                    Home = usedHomeFallback ? home : p.Home,
                    OccupantGeneration = p.OccupantGeneration <= 0 ? 1 : p.OccupantGeneration,
                    AgentStatus = HoldsSemanticAuthority(p)
                        ? p.StatusAfterProcessDeath()
                        : alive ? AgentStatus.Working : AgentStatus.Done,
                    AgentKind = HoldsSemanticAuthority(p)
                        ? p.AgentAuthority?.Agent ?? p.AgentKind
                        : null,
                    AgentMessage = HoldsSemanticAuthority(p)
                        ? p.AgentAuthority?.Message ?? p.AgentMessage
                        : null,
                });
                if (updated is null)
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        $"Pane not found: {pane.Id.Value}");

                await EmitLifecycleAsync(
                    ProtocolEventTypes.PaneLifecycle,
                    updated.Id.Value,
                    updated.LifecycleState,
                    updated.OccupantGeneration,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _bindingMutationGate.Release();
            }
        }
        catch
        {
            if (runtime is not null)
            {
                runtime.OutputReceived -= OnRuntimeOutput;
                runtime.BellReceived -= OnRuntimeBell;
                runtime.Exited -= OnRuntimeExited;
                if (registered)
                {
                    lock (_gate)
                        _runtimes.Remove(pane.Id.Value);
                }

                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dispose after failed restore for {PaneId}", pane.Id);
                }
            }

            RevertRestorePaneAfterFailedStart(pane.Id);
            throw;
        }
    }

    /// <summary>
    /// Restore spawn must not persist a live row after the runtime is dropped
    // Keep identity; mark orphaned.
    /// </summary>
    private void RevertRestorePaneAfterFailedStart(PaneId paneId)
    {
        _state.UpdatePane(paneId, p => p with
        {
            IsAlive = false,
            Pid = null,
            LifecycleState = PaneLifecycle.Orphaned,
        });
    }

    /// <summary>
    /// Admits a runtime into the map only if shutdown has not begun.
    /// Must hold the same gate as snapshot/clear in <see cref="ShutdownAsync"/>.
    /// Does not overwrite a different occupant: concurrent replace must swap
    /// under <see cref="SwapCommittedRuntimeOrThrow"/> so the previous process
    /// is not leaked.
    /// </summary>
    private void RegisterRuntimeOrThrow(PaneId paneId, IPaneRuntime runtime)
    {
        lock (_gate)
        {
            if (IsShuttingDown)
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
            if (_runtimes.TryGetValue(paneId.Value, out var existing)
                && !ReferenceEquals(existing, runtime))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    $"Pane already has a runtime: {paneId.Value}");
            }

            _runtimes[paneId.Value] = runtime;
            StartWorkspaceDirectoryTracking();
        }
    }

    /// <summary>
    /// Commit a replacement occupant. Caller holds the per-pane admit lock and
    /// <see cref="_bindingMutationGate"/>. The previous runtime stays registered
    /// until this swap so a failed start can keep the live occupant.
    /// Does not insert when close already removed the previous occupant.
    /// </summary>
    private IPaneRuntime? SwapCommittedRuntimeOrThrow(
        PaneId paneId,
        IPaneRuntime incoming,
        IPaneRuntime? expectedPrevious)
    {
        lock (_gate)
        {
            if (IsShuttingDown)
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));

            _runtimes.TryGetValue(paneId.Value, out var current);
            if (current is null && expectedPrevious is not null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Pane not found: {paneId.Value}");
            }

            if (current is not null
                && expectedPrevious is not null
                && !ReferenceEquals(current, expectedPrevious)
                && !ReferenceEquals(current, incoming))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    $"Pane runtime replaced concurrently: {paneId.Value}");
            }

            if (current is not null && !ReferenceEquals(current, incoming))
            {
                current.OutputReceived -= OnRuntimeOutput;
                current.BellReceived -= OnRuntimeBell;
                current.Exited -= OnRuntimeExited;
            }

            _runtimes[paneId.Value] = incoming;
            ClearPendingReplacementUnlocked(paneId, incoming);
            ClearReplacementInFlightUnlocked(paneId.Value);
            CloseInputActor(paneId.Value);
            _detectionScanner.Forget(paneId.Value);
            return ReferenceEquals(current, incoming) ? expectedPrevious : current;
        }
    }

    /// <summary>
    /// Track an uncommitted replacement so <see cref="ShutdownAsync"/> can dispose it.
    /// </summary>
    private void TrackPendingReplacementOrThrow(PaneId paneId, IPaneRuntime runtime)
    {
        lock (_gate)
        {
            if (IsShuttingDown)
                throw new ControlPlaneException(
                    ProtocolErrorCodes.ServerShuttingDown,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.ServerShuttingDown));
            if (_pendingRuntimes.TryGetValue(paneId.Value, out var existing)
                && !ReferenceEquals(existing, runtime))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    $"Pane already has a pending replacement: {paneId.Value}");
            }

            _pendingRuntimes[paneId.Value] = runtime;
        }
    }

    private void ClearPendingReplacementUnlocked(PaneId paneId, IPaneRuntime runtime)
    {
        if (_pendingRuntimes.TryGetValue(paneId.Value, out var current)
            && ReferenceEquals(current, runtime))
            _pendingRuntimes.Remove(paneId.Value);
    }

    private void MarkReplacementInFlightUnlocked(string paneId) =>
        _uncommittedReplacements.Add(paneId);

    private void ClearReplacementInFlightUnlocked(string paneId) =>
        _uncommittedReplacements.Remove(paneId);

    private bool HasUncommittedReplacement(string paneId)
    {
        lock (_gate)
            return HasUncommittedReplacementUnlocked(paneId);
    }

    private bool HasUncommittedReplacementUnlocked(string paneId) =>
        _uncommittedReplacements.Contains(paneId) || _pendingRuntimes.ContainsKey(paneId);

    /// <summary>
    /// Take the registered occupant and any uncommitted replacement. Caller disposes.
    /// </summary>
    private List<IPaneRuntime> TakePaneRuntimesUnlocked(string id)
    {
        var taken = new List<IPaneRuntime>(2);
        IPaneRuntime? runtime = null;
        if (_runtimes.Remove(id, out runtime))
            taken.Add(runtime);
        if (_pendingRuntimes.Remove(id, out var pending)
            && !ReferenceEquals(pending, runtime))
            taken.Add(pending);
        _uncommittedReplacements.Remove(id);
        return taken;
    }

    private bool IsRegisteredOccupantUnlocked(IPaneRuntime runtime) =>
        _runtimes.TryGetValue(runtime.Id.Value, out var current)
        && ReferenceEquals(current, runtime);

    /// <summary>
    /// True when <paramref name="runtime"/> may write detection or exit onto the live pane.
    /// Check the in-flight set first so a post-swap outgoing callback cannot observe a
    /// cleared flag and a stale registration. Safe to call while the AppState lock is
    /// held. Must not call into <see cref="_state"/> — never take AppState under
    /// <see cref="_gate"/>.
    /// </summary>
    private bool ShouldApplyOccupantStatus(IPaneRuntime runtime)
    {
        lock (_gate)
        {
            if (HasUncommittedReplacementUnlocked(runtime.Id.Value))
                return false;
            return IsRegisteredOccupantUnlocked(runtime);
        }
    }

    private List<KeyValuePair<string, IPaneRuntime>> SnapshotOwnedRuntimesUnlocked()
    {
        var snapshot = new List<KeyValuePair<string, IPaneRuntime>>(
            _runtimes.Count + _pendingRuntimes.Count);
        var seen = new HashSet<IPaneRuntime>(ReferenceEqualityComparer.Instance);
        foreach (var kv in _runtimes)
        {
            snapshot.Add(kv);
            seen.Add(kv.Value);
        }

        foreach (var kv in _pendingRuntimes)
        {
            if (seen.Add(kv.Value))
                snapshot.Add(kv);
        }

        return snapshot;
    }

    /// <summary>
    /// Re-register the previous occupant after a failed replace. No-op when the
    /// previous runtime is still the map entry.
    /// </summary>
    private void RestoreRuntimeIfAbsent(PaneId paneId, IPaneRuntime? previous)
    {
        if (previous is null)
            return;

        lock (_gate)
        {
            if (IsShuttingDown)
                return;
            if (_runtimes.TryGetValue(paneId.Value, out var current))
            {
                if (ReferenceEquals(current, previous))
                    return;
                return;
            }

            _runtimes[paneId.Value] = previous;
        }
    }

    private async Task DisposeRuntimeQuietlyAsync(IPaneRuntime? runtime, string reason, PaneId paneId)
    {
        if (runtime is null)
            return;

        runtime.OutputReceived -= OnRuntimeOutput;
        runtime.BellReceived -= OnRuntimeBell;
        runtime.Exited -= OnRuntimeExited;
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Reason} for {PaneId}", reason, paneId);
        }
    }

    private async Task FailSpawnCleanupAsync(PaneId paneId, IPaneRuntime? runtime)
    {
        // Always tear down the runtime process (not a session-graph fingerprint mutation).
        if (runtime is not null)
        {
            runtime.OutputReceived -= OnRuntimeOutput;
            runtime.BellReceived -= OnRuntimeBell;
            runtime.Exited -= OnRuntimeExited;
            lock (_gate)
                _runtimes.Remove(paneId.Value);
            _ = TakeCommandOverlay(paneId.Value);
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dispose after failed start for {PaneId}", paneId);
            }
        }
        else
        {
            lock (_gate)
                _runtimes.Remove(paneId.Value);
            _ = TakeCommandOverlay(paneId.Value);
        }

        // Graph RemovePane / PersistGraph must serialize with prepare freeze under the
        // binding mutation gate. If frozen_read_only won after RegisterPane, the pane is
        // already in the checkpoint fingerprint — silent RemovePane would rewrite a frozen
        // graph. Fail closed: leave the pane row, mark exited in-memory only.
        await _bindingMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
            {
                _intelligence.RemovePane(paneId);
                // Lifecycle is excluded from SessionGraphFingerprint; mark dead without RemovePane.
                _ = _state.UpdatePane(paneId, p =>
                {
                    var nextStatus = StatusAfterProcessDeath(p);
                    return p with
                    {
                        IsAlive = false,
                        ExitCode = p.ExitCode ?? -1,
                        Pid = null,
                        LifecycleState = PaneLifecycle.Exited,
                        AgentStatus = nextStatus,
                        AgentKind = p.AgentAuthority?.Agent ?? p.AgentKind,
                        AgentMessage = p.AgentAuthority?.Message ?? p.AgentMessage,
                        Seen = SeenAfterStatus(p, nextStatus),
                        UpdatedAt = _time.GetUtcNow(),
                    };
                });
                // Side effects (leases) are not fingerprint fields; free them.
                await CleanupPaneSideEffectsAsync(paneId.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            _intelligence.RemovePane(paneId);
            _metadata.Drop("pane", paneId.Value);
            _state.RemovePane(paneId);
            // Spawn may have been visible long enough for dual-client lease/observe; free side effects.
            await CleanupPaneSideEffectsAsync(paneId.Value, CancellationToken.None).ConfigureAwait(false);

            // Drop any durable row written by a concurrent best-effort PersistGraphAsync
            // (or a failed requireDurable write after StartAsync) so restart cannot restore
            // an orphaned phantom pane that only ever existed as lifecycle=starting.
            await PersistGraphAsync(requireDurable: false, removeMissingPanes: true)
                .ConfigureAwait(false);
        }
        finally
        {
            _bindingMutationGate.Release();
        }
    }

    private void OnRuntimeOutput(IPaneRuntime runtime, ReadOnlyMemory<byte> data)
    {
        bool apply;
        bool emit;
        lock (_gate)
        {
            // In-flight first, then identity, under one gate.
            var inFlight = HasUncommittedReplacementUnlocked(runtime.Id.Value);
            emit = IsRegisteredOccupantUnlocked(runtime);
            apply = emit && !inFlight;
        }

        if (apply)
        {
            MarkDetection(runtime);
            RequestWorkspaceDirectoryRefresh();

            try
            {
                // Origin-only: growing scrollback while following live (offset=0)
                // must not journal a reliable event per output chunk.
                MaybeEmitScrollChanged(runtime, originOnly: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Scroll emit failed for {PaneId}", runtime.Id);
            }
        }

        // Emit terminal.output (reliability=output).: redaction hook before durable write.
        // Outgoing I/O still starts an emit during replace; incoming uncommitted I/O does not.
        // EmitOutputAsync re-checks occupant identity under the pane emit gate so an
        // in-flight chunk cannot paint or journal after a committed swap.
        if (emit && !data.IsEmpty)
        {
            // Capture the feed-bound 2026 decision on this thread.
            // the same process_pty_bytes call. Do not re-read DEC 2026
            // after DelayOutputEmitAsync.
            long feedGeneration = 0L;
            var requestPaint = true;
            if (runtime is IPaneVtSnapshot snap)
            {
                var decision = snap.LastFeedPaintDecision;
                feedGeneration = decision.FeedGeneration;
                requestPaint = decision.RequestPaint;
                // thread before the next PTY read. Do not wait for emit.
                // no target can see the source. Do not queue a coalescer
                // paint that no live observer can see.
                if (requestPaint && ShouldCaptureLive(runtime.Id.Value))
                    _renderCoalescer.RequestPty(runtime.Id.Value, feedGeneration);
                else if (runtime is IPaneVtSnapshot parked)
                    MaybeEmitParkedScrollMetrics(parked);
            }

            BeginOutputEmit(runtime.Id.Value);
            try
            {
                EnqueuePaneOutput(runtime, data, feedGeneration, requestPaint);
            }
            catch
            {
                EndOutputEmit(runtime.Id.Value);
                throw;
            }
        }
    }

    /// <summary>
    /// Copy the borrowed PTY slice into a pooled chunk and hand it to one
    // / drain loop per pane.
    /// on the reader thread and signals one render task. One busy pane
    /// holds one drain task, not one task for each read.
    /// </summary>
    private void EnqueuePaneOutput(
        IPaneRuntime runtime,
        ReadOnlyMemory<byte> data,
        long feedGeneration,
        bool requestPaint)
    {
        var paneId = runtime.Id.Value;
        var length = data.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        Interlocked.Increment(ref _outputEmitChunkRents);
        try
        {
            data.Span.CopyTo(rented.AsSpan(0, length));
            var loop = _paneOutputLoops.GetOrAdd(paneId, static id => new PaneOutputEmitLoop(id));
            loop.EnsureStarted(this);
            var chunk = new PaneOutputChunk(runtime, rented, length, feedGeneration, requestPaint);
            if (loop.Writer.TryWrite(chunk))
                return;

            if (Interlocked.Exchange(ref loop.LoggedBackpressure, 1) == 0)
            {
                _logger.LogDebug(
                    "Pane {PaneId} output channel is full; PTY reader waits",
                    paneId);
            }

            loop.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            Interlocked.Increment(ref _outputEmitChunkReturns);
            throw;
        }
    }

    private async Task DrainPaneOutputAsync(PaneOutputEmitLoop loop)
    {
        try
        {
            await foreach (var chunk in loop.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await EmitOutputAsync(
                            chunk.Runtime,
                            chunk.AsMemory(),
                            chunk.FeedGeneration,
                            chunk.RequestPaint)
                        .ConfigureAwait(false);
                }
                finally
                {
                    chunk.Return();
                    Interlocked.Increment(ref _outputEmitChunkReturns);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pane {PaneId} output drain ended", loop.PaneId);
        }
    }

    private async Task StopPaneOutputLoopAsync(string paneId)
    {
        if (!_paneOutputLoops.TryRemove(paneId, out var loop))
            return;
        loop.Writer.TryComplete();
        if (loop.DrainTask is { } drain)
        {
            try
            {
                await drain.ConfigureAwait(false);
            }
            catch
            {
                // best-effort drain on pane close
            }
        }

        while (loop.Reader.TryRead(out var leftover))
        {
            leftover.Return();
            Interlocked.Increment(ref _outputEmitChunkReturns);
            EndOutputEmit(paneId);
        }
    }

    internal long OutputEmitChunkRents => Volatile.Read(ref _outputEmitChunkRents);

    internal long OutputEmitChunkReturns => Volatile.Read(ref _outputEmitChunkReturns);

    /// <summary>
    /// Stamp occupant generation at mark time. Recheck identity so a bump
    /// that lands after the output apply flag cannot mark the new generation.
    /// </summary>
    private void MarkDetection(IPaneRuntime runtime)
    {
        var occupantGeneration = _state.GetPane(runtime.Id)?.OccupantGeneration ?? 0;
        lock (_gate)
        {
            if (HasUncommittedReplacementUnlocked(runtime.Id.Value))
                return;
            if (!IsRegisteredOccupantUnlocked(runtime))
                return;
        }

        _detectionScanner.Mark(runtime.Id.Value, occupantGeneration);
    }

    /// <summary>
    /// Sample the occupant screen and publish status. Tick scans resolve the
    /// live occupant at scan time and skip when generation does not match the mark.
    /// Exit scans pass the dying runtime and the generation captured at exit.
    /// They must not look up a replacement occupant.
    /// Serialize per pane so a forced exit scan cannot overlap a tick.
    /// Do not take AppState under <see cref="_gate"/>.
    /// </summary>
    private void RunDetectionScan(string paneId, bool invokeApplyHook = true, int? markedGeneration = null, IPaneRuntime? expectedRuntime = null)
    {
        BeforeDetectionScan?.Invoke(paneId, invokeApplyHook);
        lock (GetPaneDetectionLock(paneId))
        {
            if (markedGeneration is int expectedGeneration)
            {
                var liveGeneration = _state.GetPane(new PaneId(paneId))?.OccupantGeneration ?? 0;
                if (liveGeneration != expectedGeneration)
                    return;
            }

            IPaneRuntime? runtime;
            lock (_gate)
            {
                if (HasUncommittedReplacementUnlocked(paneId))
                    return;
                if (expectedRuntime is not null)
                {
                    if (!IsRegisteredOccupantUnlocked(expectedRuntime))
                        return;
                    runtime = expectedRuntime;
                }
                else
                {
                    // Exit scans must pass the dying runtime. Do not resolve a
                    // different occupant when the generation stamp is absent.
                    if (!invokeApplyHook)
                        return;
                    if (!_runtimes.TryGetValue(paneId, out runtime))
                        return;
                }
            }

            if (invokeApplyHook)
                AfterOccupantStatusApplyDecision?.Invoke(runtime);

            var paneForGate = _state.GetPane(new PaneId(paneId));
            var generation = paneForGate?.OccupantGeneration ?? 0;
            if (markedGeneration is int marked && generation != marked)
                return;
            if (expectedRuntime is not null)
            {
                lock (_gate)
                {
                    if (!IsRegisteredOccupantUnlocked(expectedRuntime))
                        return;
                }

                if ((_state.GetPane(new PaneId(paneId))?.OccupantGeneration ?? 0) != generation)
                    return;
            }

            var holdsAuthority = paneForGate is not null && HoldsSemanticAuthority(paneForGate);

            try
            {
                var alive = runtime.IsAlive;
                DetectionResult? detection = null;
                var processCleared = false;
                if (!holdsAuthority && paneForGate is not null)
                    detection = DetectOccupant(runtime, paneForGate, out processCleared);
                // Exit samples the last screen, then death is authoritative. An
                // Unknown sample must not clobber Idle/Done before that write.
                // A confirmed process miss must still clear a stale kind.
                // known-agent fallback), so do not return solely because text
                // is empty.
                if (!invokeApplyHook
                    && !processCleared
                    && (detection is null || detection.Status == AgentStatus.Unknown))
                    return;
                AgentStatus? previous = null;
                var pane = _state.UpdatePane(new PaneId(paneId), p =>
                {
                    if (markedGeneration is int markedApply && p.OccupantGeneration != markedApply)
                        return p;
                    if (p.OccupantGeneration != generation)
                        return p;
                    if (!ShouldApplyOccupantStatus(runtime))
                        return p;

                    AgentStatus nextStatus;
                    string? nextKind;
                    string? nextMessage;
                    if (HoldsSemanticAuthority(p))
                    {
                        // Exit samples the last screen only. Death UpdatePane after
                        // the apply hook owns liveness so a bump can still overlap.
                        if (!invokeApplyHook)
                            return p;
                        if (!alive || p.ExitCode is not null)
                        {
                            return p with
                            {
                                IsAlive = false,
                                ExitCode = runtime.ExitCode ?? p.ExitCode,
                            };
                        }

                        return p with { IsAlive = true };
                    }

                    if (detection is null)
                        return p;

                    // Tick scans: do not let late output clobber a finished pane.
                    // Exit scans: sample the last screen; death stays on OnRuntimeExited.
                    if (invokeApplyHook && (!alive || p.ExitCode is not null))
                    {
                        nextStatus = StatusAfterProcessDeath(p);
                        nextKind = p.AgentKind;
                        nextMessage = p.AgentMessage;
                        if (nextStatus != p.AgentStatus
                            || !string.Equals(nextKind, p.AgentKind, StringComparison.Ordinal)
                            || !string.Equals(nextMessage, p.AgentMessage, StringComparison.Ordinal))
                        {
                            previous = p.AgentStatus;
                        }

                        return p with
                        {
                            IsAlive = false,
                            ExitCode = runtime.ExitCode ?? p.ExitCode,
                            AgentStatus = nextStatus,
                            AgentKind = nextKind,
                            AgentMessage = nextMessage,
                            Seen = SeenAfterStatus(p, nextStatus),
                        };
                    }

                    // drops the detection so the previous AgentStatus stays.
                    if (detection.SkipStateUpdate)
                        return p;

                    nextStatus = processCleared ? AgentStatus.Unknown : detection.Status;
                    nextKind = processCleared ? null : detection.AgentKind;
                    nextMessage = detection.Message;
                    if (nextStatus != p.AgentStatus
                        || !string.Equals(nextKind, p.AgentKind, StringComparison.Ordinal)
                        || !string.Equals(nextMessage, p.AgentMessage, StringComparison.Ordinal))
                    {
                        previous = p.AgentStatus;
                    }

                    return p with
                    {
                        AgentStatus = nextStatus,
                        AgentKind = nextKind,
                        AgentMessage = nextMessage,
                        Seen = SeenAfterStatus(p, nextStatus),
                        IsAlive = invokeApplyHook || p.IsAlive,
                    };
                });

                if (previous is not null && pane is not null)
                    _ = EmitAgentStatusChangedAsync(pane, previous.Value, CancellationToken.None);
                LatchForegroundShellExitIfPublished(paneId, detection, pane);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Detection update failed for {PaneId}", paneId);
            }
            finally
            {
                ArmOccupancyRecheck(paneId);
            }
        }
    }

    private void OnRuntimeBell(IPaneRuntime runtime, int count)
    {
        if (count <= 0)
            return;

        lock (_gate)
        {
            if (!IsRegisteredOccupantUnlocked(runtime))
                return;
        }

        _ = EmitPaneBellAsync(runtime, count);
    }

    private async Task EmitPaneBellAsync(IPaneRuntime runtime, int count)
    {
        if (_journal is null || count <= 0)
            return;

        var payload = RuntimeEventPayloadJson.WritePaneBell(runtime.Id.Value, count);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneBell, payload);

        try
        {
            await PublishReliableAsync(
                EventClass.Control,
                ProtocolEventTypes.PaneBell,
                payload,
                CancellationToken.None,
                stillValid: () =>
                {
                    lock (_gate)
                        return IsRegisteredOccupantUnlocked(runtime);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit pane.bell for {PaneId}", runtime.Id);
        }
    }

    private void OnRuntimeExited(IPaneRuntime runtime, int exitCode)
    {
        var generation = _state.GetPane(runtime.Id)?.OccupantGeneration ?? 0;
        bool apply;
        bool duringReplace;
        lock (_gate)
        {
            var inFlight = HasUncommittedReplacementUnlocked(runtime.Id.Value);
            var registered = IsRegisteredOccupantUnlocked(runtime);
            duringReplace = registered && inFlight;
            apply = registered && !inFlight;
            if (duringReplace)
            {
                runtime.OutputReceived -= OnRuntimeOutput;
                runtime.BellReceived -= OnRuntimeBell;
            }
        }

        if (!apply)
        {
            if (duringReplace)
            {
                _logger.LogInformation(
                    "Pane {PaneId} occupant exited during replace with {Code}",
                    runtime.Id,
                    exitCode);
            }

            return;
        }

        RunDetectionScan(
            runtime.Id.Value,
            invokeApplyHook: false,
            markedGeneration: generation,
            expectedRuntime: runtime);
        _detectionScanner.Forget(runtime.Id.Value);
        AfterOccupantStatusApplyDecision?.Invoke(runtime);

        var applied = false;
        AgentStatus? previous = null;
        var pane = _state.UpdatePane(runtime.Id, p =>
        {
            if (p.OccupantGeneration != generation)
                return p;
            if (!ShouldApplyOccupantStatus(runtime))
                return p;

            applied = true;
            var nextStatus = StatusAfterProcessDeath(p);
            if (nextStatus != p.AgentStatus)
                previous = p.AgentStatus;
            return p with
            {
                IsAlive = false,
                Pid = null,
                ExitCode = exitCode,
                LifecycleState = PaneLifecycle.Exited,
                AgentStatus = nextStatus,
                Seen = SeenAfterStatus(p, nextStatus),
                UpdatedAt = _time.GetUtcNow(),
            };
        });

        if (!applied)
            return;

        _placement.RevokeOccupant(runtime.Id);

        // Natural process exit is allowed while frozen_read_only: RPC mutations are denied,
        // but process death is not a control-plane graph mutation. Fingerprint excludes
        // pane lifecycle_state so exit cannot brick export with checkpoint_conflict.
        runtime.OutputReceived -= OnRuntimeOutput;
        runtime.BellReceived -= OnRuntimeBell;
        _ = PersistGraphAsync(requireDurable: false);
        if (pane is not null)
        {
            if (previous is not null)
                _ = EmitAgentStatusChangedAsync(pane, previous.Value, CancellationToken.None);
            _ = FlushThenEmitExitAsync(pane);
        }

        _logger.LogInformation("Pane {PaneId} exited with {Code}", runtime.Id, exitCode);
        ScheduleCommandOverlayClose(runtime.Id.Value);
        ReleaseOverlayPane(runtime.Id.Value);
        var ownerPlugin = OwnerPluginId(runtime.Id.Value);
        if (ownerPlugin is not null)
            DropPluginResources(ownerPlugin);
    }

    private JsonArray PaneList()
    {
        var arr = new JsonArray();
        foreach (var pane in _state.ListPanes())
            arr.Add((JsonNode)PaneToJson(pane));
        return arr;
    }

    private JsonObject PaneGet(PaneGetParams p)
    {
        var id = RequireField(p.PaneId, "pane_id");
        var pane = _state.GetPane(new PaneId(id))
            ?? throw new ControlPlaneException(-32004, $"Pane not found: {id}");
        return PaneToJson(pane);
    }

    private async Task<JsonObject> PaneSendTextAsync(
        PaneSendTextParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.send_text");
        EnsureAttachPaneMutation(connection, "pane.send_text");
        var id = RequireField(p.PaneId, "pane_id");
        var text = EmptyToNull(p.Text) ?? EmptyToNull(p.Input)
            ?? throw new ControlPlaneException(-32602, "text is required");

        var leaseId = EmptyToNull(p.LeaseId);
        AuthorizeInput(id, leaseId, connection?.ConnectionId);
        EnsureWorkGenerationAllowsInput(id);
        RejectStaleOverlayInput(id, connection, p.OverlayGeneration);

        if (!text.EndsWith('\n') && !text.EndsWith('\r'))
            text += "\n";

        if (InputBeforeBindingGateForTests is { } beforeGate)
            await beforeGate().ConfigureAwait(false);

        // Re-check freeze + generation under the same gate dest start uses to
        // Remember, so a stale write that waited cannot land after a newer generation.
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation("pane.send_text");
            EnsureWorkGenerationAllowsInput(id);
            var runtime = GetRuntime(id);
            await runtime.WriteTextAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        return new JsonObject { ["ok"] = true, ["pane_id"] = id };
    }

    private async Task<JsonObject> PaneSendKeysAsync(
        PaneSendKeysParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.send_keys");
        EnsureAttachPaneMutation(connection, "pane.send_keys");
        var id = RequireField(p.PaneId, "pane_id");
        var shellInput = TryAuthorizeClientShellPaneInput(id, connection);
        var leaseId = EmptyToNull(p.LeaseId);
        if (!shellInput)
            leaseId = RequireField(p.LeaseId, "lease_id");
        var data = RequireField(p.Data, "data");
        var encoding = (EmptyToNull(p.Encoding) ?? "base64").ToLowerInvariant();

        if (!shellInput)
            AuthorizeInput(id, leaseId, connection?.ConnectionId);
        EnsureWorkGenerationAllowsInput(id);
        RejectStaleOverlayInput(id, connection, p.OverlayGeneration);

        byte[] bytes;
        try
        {
            bytes = encoding switch
            {
                "base64" => Convert.FromBase64String(data),
                "utf8" or "text" => Encoding.UTF8.GetBytes(data),
                _ => throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    $"Unsupported encoding: {encoding}"),
            };
        }
        catch (FormatException)
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "Invalid base64 data");
        }

        // Per-pane admit lock serializes against agent.start occupant swap.
        // Generation re-check and key admission use the same binding gate dest
        // start uses to Remember. Lock order: pane admit → bindingMutationGate.
        var admit = await AcquirePaneAdmitAsync(id, ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (!shellInput)
                    AuthorizeInput(id, leaseId, connection?.ConnectionId);
                EnsureWorkGenerationAllowsInput(id);
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
            {
                ReleasePaneAdmitLock(id);
                throw;
            }

            if (InputBeforeBindingGateForTests is { } beforeGate)
                await beforeGate().ConfigureAwait(false);

            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureNotFrozenForMutation("pane.send_keys");
                if (shellInput && !TryAuthorizeClientShellPaneInput(id, connection))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "ClientShell input requires an active viewer of this pane");
                }

                EnsureWorkGenerationAllowsInput(id);
                var runtime = GetRuntime(id);
                var occupantGeneration = SnapshotOccupantGeneration(new PaneId(id));
                var actor = GetOrCreateInputActor(id, runtime, occupantGeneration);
                if (actor.OccupantGeneration != occupantGeneration
                    || !ReferenceEquals(actor.Runtime, runtime))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Input occupant changed during admission.");
                }

                if (!actor.TryAdmit(bytes))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Input admission queue is full or the occupant is closed.");
                }

                AttachPathTrace.RecordInput(AttachPathTrace.StageAdmit, id, bytes);
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainInput,
                    AttachPathTrace.StageAdmit,
                    id,
                    bytes.Length);
            }
            finally
            {
                _bindingMutationGate.Release();
            }
        }
        finally
        {
            SafeReleaseAdmit(admit);
        }

        if (shellInput)
            await ClaimShellTabGeometryOnInteractAsync(connection, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["pane_id"] = id,
            ["accepted_bytes"] = bytes.Length,
        };
    }

    private PaneInputActor GetOrCreateInputActor(string paneId, IPaneRuntime runtime, int occupantGeneration)
    {
        if (_inputActors.TryGetValue(paneId, out var existing)
            && ReferenceEquals(existing.Runtime, runtime)
            && existing.OccupantGeneration == occupantGeneration
            && existing.IsAccepting)
        {
            return existing;
        }

        var created = new PaneInputActor(
            runtime,
            occupantGeneration,
            onWriteSucceeded: () => _renderCoalescer.MarkInputWritten(paneId));
        var prior = _inputActors.AddOrUpdate(paneId, created, (key, previous) =>
        {
            if (ReferenceEquals(previous.Runtime, runtime)
                && previous.OccupantGeneration == occupantGeneration
                && previous.IsAccepting)
            {
                return previous;
            }

            previous.RejectNewAdmission();
            _ = PublishUndeliverableThenDisposeAsync(key, previous);
            return created;
        });
        if (!ReferenceEquals(prior, created))
            _ = PublishUndeliverableThenDisposeAsync(paneId, created);
        return prior;
    }

    private void CloseInputActor(string paneId)
    {
        if (_inputActors.TryRemove(paneId, out var actor))
        {
            actor.RejectNewAdmission();
            _ = PublishUndeliverableThenDisposeAsync(paneId, actor);
        }
    }

    private async Task PublishUndeliverableThenDisposeAsync(string paneId, PaneInputActor actor)
    {
        try
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pane input actor dispose failed for {PaneId}", paneId);
        }

        var undeliverable = actor.UndeliverableBytes;
        if (undeliverable > 0)
        {
            _closedInputUndeliverable.AddOrUpdate(
                paneId, undeliverable, (_, prev) => prev + undeliverable);
        }

        if (undeliverable <= 0 || _subscriptions is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteNotificationShown(
            "Input undeliverable",
            undeliverable.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes",
            "pane.input",
            "none",
            paneId,
            "input_undeliverable");
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.NotificationShown, payload);
        var subscriptions = _subscriptions;
        var occurredAt = _time.GetUtcNow();
        EmitInAllocationOrder(
            _inputUndeliverableEmitGate,
            () => _paneRenderSeq.AddOrUpdate(
                paneId + ":input_undeliverable", 1L, static (_, prev) => prev + 1),
            seq => subscriptions.PostLive(new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Control,
                Reliability = EventReliability.Reliable,
                Type = ProtocolEventTypes.NotificationShown,
                OccurredAt = occurredAt,
                PayloadJson = payload,
                Lane = WriterLaneNames.Control,
            }));
    }

    private async Task<JsonObject> PaneResizeAsync(
        PaneResizeParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.resize");
        EnsureAttachPaneMutation(connection, "pane.resize");
        var id = RequireField(p.PaneId, "pane_id");
        var cols = p.Cols ?? 0;
        var rows = p.Rows ?? 0;
        if (!VtFloorDefaults.TryValidateDimensions(cols, rows, out var dimError))
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                dimError ?? "cols and rows must be within the VT dimension budget");

        var leaseId = EmptyToNull(p.LeaseId);
        var shellResize = connection is not null && IsActiveShellClient(connection);
        if (shellResize)
        {
            var shell = connection!;
            var pane = _state.GetPane(new PaneId(id))
                ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
            var tabId = pane.TabId.Value;
            var overlayOwner = _overlay.OwnerOf(new PaneId(id));
            if (overlayOwner is not null && !CallerOwnsOverlay(shell, overlayOwner))
            {
                return PaneResizeResult(id, pane.Cols, pane.Rows, overlayOwner.AttachClientId);
            }

            // A newly shown tab has no geometry owner. The viewer of that
            // tab becomes the owner when it sends the content-box size.
            // A client focused on another tab does not take it.
            var viewsTab = !string.IsNullOrWhiteSpace(tabId)
                && string.Equals(
                    FocusedTabIdForConnection(shell.ConnectionId),
                    tabId,
                    StringComparison.Ordinal);
            if (viewsTab && !_tabGeometry.IsController(shell.ConnectionId, tabId))
                _tabGeometry.ClaimUnowned(shell.ConnectionId, tabId);

            if (string.IsNullOrWhiteSpace(tabId)
                || !_tabGeometry.IsController(shell.ConnectionId, tabId))
            {
                return PaneResizeResult(
                    id,
                    pane.Cols,
                    pane.Rows,
                    _tabGeometry.Controller(tabId) ?? "");
            }
        }
        else
        {
            AuthorizeResize(id, leaseId, connection?.ConnectionId);
        }

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation("pane.resize");
            var runtime = GetRuntime(id);
            await runtime.ResizeAsync(cols, rows, ct).ConfigureAwait(false);
            _state.UpdatePane(new PaneId(id), pane => pane with { Cols = cols, Rows = rows });
            if (shellResize)
                RememberPublishedShellPaneSize(connection!.ConnectionId, id, cols, rows);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        var paneGate = GetPaneEmitGate(id);
        await paneGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ForgetPostedFull(id);
            _renderCoalescer.Cancel(id);
            if (!TryPostVtSnapshotCore(id, attachPath: false, force: true))
                FailClosedSnapshotPaintOrByteFallback(id, () => WriteAttachByteFallback(id));
        }
        finally
        {
            paneGate.Release();
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["pane_id"] = id,
            ["cols"] = cols,
            ["rows"] = rows,
        };
    }

    /// <summary>
    /// Require an active input lease. Explicit lease_id preferred; otherwise holder connection.
    /// Rejects ghost leases whose pane was closed.
    /// </summary>
    private void AuthorizeInput(string paneId, string? leaseId, string? connectionId)
    {
        RejectIfPaneAbsent(paneId);
        // Resize scope does not authorize input.
        var auth = _leases.TryAuthorize(paneId, LeaseScopes.Input, leaseId, connectionId);
        ThrowForAuthorize(auth);
    }

    /// <summary>
    /// Resize accepts a resize lease or falls back to an input lease for the same holder.
    /// </summary>
    private void AuthorizeResize(string paneId, string? leaseId, string? connectionId)
    {
        RejectIfPaneAbsent(paneId);
        var resize = _leases.TryAuthorize(paneId, LeaseScopes.Resize, leaseId, connectionId);
        if (resize.Status == LeaseAuthorizeStatus.Authorized)
            return;

        // Input lease also covers resize for controller convenience (design §4.5 scopes).
        var input = _leases.TryAuthorize(paneId, LeaseScopes.Input, leaseId, connectionId);
        if (input.Status == LeaseAuthorizeStatus.Authorized)
            return;

        // Prefer the more specific failure (expired over missing).
        if (resize.Status == LeaseAuthorizeStatus.Expired || input.Status == LeaseAuthorizeStatus.Expired)
            throw new ControlPlaneException(ProtocolErrorCodes.LeaseExpired, "Lease expired");
        throw new ControlPlaneException(ProtocolErrorCodes.LeaseRequired, "Controller lease required");
    }

    /// <summary>
    /// Drop residual leases and fail when the pane is gone (claim/authorize/renew consistency).
    /// </summary>
    private void RejectIfPaneAbsent(string paneId)
    {
        if (_state.GetPane(new PaneId(paneId)) is not null)
            return;

        _ = _leases.DropPane(paneId);
        throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
    }

    private static void ThrowForAuthorize(LeaseAuthorizeResult auth)
    {
        switch (auth.Status)
        {
            case LeaseAuthorizeStatus.Authorized:
                return;
            case LeaseAuthorizeStatus.Expired:
                throw new ControlPlaneException(ProtocolErrorCodes.LeaseExpired, "Lease expired");
            case LeaseAuthorizeStatus.WrongHolder:
                throw new ControlPlaneException(ProtocolErrorCodes.LeaseRequired, "Controller lease required");
            default:
                throw new ControlPlaneException(ProtocolErrorCodes.LeaseRequired, "Controller lease required");
        }
    }

    private JsonObject PaneRead(PaneReadParams p)
    {
        var id = RequireField(p.PaneId, "pane_id");
        var source = ProtocolPaneReadSources.Normalize(EmptyToNull(p.Source) ?? ProtocolPaneReadSources.Visible);
        var lines = p.Lines ?? 200;
        var runtime = GetRuntime(id);
        var pane = _state.GetPane(new PaneId(id));

        string raw = ReadPaneSource(runtime, source, lines);

        var compressed = _intelligence.CompressForAgent(new PaneId(id), raw, pane?.Command);
        return NdjsonRpcBudget.FitPaneReadObject(
            new JsonObject
            {
                ["pane_id"] = id,
                ["source"] = source,
                ["text"] = raw,
                ["compressed"] = compressed,
                ["agent_status"] = (pane?.AgentStatus ?? AgentStatus.Unknown).ToString().ToLowerInvariant(),
            },
            UnixSocketServerOptions.DefaultMaxLineBytes);
    }

    private async Task<JsonObject> PaneCloseAsync(
        PaneCloseParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("pane.close");
        var id = RequireField(p.PaneId, "pane_id");
        var side = GetPaneSideEffectLock(id);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drain queued Output before dropping occupant identity so the
            // emit loop still journals chunks that the PTY already produced.
            await WaitOutputEmitsIdleAsync(id).ConfigureAwait(false);

            // Lock order with prepare: side-effect → bindingMutationGate.
            // Prepare freezes under bindingMutationGate; durable graph RemovePane must
            // not race past that freeze.
            // Dispose of IPaneRuntime stays outside the gate for latency.
            List<IPaneRuntime> taken = [];
            int gen = 0;
            CustomCommandOverlayState? overlay = null;
            var alreadyGone = false;
            string? closedTabId = null;
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, "pane.close");
                EnsureNotFrozenForMutation("pane.close");
                RememberWorkGeneration(p.WorkId, p.Generation);

                lock (_gate)
                    taken = TakePaneRuntimesUnlocked(id);

                var existing = _state.GetPane(new PaneId(id));
                if (existing is null && taken.Count == 0)
                {
                    alreadyGone = true;
                }
                else
                {
                    _intelligence.RemovePane(new PaneId(id));
                    gen = existing?.OccupantGeneration ?? 0;
                    closedTabId = existing?.TabId.Value;
                    _metadata.Drop("pane", id);
                    _state.RemovePane(new PaneId(id));
                    overlay = TakeCommandOverlay(id);
                    if (overlay is not null)
                        ApplyCommandOverlayRestoreUnlocked(overlay);
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (alreadyGone)
                return new JsonObject { ["ok"] = true, ["pane_id"] = id };

            try
            {
                foreach (var runtime in taken)
                {
                    runtime.OutputReceived -= OnRuntimeOutput;
                    runtime.BellReceived -= OnRuntimeBell;
                    runtime.Exited -= OnRuntimeExited;
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }

                await FlushTerminalOutputAsync(id).ConfigureAwait(false);

                // Free leases, attachment quota, and observe filters for the closed pane.
                await CleanupPaneSideEffectsAsync(id, ct).ConfigureAwait(false);

                await EmitLifecycleAsync(
                    ProtocolEventTypes.PaneLifecycle,
                    id,
                    PaneLifecycle.Closed,
                    gen,
                    CancellationToken.None).ConfigureAwait(false);

                // Explicit user action: sync durable graph and delete rows absent from state.
                await PersistGraphAsync(requireDurable: true, removeMissingPanes: true).ConfigureAwait(false);
                ReconcileAttachClientViews();

                if (overlay is not null)
                {
                    await EmitLayoutUpdatedAsync(overlay.TabId, overlay.RestorePaneId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(closedTabId))
                {
                    await EmitLayoutUpdatedAsync(closedTabId, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return new JsonObject { ["ok"] = true, ["pane_id"] = id };
            }
            catch
            {
                if (overlay is not null)
                    PutCommandOverlayBack(overlay);
                throw;
            }
        }
        finally
        {
            side.Release();
            // Drop the side-effect lock entry after close so the map stays bounded.
            _ = _paneSideEffectLocks.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Tear down a pane runtime after the graph row is already gone
    /// (tab.close / workspace.close / apply replace).
    /// Does not RemovePane or persist — caller owns the graph write.
    /// Holds the pane side-effect lock, matching <see cref="PaneCloseAsync"/>.
    /// Callers must not already hold that lock (it is not recursive).
    /// </summary>
    /// <param name="occupantGeneration">
    /// Generation snapshotted under the binding gate before the graph row is deleted.
    /// Wire value is never 0.
    /// </param>
    private async Task TeardownRemovedPaneRuntimeAsync(string id, int occupantGeneration, CancellationToken ct)
    {
        var side = GetPaneSideEffectLock(id);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitOutputEmitsIdleAsync(id).ConfigureAwait(false);
            List<IPaneRuntime> taken;
            lock (_gate)
                taken = TakePaneRuntimesUnlocked(id);
            _intelligence.RemovePane(new PaneId(id));
            foreach (var runtime in taken)
            {
                runtime.OutputReceived -= OnRuntimeOutput;
                runtime.BellReceived -= OnRuntimeBell;
                runtime.Exited -= OnRuntimeExited;
                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dispose after tab/layout remove for {PaneId}", id);
                }
            }

            await FlushTerminalOutputAsync(id).ConfigureAwait(false);
            await CleanupPaneSideEffectsAsync(id, ct).ConfigureAwait(false);
            await EmitLifecycleAsync(
                ProtocolEventTypes.PaneLifecycle,
                id,
                PaneLifecycle.Closed,
                NormalizeOccupantGeneration(occupantGeneration),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            side.Release();
            _ = _paneSideEffectLocks.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Occupant generation for a pane that is about to leave the graph.
    /// Generation 0 is a pre-admit sentinel and must not appear on the wire.
    /// </summary>
    private int SnapshotOccupantGeneration(PaneId paneId)
    {
        var pane = _state.GetPane(paneId);
        return NormalizeOccupantGeneration(pane?.OccupantGeneration ?? 0);
    }

    private SemaphoreSlim GetPaneSideEffectLock(string paneId) =>
        _paneSideEffectLocks.GetOrAdd(paneId, static _ => new SemaphoreSlim(1, 1));

    private SemaphoreSlim GetPaneEmitGate(string paneId) =>
        _paneEmitGates.GetOrAdd(paneId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Next live render seq for <paramref name="paneId"/>. Caller holds the pane emit gate.
    /// </summary>
    private long PeekNextRenderSeq(string paneId) =>
        _paneRenderSeq.TryGetValue(paneId, out var current) ? current + 1 : 1L;

    /// <summary>
    /// Release leases (with lease.changed audit), drop attachment quota slots,
    /// clear subscription observe filters, and free per-pane admit locks for a
    /// pane that no longer exists.
    /// </summary>
    private async Task CleanupPaneSideEffectsAsync(string paneId, CancellationToken ct)
    {
        var released = _leases.DropPane(paneId);
        foreach (var lease in released)
            await EmitLeaseChangedAsync(lease, ct).ConfigureAwait(false);

        _ = _attachments.DropByPane(paneId);
        _subscriptions?.DetachPane(paneId);
        _placement.RevokePane(new PaneId(paneId));
        // the next modal can open. Natural exit already calls
        // ReleaseOverlayPane. Close, tab close, and workspace close
        // must release the same pane reservation here.
        ReleaseOverlayPane(paneId);
        ReleasePaneAdmitLock(paneId);
        _renderCoalescer.Forget(paneId);
        _detectionScanner.Forget(paneId);
        _ = _paneDetectionLocks.TryRemove(paneId, out _);
        _ = _agentPresence.TryRemove(paneId, out _);
        _ = _paneEmitGates.TryRemove(paneId, out _);
        _ = _paneRenderSeq.TryRemove(paneId, out _);
        _ = _paneOutputSeq.TryRemove(paneId, out _);
        _ = _paneSnapshotEpoch.TryRemove(paneId, out _);
        _ = _terminalOutputRawCapByPaneId.TryRemove(paneId, out _);
        await StopPaneOutputLoopAsync(paneId).ConfigureAwait(false);
        ForgetPostedFull(paneId);
    }

    /// <summary>
    /// Remove the per-pane prompt/bump admit lock so long-lived mux hosts do not
    /// retain unbounded dictionary entries. Never Dispose the <see cref="SemaphoreSlim"/>:
    /// a concurrent <c>agent.prompt</c>/<c>BumpOccupantGenerationAsync</c> may still hold
    /// the instance after <c>TryRemove</c>; disposing would surface
    /// <see cref="ObjectDisposedException"/> as wire -32000.
    /// Entries are bounded by max_panes and cleaned on close; GC reclaims the slim.
    /// </summary>
    private void ReleasePaneAdmitLock(string paneId) =>
        _ = _paneAdmitLocks.TryRemove(paneId, out _);

    /// <summary>
    /// Acquire the per-pane admit lock only for a live pane. Validates liveness
    /// before <c>GetOrAdd</c> so client-controlled missing pane ids cannot grow the map.
    /// Re-checks after wait; maps dispose races to NotFound.
    /// </summary>
    private async Task<SemaphoreSlim> AcquirePaneAdmitAsync(string paneId, CancellationToken ct)
    {
        RejectIfPaneAbsent(paneId);

        var admit = _paneAdmitLocks.GetOrAdd(paneId, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await admit.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Defense: historical dispose path or future refcount dispose.
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        }

        // Close may have raced while we waited; drop any admit entry for a ghost pane.
        if (_state.GetPane(new PaneId(paneId)) is null)
        {
            SafeReleaseAdmit(admit);
            ReleasePaneAdmitLock(paneId);
            _ = _leases.DropPane(paneId);
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        }

        return admit;
    }

    private static void SafeReleaseAdmit(SemaphoreSlim admit)
    {
        try
        {
            admit.Release();
        }
        catch (ObjectDisposedException)
        {
            // Close raced; ignore.
        }
        catch (SemaphoreFullException)
        {
            // Double-release defense; ignore.
        }
    }

    private JsonArray AgentList()
    {
        var arr = new JsonArray();
        foreach (var pane in _state.ListPanes())
        {
            var aligned = AlignHeldAuthorityStatus(pane);
            var item = new JsonObject
            {
                ["pane_id"] = aligned.Id.Value,
                ["label"] = aligned.Label,
                ["agent"] = aligned.AgentKind,
                ["state"] = aligned.AgentStatus.ToString().ToLowerInvariant(),
                ["message"] = aligned.AgentMessage,
                ["alive"] = aligned.IsAlive,
                ["tokens"] = TokensToJson(aligned.Id.Value),
            };
            if (!string.IsNullOrEmpty(aligned.AgentName))
                item["name"] = aligned.AgentName;
            var session = AgentSessionToJson(aligned.AgentSession);
            if (session is not null)
                item["agent_session"] = session;
            arr.Add((JsonNode)item);
        }
        return arr;
    }

    private JsonObject AgentGet(PaneGetParams p)
    {
        var pane = RefreshAgentPane(ResolveAgentTarget(p.PaneId, p.AgentId).Id.Value);
        return BuildAgentStatusResult(pane, wait: null, timedOut: null);
    }

    private JsonObject BuildAgentStatusResult(PaneState pane, AgentWaitOutcomeFields? wait, bool? timedOut)
    {
        // Wire never exposes generation 0.
        var gen = NormalizeOccupantGeneration(pane.OccupantGeneration);
        // Effective bind: pane-local first, then session (matches agent.read / binding.get).
        var binding = pane.Binding ?? _state.Snapshot().Binding;
        var result = new JsonObject
        {
            ["pane_id"] = pane.Id.Value,
            ["agent"] = pane.AgentKind,
            ["state"] = pane.AgentStatus.ToString().ToLowerInvariant(),
            ["message"] = pane.AgentMessage,
            ["alive"] = pane.IsAlive,
            ["binding"] = BindingToJson(binding),
            ["occupant_generation"] = gen,
            ["occupant_id"] = $"{pane.Id.Value}:{gen}",
            ["runtime_session_id"] = _state.SessionId.Value,
            ["tokens"] = TokensToJson(pane.Id.Value),
        };
        if (!string.IsNullOrEmpty(pane.AgentName))
            result["name"] = pane.AgentName;

        if (wait is not null)
        {
            result["wait"] = new JsonObject
            {
                ["until"] = wait.Until,
                ["timed_out"] = wait.TimedOut,
            };
        }

        // Additive legacy top-level flag (older clients); fixture prefers nested wait.
        if (timedOut is true)
            result["timed_out"] = true;

        var session = AgentSessionToJson(pane.AgentSession);
        if (session is not null)
            result["agent_session"] = session;

        return result;
    }

    private sealed record AgentWaitOutcomeFields(string Until, bool TimedOut);

    private async Task<JsonObject> AgentWaitAsync(AgentWaitParams p, CancellationToken ct)
    {
        var id = RequireField(p.PaneId, "pane_id");
        var until = (EmptyToNull(p.Until) ?? "blocked").ToLowerInvariant();
        var timeoutMs = p.TimeoutMs ?? 60_000;

        var paneAtStart = _state.GetPane(new PaneId(id))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
        // Pin never uses 0: restored/pre-admit panes normalize to first occupant gen 1.
        var pinnedGen = NormalizeOccupantGeneration(paneAtStart.OccupantGeneration);

        return await AgentWaitPinnedAsync(id, until, timeoutMs, pinnedGen, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> AgentWaitPinnedAsync(
        string paneId,
        string until,
        int timeoutMs,
        int pinnedGen,
        CancellationToken ct)
    {
        // Normalize pin: generation 0 is not a real occupant pin target.
        pinnedGen = NormalizeOccupantGeneration(pinnedGen);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        while (!cts.IsCancellationRequested)
        {
            var pane = RefreshAgentPane(paneId);
            if (IsOccupantReplaced(pinnedGen, pane.OccupantGeneration))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.OccupantReplaced,
                    $"Occupant replaced: pinned generation {pinnedGen}, current {pane.OccupantGeneration}");
            }

            // Incoming occupant is not committed yet. Do not settle on the outgoing shell.
            if (!HasUncommittedReplacement(paneId)
                && MatchesUntil(until, pane.AgentStatus.ToString().ToLowerInvariant()))
            {
                await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
                return BuildAgentStatusResult(
                    pane,
                    new AgentWaitOutcomeFields(until, TimedOut: false),
                    timedOut: false);
            }

            try
            {
                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        var final = RefreshAgentPane(paneId);
        if (IsOccupantReplaced(pinnedGen, final.OccupantGeneration))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.OccupantReplaced,
                $"Occupant replaced: pinned generation {pinnedGen}, current {final.OccupantGeneration}");
        }

        if (!HasUncommittedReplacement(paneId)
            && MatchesUntil(until, final.AgentStatus.ToString().ToLowerInvariant()))
        {
            await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
            return BuildAgentStatusResult(
                final,
                new AgentWaitOutcomeFields(until, TimedOut: false),
                timedOut: false);
        }

        await FlushExpiredMetadataForReadAsync(ct).ConfigureAwait(false);
        return BuildAgentStatusResult(
            final,
            new AgentWaitOutcomeFields(until, TimedOut: true),
            timedOut: true);
    }

    /// <summary>
    /// True when the live generation is a real replacement of the pin.
    /// The 0→1 bootstrap transition is never treated as occupant_replaced.
    /// </summary>
    private static bool IsOccupantReplaced(int pinnedGen, int currentGen)
    {
        var pin = NormalizeOccupantGeneration(pinnedGen);
        var cur = NormalizeOccupantGeneration(currentGen);
        return pin != cur;
    }

    /// <summary>
    /// Occupant generation 0 is a pre-admit / unset sentinel — never a pin or wire value.
    /// </summary>
    private static int NormalizeOccupantGeneration(int generation) =>
        generation <= 0 ? 1 : generation;

    private static bool MatchesUntil(string until, string state) =>
        until == "any" ||
        state == until ||
        (until == "done" && state is "done" or "idle") ||
        (until == "blocked" && state == "blocked");

    private PaneState RefreshAgentPane(string id)
    {
        var pane = _state.GetPane(new PaneId(id))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
        pane = AlignHeldAuthorityStatus(pane);
        var generation = pane.OccupantGeneration;

        IPaneRuntime? runtime;
        lock (_gate)
        {
            // In-flight first, then identity, under one gate.
            if (HasUncommittedReplacementUnlocked(id))
                return pane;
            _runtimes.TryGetValue(id, out runtime);
        }

        if (runtime is null)
        {
            // restore never reattaches. Apply the restorer death rule so
            // release/clear of held working/blocked does not leave that wait.
            if (pane.LifecycleState == PaneLifecycle.Starting)
                return pane;
            pane = UpdatePaneEmittingStatus(pane.Id, x =>
            {
                if (x.OccupantGeneration != generation)
                    return x;
                var nextStatus = StatusAfterProcessDeath(x);
                return x with
                {
                    IsAlive = false,
                    AgentStatus = nextStatus,
                    AgentKind = x.AgentAuthority?.Agent ?? x.AgentKind,
                    AgentMessage = x.AgentAuthority?.Message ?? x.AgentMessage,
                    LifecycleState = x.LifecycleState is PaneLifecycle.Running or PaneLifecycle.Starting
                        ? PaneLifecycle.Exited
                        : x.LifecycleState,
                };
            }) ?? pane;
            _detectionScanner.CancelRecheck(id);
            return pane;
        }

        var alive = runtime.IsAlive;
        var exit = runtime.ExitCode ?? pane.ExitCode;
        if (!alive || exit is not null)
        {
            // StartAsync has not finished: IsAlive is false while LifecycleState is
            // Starting and ExitCode is null. Do not demote to Done/Exited — dual-client
            // agent.wait until=done during pane.create would otherwise succeed early
            // . Wait for StartAsync reconcile or a real exit.
            if (pane.LifecycleState == PaneLifecycle.Starting && exit is null)
                return pane;

            // Dead process: do not let detection text clobber terminal Done/Idle.
            // Held semantic authority also owns status until release/clear.
            pane = UpdatePaneEmittingStatus(pane.Id, x =>
            {
                if (x.OccupantGeneration != generation)
                    return x;
                if (!ShouldApplyOccupantStatus(runtime))
                    return x;
                var nextStatus = StatusAfterProcessDeath(x);
                return x with
                {
                    IsAlive = false,
                    ExitCode = exit,
                    Pid = null,
                    AgentStatus = nextStatus,
                    AgentKind = x.AgentAuthority?.Agent ?? x.AgentKind,
                    AgentMessage = x.AgentAuthority?.Message ?? x.AgentMessage,
                    LifecycleState = x.LifecycleState is PaneLifecycle.Running or PaneLifecycle.Starting
                        ? PaneLifecycle.Exited
                        : x.LifecycleState,
                };
            }) ?? pane;
            _detectionScanner.CancelRecheck(id);
        }
        else
        {
            DetectionResult? det = null;
            var refreshCleared = false;
            if (!HoldsSemanticAuthority(pane))
                det = DetectOccupant(runtime, pane, out refreshCleared);
            pane = UpdatePaneEmittingStatus(pane.Id, x =>
            {
                if (x.OccupantGeneration != generation)
                    return x;
                if (!ShouldApplyOccupantStatus(runtime))
                    return x;
                if (HoldsSemanticAuthority(x))
                    return x with { IsAlive = true, ExitCode = null };
                if (det is null)
                    return x with { IsAlive = true, ExitCode = null };
                if (det.SkipStateUpdate)
                    return x with { IsAlive = true, ExitCode = null };
                var nextKind = refreshCleared ? null : det.AgentKind;
                return x with
                {
                    AgentStatus = refreshCleared ? AgentStatus.Unknown : det.Status,
                    AgentKind = nextKind,
                    AgentMessage = det.Message,
                    IsAlive = true,
                    ExitCode = null,
                };
            }) ?? pane;
            LatchForegroundShellExitIfPublished(id, det, pane);
            ArmOccupancyRecheck(id);
        }

        return pane;
    }

    /// <summary>
    /// When authority is held, the durable/live row must match <see cref="PaneAgentAuthority.State"/>.
    /// Repairs a shutdown-demoted disk status after reload without a live runtime.
    /// </summary>
    private PaneState AlignHeldAuthorityStatus(PaneState pane)
    {
        if (pane.AgentAuthority is not { } held)
            return pane;
        if (pane.AgentStatus == held.State
            && string.Equals(pane.AgentKind, held.Agent, StringComparison.Ordinal)
            && string.Equals(pane.AgentMessage, held.Message, StringComparison.Ordinal))
            return pane;

        return UpdatePaneEmittingStatus(pane.Id, p =>
        {
            if (p.AgentAuthority is not { } current)
                return p;
            return p with
            {
                AgentStatus = current.State,
                AgentKind = current.Agent,
                AgentMessage = current.Message,
            };
        }) ?? pane;
    }

    private async Task<JsonObject> AgentPromptAsync(
        AgentPromptParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation("agent.prompt");
        var id = RequireField(p.PaneId, "pane_id");
        var message = RequireField(p.Message, "message");
        var leaseId = EmptyToNull(p.LeaseId);

        // Nested wait object or legacy wait:true + until/timeout_ms (until protocol major 2).
        var (doWait, until, timeoutMs) = ParsePromptWait(p);

        var pinnedGen = 0;
        var admit = await AcquirePaneAdmitAsync(id, ct).ConfigureAwait(false);
        try
        {
            // Authorize under admit lock; freeze re-check + write under bindingMutationGate
            // (same gate prepare uses for freeze) so post-barrier writes fail closed.
            // Lock order: pane admit → bindingMutationGate (prepare never takes admit).
            try
            {
                AuthorizeInput(id, leaseId, connection?.ConnectionId);
                EnsureWorkGenerationAllowsInput(id);
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
            {
                // Prompt-after-close / race: do not retain an admit entry for a missing pane.
                ReleasePaneAdmitLock(id);
                throw;
            }

            var text = message;
            if (!text.EndsWith('\n') && !text.EndsWith('\r'))
                text += "\n";

            if (InputBeforeBindingGateForTests is { } beforeGate)
                await beforeGate().ConfigureAwait(false);

            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, "agent.prompt");
                EnsureNotFrozenForMutation("agent.prompt");
                EnsureWorkGenerationAllowsInput(id);

                var paneBefore = _state.GetPane(new PaneId(id));
                if (paneBefore is null)
                {
                    ReleasePaneAdmitLock(id);
                    _ = _leases.DropPane(id);
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
                }

                // Same pin normalization as agent.wait (never pin generation 0).
                pinnedGen = NormalizeOccupantGeneration(paneBefore.OccupantGeneration);

                var runtime = GetRuntime(id);
                await runtime.WriteTextAsync(text, ct).ConfigureAwait(false);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (!doWait)
                return new JsonObject { ["ok"] = true, ["pane_id"] = id };
        }
        finally
        {
            SafeReleaseAdmit(admit);
        }

        // Wait outside admit lock (long poll); generation pinned at write time.
        return await AgentWaitPinnedAsync(id, until, timeoutMs, pinnedGen, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse nested <c>wait: { until, timeout_ms }</c> or legacy <c>wait: true</c> + top-level fields.
    /// </summary>
    private static (bool DoWait, string Until, int TimeoutMs) ParsePromptWait(AgentPromptParams p)
    {
        var until = "blocked";
        var timeoutMs = 60_000;
        var doWait = p.Wait is not null;

        if (p.Wait is not null)
        {
            if (!string.IsNullOrWhiteSpace(p.Wait.Until))
                until = p.Wait.Until.ToLowerInvariant();
            if (p.Wait.TimeoutMs is int tm)
                timeoutMs = tm;
        }

        // Legacy top-level until/timeout when the nested object omitted those fields.
        if (doWait)
        {
            if (string.IsNullOrWhiteSpace(p.Wait?.Until) && !string.IsNullOrWhiteSpace(p.Until))
                until = p.Until.ToLowerInvariant();
            if (p.Wait?.TimeoutMs is null && p.TimeoutMs is int topTm)
                timeoutMs = topTm;
        }

        return (doWait, until, timeoutMs);
    }

    /// <summary>
    /// Production primitive for occupant replacement: increments <c>occupant_generation</c>
    /// and emits lifecycle events. F1 has no public pane.restart yet; process exit alone
    /// does <em>not</em> bump generation (exit ends the occupant, it does not replace it).
    /// Future restart/respawn paths must call this when admitting a new occupant into
    /// an existing pane so pinned <c>agent.wait</c> returns <c>occupant_replaced</c> (-32008).
    /// Serialized on the same per-pane admit lock as <c>agent.prompt</c> authorize+write+pin
    /// so a bump cannot interleave mid-prompt critical section.
    /// </summary>
    internal async Task<int> BumpOccupantGenerationAsync(string paneId, CancellationToken ct = default)
    {
        var admit = await AcquirePaneAdmitAsync(paneId, ct).ConfigureAwait(false);
        try
        {
            var generation = await BumpOccupantGenerationUnderAdmitAsync(paneId, ct)
                .ConfigureAwait(false);
            _metadata.Drop("pane", paneId);
            return generation;
        }
        finally
        {
            SafeReleaseAdmit(admit);
        }
    }

    /// <summary>
    /// Occupant bump while the caller already holds the per-pane admit lock.
    /// <c>agent.start</c> holds admit for the whole replace so it must not
    /// re-enter <see cref="BumpOccupantGenerationAsync"/>. Clears authority
    /// and identity; pane tokens stay until a successful occupant commit.
    /// </summary>
    private async Task<int> BumpOccupantGenerationUnderAdmitAsync(string paneId, CancellationToken ct)
    {
        // base = max(current, 1) then +1 so a historical gen-0 pane always changes pin
        // identity on first real replacement (0→2, not 0→1 which IsOccupantReplaced
        // would treat as bootstrap / same pin).
        var pane = UpdatePaneEmittingStatus(new PaneId(paneId), p =>
        {
            var bas = NormalizeOccupantGeneration(p.OccupantGeneration);
            return p with
            {
                OccupantGeneration = bas + 1,
                AgentStatus = AgentStatus.Working,
                AgentKind = null,
                AgentMessage = null,
                AgentAuthority = null,
                AgentSession = null,
                AgentAuthoritySequences = EmptyAuthoritySequences(),
            };
        });
        if (pane is null)
        {
            ReleasePaneAdmitLock(paneId);
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        }

        _detectionScanner.Forget(paneId);
        lock (GetPaneDetectionLock(paneId))
        {
            if (_agentPresence.TryGetValue(paneId, out var presence))
                presence.Reset(pane.OccupantGeneration);
        }
        _placement.RevokeOccupant(new PaneId(paneId));

        await EmitLifecycleAsync(
            ProtocolEventTypes.OccupantLifecycle,
            pane.Id.Value,
            pane.LifecycleState,
            pane.OccupantGeneration,
            ct).ConfigureAwait(false);
        await EmitLifecycleAsync(
            ProtocolEventTypes.PaneLifecycle,
            pane.Id.Value,
            pane.LifecycleState,
            pane.OccupantGeneration,
            ct).ConfigureAwait(false);
        return pane.OccupantGeneration;
    }

    /// <summary>
    /// Test hook: after pane admit, before <c>_bindingMutationGate</c>. Lets tests
    /// report authority in the pre-bump window of a failing <c>agent.start</c>.
    /// </summary>
    internal Func<Task>? AgentStartBeforeBindingGateForTests { get; set; }

    /// <summary>
    /// Test hook: after the pre-gate generation check, before <c>_bindingMutationGate</c>.
    /// Lets tests record a newer generation while a stale write waits.
    /// </summary>
    internal Func<Task>? InputBeforeBindingGateForTests { get; set; }

    /// <summary>
    /// After graph mutation and render-identity reset, before persist.
    /// Holds the pane side-effect lock. Tests delay publication to assert
    /// show/hide/close order.
    /// </summary>
    internal Func<Task>? VisibilityAfterMutationBeforePublishForTests { get; set; }

    /// <summary>
    /// Optional test observer. Invoked when a visibility mutation
    /// resets Full identity. Tests own the collection. Production
    /// keeps no history.
    /// </summary>
    internal Action<string>? VisibilityResetObservedForTests { get; set; }

    /// <summary>
    /// Between the two re-anchor samples of one live cells encode.
    /// A test marks the observer pending here. The second sample then
    /// forces a full encode with a null baseline.
    /// </summary>
    internal Action<string>? BeforeCleanReanchorDecisionForTests { get; set; }

    /// <summary>
    /// After the re-anchor generation is read and before writer admission.
    /// A test marks the observer pending here. The commit keeps that newer mark.
    /// </summary>
    internal Action<string>? BeforeLiveCellsAdmitForTests { get; set; }

    internal void MarkLiveObserversReanchorForTests(string paneId)
    {
        if (_subscriptions is null || string.IsNullOrWhiteSpace(paneId))
            return;
        foreach (var sub in _subscriptions.ListLiveObservers(paneId))
            sub.MarkReanchorPending(paneId);
    }

    internal void MarkObserverReanchorForTests(string subscriptionId, string paneId)
    {
        if (_subscriptions is null || string.IsNullOrWhiteSpace(subscriptionId))
            return;
        _subscriptions.Get(subscriptionId)?.MarkReanchorPending(paneId);
    }

    internal bool ObserverReanchorPendingForTests(string subscriptionId, string paneId) =>
        _subscriptions?.Get(subscriptionId)?.ReanchorPending(paneId) ?? false;

    /// <summary>Test helper: in-memory placement secrets.</summary>
    internal IPanePlacementAuthorityService Placement => _placement;

    /// <summary>
    /// Test helper: set Continuity Work identity without an occupant start.
    /// </summary>
    internal void ForceWorkIdentityForTests(string paneId, string? workId, long workGeneration)
    {
        _ = _state.UpdatePane(new PaneId(paneId), p => p with
        {
            WorkId = workId,
            WorkGeneration = workGeneration,
        })
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
    }

    /// <summary>
    /// Test helper: force agent status without process I/O (honest pin tests after generation bump).
    /// </summary>
    internal void ForceAgentStatusForTests(string paneId, AgentStatus status)
    {
        var pane = _state.GetPane(new PaneId(paneId))
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        _ = TryApplyAgentStatus(paneId, pane.OccupantGeneration, status, pane.AgentKind, pane.AgentMessage);
    }

    /// <summary>Test helper: run one detection scan now. Does not restart the pane.</summary>
    internal void ScanDetectionNowForTests(string paneId)
    {
        var pane = _state.GetPane(new PaneId(paneId));
        var generation = pane?.OccupantGeneration ?? 0;
        _detectionScanner.Mark(paneId, generation);
        _detectionScanner.ScanNow(paneId);
    }

    /// <summary>
    /// Test helper: probe occupancy without a content <c>Mark</c>.
    /// </summary>
    internal void ScanOccupancyRecheckNowForTests(string paneId)
    {
        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null)
            return;
        RunDetectionScan(
            paneId,
            invokeApplyHook: true,
            markedGeneration: pane.OccupantGeneration);
    }

    internal bool HasOccupancyRecheckForTests(string paneId) =>
        _detectionScanner.HasRecheck(paneId);

    /// <summary>Test helper: set seen without a status transition.</summary>
    internal void ForceSeenForTests(string paneId, bool seen)
    {
        _ = _state.UpdatePane(new PaneId(paneId), p => p with { Seen = seen })
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
    }

    /// <summary>
    /// Test helper: force occupant generation (simulates historical gen-0 / restored state).
    /// </summary>
    internal void ForceOccupantGenerationForTests(string paneId, int generation)
    {
        _ = _state.UpdatePane(new PaneId(paneId), p => p with { OccupantGeneration = generation })
            ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
    }

    /// <summary>Test helper: count of retained per-pane admit locks.</summary>
    internal int PaneAdmitLockCount => _paneAdmitLocks.Count;

    /// <summary>Test helper: lease registry.</summary>
    internal ILeaseRegistry Leases => _leases;

    /// <summary>Test helper: attachment registry.</summary>
    internal IAttachmentRegistry Attachments => _attachments;

    /// <summary>Test helper: visible-set publication.</summary>
    internal IVisibleSetPublication VisibleSets => _visibleSets;

    private JsonObject AgentRead(PaneReadParams p)
    {
        var id = RequireField(p.PaneId, "pane_id");
        var source = ProtocolPaneReadSources.Normalize(EmptyToNull(p.Source) ?? ProtocolPaneReadSources.Visible);
        var lines = p.Lines ?? 200;
        var runtime = GetRuntime(id);
        var pane = _state.GetPane(new PaneId(id));
        var snap = _state.Snapshot();

        string raw = ReadPaneSource(runtime, source, lines);

        // Redact secrets before presentation compress and agent-facing raw.
        // Stateless: VT buffer already joins chunks. Do not share carry with journal emit.
        var redactedBytes = _redactor.RedactTerminalBytes(
            Encoding.UTF8.GetBytes(raw ?? string.Empty));
        raw = Encoding.UTF8.GetString(redactedBytes.Span);

        // agent.read caps raw at 64 KiB. Detection path for pane.read stays uncapped.
        const int rawCapBytes = 64 * 1024;
        raw = CapUtf8(raw, rawCapBytes);

        const int maxLines = 200;
        const int maxBytes = 32 * 1024;
        var pres = _presentation.Compress(new PresentationRequest
        {
            PaneId = id,
            RawText = raw,
            CommandHint = pane?.Command,
            MaxLines = maxLines,
            MaxCompressedBytes = maxBytes,
            RawCapBytes = rawCapBytes,
            TimeoutMs = 250,
        });

        // Presentation-only Atomic headers (do not mutate VT). Prefer pane binding, else session.
        var binding = pane?.Binding ?? snap.Binding;
        var text = ApplyAtomicHeaders(id, pres.Text, binding, pane?.Command);

        var journalNext = _journal?.NextSeq ?? snap.NextEventSeq;
        var gen = pane?.OccupantGeneration ?? 0;
        var status = (pane?.AgentStatus ?? AgentStatus.Unknown).ToString().ToLowerInvariant();

        return new JsonObject
        {
            ["pane_id"] = id,
            ["text"] = text,
            // raw is post-redaction + capped (not VT firehose).
            ["raw"] = raw,
            ["source"] = source,
            ["agent_status"] = status,
            ["occupant_generation"] = gen,
            ["runtime_session_id"] = snap.Id.Value,
            ["source_checkpoint"] = new JsonObject
            {
                ["source"] = source,
                ["journal_next_seq"] = journalNext,
                ["occupant_generation"] = gen,
                ["runtime_session_id"] = snap.Id.Value,
                ["binding"] = BindingToJson(binding),
            },
            ["presentation"] = new JsonObject
            {
                ["compressor"] = pres.CompressorId,
                ["truncated"] = pres.Truncated,
                ["max_lines"] = maxLines,
                ["max_bytes"] = maxBytes,
            },
        };
    }

    private static string ReadPaneSource(IPaneRuntime runtime, string source, int lines) =>
        source switch
        {
            ProtocolPaneReadSources.Recent => runtime.ReadRecentText(lines),
            ProtocolPaneReadSources.RecentUnwrapped => runtime.ReadRecentUnwrappedText(lines),
            ProtocolPaneReadSources.Detection => runtime.ReadDetectionText(),
            _ => runtime.ReadVisibleText(),
        };

    private static string ApplyAtomicHeaders(
        string paneId, string body, AtomicBinding? binding, string? commandHint)
    {
        if (binding is null)
            return body;

        var header = new StringBuilder();
        header.AppendLine($"# hypa pane={paneId}");
        if (binding.RunId is not null)
            header.AppendLine($"# atomic.run={binding.RunId}");
        if (binding.StepId is not null)
            header.AppendLine($"# atomic.step={binding.StepId}");
        if (binding.AgentSessionId is not null)
            header.AppendLine($"# atomic.agent_session={binding.AgentSessionId}");
        if (!string.IsNullOrEmpty(commandHint))
            header.AppendLine($"# command={commandHint}");
        header.AppendLine();
        return header + body;
    }

    private static string CapUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    private IPaneRuntime GetRuntime(string paneId)
    {
        if (TryGetRuntime(paneId, out var runtime) && runtime is not null)
            return runtime;
        throw new ControlPlaneException(-32004, $"Pane runtime not found: {paneId}");
    }

    private bool TryGetRuntime(string paneId, out IPaneRuntime? runtime)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(paneId, out runtime);
        }
    }

    /// <summary>Test helper: number of live pane runtimes held by the control plane.</summary>
    internal int RuntimeCount
    {
        get { lock (_gate) return _runtimes.Count; }
    }

    /// <summary>Test helper: uncommitted replacements tracked for shutdown.</summary>
    internal int PendingRuntimeCount
    {
        get { lock (_gate) return _pendingRuntimes.Count; }
    }

    /// <summary>
    /// Test hook: runs after output/exit is allowed to apply and before the pane write.
    /// Output apply runs on the detection scan. Exit apply stays on OnRuntimeExited.
    /// Used to overlap bump/swap with a late outgoing event.
    /// </summary>
    internal Action<IPaneRuntime>? AfterOccupantStatusApplyDecision { get; set; }

    /// <summary>
    /// Test hook: runs at the start of a scan, before the per-pane lock.
    /// Used to overlap a forced exit scan with an in-flight tick and a swap.
    /// </summary>
    internal Action<string, bool>? BeforeDetectionScan { get; set; }

    /// <summary>
    /// Test hook: runs after the detection tick copies the marked set and
    /// before each scan. Forget cannot retract an id already in that batch.
    /// </summary>
    internal Action? AfterDetectionBatchCopied
    {
        get => _detectionScanner.AfterBatchCopied;
        set => _detectionScanner.AfterBatchCopied = value;
    }

    private object GetPaneDetectionLock(string paneId) =>
        _paneDetectionLocks.GetOrAdd(paneId, static _ => new object());

    /// <summary>
    /// Test hook: runs after BeginOutputEmit and before the pane emit gate.
    /// Holds an in-flight emit across a committed occupant swap.
    /// </summary>
    internal Func<IPaneRuntime, Task>? DelayOutputEmitAsync { get; set; }

    private int _failNextSnapshotPack;
    private int _snapshotClientBatchBytes = AttachSnapshotPacker.MaxClientBatchBytes;

    /// <summary>
    /// Test hook: the next VT snapshot pack throws after a successful capture.
    /// Proves live byte fallback on a packer failure, not corrupt capture JSON.
    /// </summary>
    internal void FailNextSnapshotPackOnce() => Interlocked.Exchange(ref _failNextSnapshotPack, 1);

    /// <summary>
    /// Test hook: shrink the client assembler cap so an otherwise valid
    /// complete batch fails closed and keeps byte fallback.
    /// </summary>
    internal void SetSnapshotClientBatchBytesForTest(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        Volatile.Write(ref _snapshotClientBatchBytes, bytes);
    }

    /// <summary>
    /// Test hook: drop the last posted full-grid size so the next live paint
    /// cannot publish a patch the client would reject.
    /// </summary>
    internal void ClearPostedFullGridForTest(string paneId) =>
        ForgetPostedFull(paneId);

    /// <summary>
    /// Test hook: runs at the start of overlay close-on-exit, before pane.close.
    /// Used to freeze the session after exit is seen and before graph rewrite.
    /// </summary>
    internal Func<string, Task>? DelayCommandOverlayCloseAsync { get; set; }

    /// <summary>
    /// Test hook: runs before occupant-generation recheck in status emit.
    /// Holds a captured status event across a committed occupant replacement.
    /// </summary>
    internal Func<PaneState, Task>? DelayAgentStatusEmitAsync { get; set; }

    /// <summary>
    /// Test hook: runs after the last pre-emit occupant-generation check and
    /// before <c>EmitReliableAsync</c>. Holds across the journal-boundary wait.
    /// </summary>
    internal Func<PaneState, Task>? DelayAfterAgentStatusEmitCheckAsync { get; set; }

    /// <summary>
    /// Test hook: runs after the occupant-identity predicate under the emit
    /// gate and before journal append. A second predicate runs after this hook.
    /// </summary>
    internal Func<Task>? DelayAfterReliableEmitIdentityCheckAsync { get; set; }

    /// <summary>Test helper: registered occupant, or null when the pane has no runtime.</summary>
    internal IPaneRuntime? PeekRuntime(string paneId)
    {
        lock (_gate)
            return _runtimes.TryGetValue(paneId, out var runtime) ? runtime : null;
    }

    /// <summary>Test helper: undeliverable bytes from closed input actors for a pane.</summary>
    internal int PeekClosedInputUndeliverable(string paneId) =>
        _closedInputUndeliverable.TryGetValue(paneId, out var n) ? n : 0;

    /// <summary>Test helper: uncommitted replacement, or null when none is tracked.</summary>
    internal IPaneRuntime? PeekPendingRuntime(string paneId)
    {
        lock (_gate)
            return _pendingRuntimes.TryGetValue(paneId, out var runtime) ? runtime : null;
    }

    /// <summary>Test helper: whether shutdown has begun.</summary>
    internal bool ShuttingDown => IsShuttingDown;

    private JsonObject WorkspaceToJson(WorkspaceState ws)
    {
        var source = WorkspaceDirectoryIdentity.Source(_state.Snapshot(), ws);
        var cwd = source?.Cwd ?? ws.Cwd;
        var git = _workspaceDirectoryGit.Resolve(cwd);
        var label = ws.CustomLabel ? ws.Label
            : git.RepositoryName.Length > 0 ? git.RepositoryName
            : WorkspaceDirectoryIdentity.FallbackLabel(cwd, _operatorHome);
        var tokens = _metadata.Get("workspace", ws.Id.Value);
        var obj = new JsonObject
        {
            ["workspace_id"] = ws.Id.Value,
            ["label"] = label,
            ["custom_label"] = ws.CustomLabel,
            ["resolved_cwd"] = cwd,
            ["identity_pane_id"] = source?.Id.Value,
            ["branch"] = git.Branch,
            ["git_status"] = git.Status,
            ["repository_name"] = git.RepositoryName,
            ["cwd"] = ws.Cwd,
            ["ordinal"] = ws.Ordinal,
            ["focused_tab_id"] = ws.FocusedTabId?.Value,
            ["binding"] = BindingToJson(ws.Binding),
            ["tokens"] = new JsonObject(tokens.ToDictionary(p => p.Key, p => (JsonNode?)JsonValue.Create(p.Value), StringComparer.Ordinal)),
        };
        if (ws.Worktree is { } membership)
        {
            obj["worktree"] = new JsonObject
            {
                ["key"] = membership.Key,
                ["label"] = membership.Label,
                ["is_linked_worktree"] = membership.IsLinkedWorktree,
            };
        }

        return obj;
    }

    private static JsonObject TabToJson(TabState tab)
    {
        var layout = tab.LayoutRoot;
        return new JsonObject
        {
            ["tab_id"] = tab.Id.Value,
            ["workspace_id"] = tab.WorkspaceId.Value,
            ["label"] = tab.Label,
            ["ordinal"] = tab.Ordinal,
            ["focused_pane_id"] = tab.FocusedPaneId?.Value,
            ["zoomed"] = tab.Zoomed,
            ["zoomed_pane_id"] = tab.ZoomedPaneId?.Value,
            ["custom_label"] = tab.CustomLabel,
            ["identity_pane_id"] = tab.IdentityPaneId?.Value,
            ["layout"] = layout?.ToJsonObject(includePaneId: true),
        };
    }

    private JsonObject PaneToJson(PaneState pane, string? parentCapability = null)
    {
        // Effective bind for wire consistency with agent.get / agent.read / binding.get.
        var binding = pane.Binding ?? _state.Snapshot().Binding;
        var obj = new JsonObject
        {
            ["pane_id"] = pane.Id.Value,
            ["tab_id"] = pane.TabId.Value,
            ["workspace_id"] = pane.WorkspaceId.Value,
            ["label"] = pane.Label,
            ["cwd"] = pane.Cwd,
            ["command"] = pane.Command,
            ["cols"] = pane.Cols,
            ["rows"] = pane.Rows,
            ["alive"] = pane.IsAlive,
            ["exit_code"] = pane.ExitCode,
            ["agent"] = pane.AgentKind,
            ["state"] = pane.AgentStatus.ToString().ToLowerInvariant(),
            ["message"] = pane.AgentMessage,
            ["binding"] = BindingToJson(binding),
            ["seen"] = pane.Seen,
            ["right_click"] = string.IsNullOrWhiteSpace(pane.RightClick)
                ? PaneRightClick.Hypa
                : pane.RightClick,
            ["tokens"] = TokensToJson(pane.Id.Value),
            ["placement"] = PanePlacementWire.ToWire(pane.Placement),
            ["hidden"] = pane.Placement == PanePlacement.Hidden,
        };
        if (pane.ParentPaneId is { } parent)
            obj["parent_pane_id"] = parent.Value;
        if (!string.IsNullOrWhiteSpace(parentCapability))
            obj["parent_capability"] = parentCapability;
        if (!string.IsNullOrEmpty(pane.AgentName))
            obj["agent_name"] = pane.AgentName;
        if (!string.IsNullOrWhiteSpace(pane.WorkId))
        {
            obj["work_id"] = pane.WorkId;
            obj["generation"] = pane.WorkGeneration;
        }
        if (!string.IsNullOrWhiteSpace(pane.Home))
            obj["home"] = pane.Home;
        var session = AgentSessionToJson(pane.AgentSession);
        if (session is not null)
            obj["agent_session"] = session;
        return obj;
    }

    private JsonObject TokensToJson(string paneId)
    {
        var tokens = _metadata.Get("pane", paneId);
        return new JsonObject(tokens.ToDictionary(
            p => p.Key,
            p => (JsonNode?)JsonValue.Create(p.Value),
            StringComparer.Ordinal));
    }

    private static JsonNode? BindingToJson(AtomicBinding? b)
    {
        if (b is null)
            return null;
        return new JsonObject
        {
            ["agent_session_id"] = b.AgentSessionId,
            ["run_id"] = b.RunId,
            ["step_id"] = b.StepId,
            ["memory_id"] = b.MemoryId,
            ["project_root"] = b.ProjectRoot,
            ["tenant_id"] = b.TenantId,
        };
    }

    private static AtomicBinding? ReadBinding(
        BindingDto? nested,
        string? agentSessionId,
        string? runId,
        string? stepId,
        string? tenantId,
        string? memoryId,
        string? projectRoot)
    {
        if (nested is not null)
            return BindingFromDto(nested);

        var session = EmptyToNull(agentSessionId);
        var run = EmptyToNull(runId);
        var step = EmptyToNull(stepId);
        var tenant = EmptyToNull(tenantId);
        var memory = EmptyToNull(memoryId);
        var root = EmptyToNull(projectRoot);
        if (session is null && run is null && step is null && tenant is null &&
            memory is null && root is null)
            return null;
        return new AtomicBinding
        {
            AgentSessionId = session,
            RunId = run,
            StepId = step,
            MemoryId = memory,
            ProjectRoot = root,
            TenantId = tenant,
        };
    }

    private static AtomicBinding BindingFromDto(BindingDto b) => new()
    {
        AgentSessionId = EmptyToNull(b.AgentSessionId),
        RunId = EmptyToNull(b.RunId),
        StepId = EmptyToNull(b.StepId),
        MemoryId = EmptyToNull(b.MemoryId),
        ProjectRoot = EmptyToNull(b.ProjectRoot),
        TenantId = EmptyToNull(b.TenantId),
    };

    /// <summary>
    /// Explicit request binding, else workspace binding, else session binding.
    /// Ensures new panes inherit session-level <c>runtime.binding.set</c>.
    /// </summary>
    private AtomicBinding? ResolveInheritedBinding(AtomicBinding? explicitBinding, WorkspaceState ws)
    {
        if (explicitBinding is not null)
            return explicitBinding;
        if (ws.Binding is not null)
            return ws.Binding;
        return _state.Snapshot().Binding;
    }

    /// <summary>
    /// Admit a binding write: resolve mode from placement+governed and fail closed via
    /// <see cref="BindingMatrix.Validate"/>. Null binding is allowed only for local
    /// unmanaged (pre-binding local shells). Remote and local-governed fail closed on
    /// absent binding so pane/workspace placement cannot spawn unbound.
    /// Process tenant/run anchors are resolved from session + workspace + pane graph so
    /// pane-scoped sets cannot bypass singularity when session.Binding is still null.
    /// Does <b>not</b> capture sticky process anchors — callers must
    /// <see cref="CaptureProcessAnchorsIfGoverned"/> only after a successful apply.
    /// Caller must hold <see cref="_bindingMutationGate"/> for multi-step admit+apply.
    /// </summary>
    private AtomicBinding? AdmitBinding(
        AtomicBinding? binding,
        bool paneScoped,
        string? placement = null,
        bool? governed = null,
        string? existingTenantId = null,
        string? existingRunId = null)
    {
        var snap = _state.Snapshot();
        var mode = BindingMatrix.ResolveMode(
            placement ?? snap.Placement,
            governed ?? snap.Governed);

        // Local unmanaged may start unbound; remote/governed placement requires claims.
        if (binding is null && mode is BindingMode.LocalUnmanaged)
            return null;

        var (anchorTenant, anchorRun) = _state.ResolveProcessBindingAnchors();
        var tenant = existingTenantId ?? anchorTenant;
        var run = existingRunId ?? anchorRun;
        var error = BindingMatrix.Validate(
            binding,
            mode,
            paneScoped: paneScoped,
            existingTenantId: tenant,
            existingRunId: run);
        if (error is not null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, error);

        return binding;
    }

    /// <summary>
    /// Pin process tenant/run anchors after a successful binding apply (session fill,
    /// pane update, workspace-only create, or successful pane start). Never call on
    /// admit alone, and never before a workspace-created default pane has started.
    /// </summary>
    private void CaptureProcessAnchorsIfGoverned(
        AtomicBinding? binding,
        string? placement = null,
        bool? governed = null)
    {
        if (binding is null)
            return;

        var snap = _state.Snapshot();
        var mode = BindingMatrix.ResolveMode(
            placement ?? snap.Placement,
            governed ?? snap.Governed);
        if (mode is BindingMode.LocalGoverned or BindingMode.Remote)
            _state.CaptureProcessBindingAnchors(binding.TenantId, binding.RunId);
    }

    private static JsonElement Ok(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonElement Ok(JsonArray arr)
    {
        using var doc = JsonDocument.Parse(arr.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string RequireField(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ControlPlaneException(-32602, $"{name} is required")
            : value;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ThrowFenced() =>
        throw new ControlPlaneException(ProtocolErrorCodes.Fenced, ProtocolErrors.Fenced);

    private void RememberWorkGeneration(string? workId, long? generation) =>
        _workGenerationGate.Remember(EmptyToNull(workId), generation);

    private void EnsureWorkGenerationAllowsStart(string? workId, long? generation)
    {
        if (!_workGenerationGate.AllowsStart(EmptyToNull(workId), generation))
            ThrowFenced();
    }

    private void EnsureWorkGenerationAllowsInput(string paneId)
    {
        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null)
            return;
        if (!_workGenerationGate.AllowsOccupant(pane.WorkId, pane.WorkGeneration))
            ThrowFenced();
    }

    private static PanePlacement ParsePanePlacement(string? raw)
    {
        if (PanePlacementWire.TryParse(raw, out var placement))
            return placement;
        throw new ControlPlaneException(
            ProtocolErrorCodes.InvalidParams,
            "placement must be hidden or tiled");
    }

    private string ResolvePaneHome(IReadOnlyDictionary<string, string>? spawnEnv)
    {
        if (spawnEnv is not null
            && spawnEnv.TryGetValue("HOME", out var home)
            && !string.IsNullOrWhiteSpace(home))
        {
            return Path.GetFullPath(home.Trim());
        }

        return _operatorHome;
    }

    private static string? ResolveProcessHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.GetFullPath(home.Trim());

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.GetFullPath(profile);
    }

    private IReadOnlyDictionary<string, string> WithManagedPaneId(
        IReadOnlyDictionary<string, string>? env,
        PaneId paneId)
    {
        var map = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal);
        ApplyPaneBaseEnv(map);
        if (!map.ContainsKey(PaneIdEnvironment.HypaPaneId))
            map[PaneIdEnvironment.HypaPaneId] = paneId.Value;
        var pane = _state.GetPane(paneId);
        if (pane is not null)
        {
            if (!map.ContainsKey(PaneIdEnvironment.HypaTabId))
                map[PaneIdEnvironment.HypaTabId] = pane.TabId.Value;
            if (!map.ContainsKey(PaneIdEnvironment.HypaWorkspaceId))
                map[PaneIdEnvironment.HypaWorkspaceId] = pane.WorkspaceId.Value;
        }

        return map;
    }

    /// <summary>
    /// Socket and CLI path for every pane child, including a popup.
    /// The mux overwrites both keys when it knows them.
    /// </summary>
    private void ApplyPaneBaseEnv(Dictionary<string, string> map)
    {
        if (!string.IsNullOrEmpty(_runtimeSocketPath))
            map[CustomCommandEnvironment.RuntimeSocket] = _runtimeSocketPath;
        if (!string.IsNullOrEmpty(_cliBinPath))
            map[CustomCommandEnvironment.BinPath] = _cliBinPath;
    }

    /// <summary>
    /// for Managed identity. Hypa copies that public id and always overwrites
    /// HYPA_PANE_TOKEN with the issued occupant secret.
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithOccupantIdentity(
        IReadOnlyDictionary<string, string>? env,
        PaneId paneId,
        string occupantToken)
    {
        var map = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal);
        map[PaneIdEnvironment.HypaPaneId] = paneId.Value;
        map[PaneIdEnvironment.HypaPaneToken] = occupantToken;
        return map;
    }

    private void RejectHiddenPane(PaneState pane, string operation)
    {
        if (pane.Placement != PanePlacement.Hidden)
            return;
        throw new ControlPlaneException(
            ProtocolErrorCodes.InvalidParams,
            $"cannot {operation} a hidden pane");
    }

    private void RejectHiddenPaneId(string? paneId, string operation)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is { Placement: PanePlacement.Hidden })
            RejectHiddenPane(pane, operation);
    }

    /// <summary>
    // A live
    /// group with an empty command is a miss. After a live group was
    /// seen, a null probe is a miss, not spawn <c>pane.Command</c>.
    /// Spawn command is only the fallback when no group was observed
    /// (<see cref="NullPaneProcessInfoProbe"/>).
    /// </summary>
    private DetectionProcessProbe ResolveDetectionProbe(
        IPaneRuntime runtime,
        PaneState pane,
        AgentDetectionPresence presence)
    {
        if (runtime.Pid is int pid)
        {
            var foreground = _processInfoProbe.TryGetForegroundInfo(pid);
            if (foreground is not null)
            {
                presence.MarkForegroundGroupObserved();
                presence.NoteProcessCheck(_time.GetUtcNow(), foreground.GroupId);
                var command = string.IsNullOrWhiteSpace(foreground.Command)
                    ? null
                    : foreground.Command;
                return new DetectionProcessProbe(command, foreground.ForegroundIsPaneShell);
            }
        }

        if (presence.SawForegroundGroup)
        {
            presence.NoteProcessCheck(_time.GetUtcNow(), groupId: null);
            return new DetectionProcessProbe(null, ForegroundIsPaneShell: false);
        }

        return new DetectionProcessProbe(pane.Command, ForegroundIsPaneShell: false);
    }

    /// <summary>
    /// Explain-only process name. Does not mutate presence.
    /// After a live group was seen, do not revive spawn argv.
    /// </summary>
    private string? ResolveDetectionProcessName(IPaneRuntime runtime, PaneState pane)
    {
        if (runtime.Pid is int pid)
        {
            var foreground = _processInfoProbe.TryGetForegroundInfo(pid);
            if (foreground is not null)
            {
                return string.IsNullOrWhiteSpace(foreground.Command)
                    ? null
                    : foreground.Command;
            }
        }

        if (_agentPresence.TryGetValue(pane.Id.Value, out var presence)
            && presence.OccupantGeneration == pane.OccupantGeneration
            && presence.SawForegroundGroup)
        {
            return null;
        }

        return pane.Command;
    }

    /// <summary>
    /// kind is set, the pane-shell clear is not pending, the group did
    /// not change, and less than <c>PROCESS_RECHECK_IDENTIFIED</c> passed.
    /// Cheap group lookup still runs so a job change probes at once.
    /// </summary>
    private bool ShouldSkipExpensiveProcessProbe(
        AgentDetectionPresence presence,
        int? currentGroupId)
    {
        if (presence.PendingForegroundShellClear)
            return false;
        if (presence.CurrentAgent is null)
            return false;
        if (presence.LastProcessCheck == default)
            return false;
        if (currentGroupId != presence.LastForegroundGroupId)
            return false;
        return _time.GetUtcNow() - presence.LastProcessCheck
            < AgentDetectionPresence.ProcessRecheckIdentified;
    }

    /// <summary>
    /// <c>src/pane.rs:1021-1044</c> process identity, then
    /// <c>src/detect/mod.rs:287-309</c> screen+OSC state.
    /// Occupancy is <see cref="AgentDetectionPresence.CurrentAgent"/>.
    /// </summary>
    private DetectionResult DetectOccupant(
        IPaneRuntime runtime,
        PaneState pane,
        out bool processCleared)
    {
        lock (GetPaneDetectionLock(pane.Id.Value))
        {
            var presence = _agentPresence.GetOrAdd(
                pane.Id.Value,
                static _ => new AgentDetectionPresence());
            if (presence.OccupantGeneration != pane.OccupantGeneration)
            {
                var previousGenerationAgent = presence.CurrentAgent;
                presence.Reset(pane.OccupantGeneration);
                // previous agent keeps OSC the new process already emitted.
                if (previousGenerationAgent is not null)
                    runtime.ClearAgentOscState();
            }

            var currentGroupId = runtime.Pid is int pid
                ? _processInfoProbe.TryGetForegroundGroup(pid)
                : null;
            if (ShouldSkipExpensiveProcessProbe(presence, currentGroupId))
            {
                processCleared = false;
                var skippedKind = presence.CurrentAgent;
                if (skippedKind is null)
                    return new DetectionResult { Status = AgentStatus.Unknown, Confidence = 0 };
                return _detector.Detect(
                    runtime.ReadDetectionText(),
                    skippedKind,
                    runtime.ReadDetectionOscTitle(),
                    runtime.ReadDetectionOscProgress());
            }

            var probe = ResolveDetectionProbe(runtime, pane, presence);
            string? identified = AgentKindCatalog.TryResolve(probe.ProcessName, out var canonical)
                ? canonical
                : null;
            var previous = presence.CurrentAgent;
            var action = ForegroundShellAgentActions.Resolve(
                previous,
                identified,
                probe.ForegroundIsPaneShell,
                presence.ForegroundShellExitReported);
            var changed = presence.ApplyForegroundShellAction(action, previous, identified);
            if (changed && previous is not null)
                runtime.ClearAgentOscState();

            var kind = presence.CurrentAgent;
            processCleared = action is ForegroundShellAgentAction.ClearAgent
                || (changed && kind is null);

            // first pane-shell return publishes Idle with kind still set.
            var processExited = presence.PendingForegroundShellClear
                && kind is not null
                && !presence.ForegroundShellExitReported;
            if (processExited)
            {
                return new DetectionResult
                {
                    Status = AgentStatus.Idle,
                    AgentKind = kind,
                    Confidence = 0.4,
                    ProcessExited = true,
                };
            }

            if (kind is null)
                return new DetectionResult { Status = AgentStatus.Unknown, Confidence = 0 };

            return _detector.Detect(
                runtime.ReadDetectionText(),
                kind,
                runtime.ReadDetectionOscTitle(),
                runtime.ReadDetectionOscProgress());
        }
    }

    /// <summary>
    /// is applied, not when the probe first decides to publish it.
    /// </summary>
    private void LatchForegroundShellExitIfPublished(
        string paneId,
        DetectionResult? detection,
        PaneState? pane)
    {
        if (detection is not { ProcessExited: true })
            return;
        if (pane is not { AgentStatus: AgentStatus.Idle })
            return;
        if (!string.Equals(pane.AgentKind, detection.AgentKind, StringComparison.Ordinal))
            return;
        if (_agentPresence.TryGetValue(paneId, out var reported))
            reported.MarkForegroundShellExitReported();
    }

    /// <summary>
    /// pending pane-shell clear on the 300 ms tick, identified occupancy
    /// argv recheck at 5 s, unidentified occupancy at 500 ms.
    /// <c>src/pane.rs:2441</c> pending restore probe does not apply.
    /// </summary>
    private void ArmOccupancyRecheck(string paneId)
    {
        var pane = _state.GetPane(new PaneId(paneId));
        if (pane is null || HoldsSemanticAuthority(pane) || !pane.IsAlive)
        {
            _detectionScanner.CancelRecheck(paneId);
            return;
        }

        if (!_agentPresence.TryGetValue(paneId, out var presence)
            || presence.OccupantGeneration != pane.OccupantGeneration)
        {
            _detectionScanner.CancelRecheck(paneId);
            return;
        }

        if (presence.PendingForegroundShellClear)
        {
            _detectionScanner.ScheduleRecheck(
                paneId,
                pane.OccupantGeneration,
                PaneDetectionScanner.DetectionTick);
            return;
        }

        if (presence.CurrentAgent is not null)
        {
            var delay = AgentDetectionPresence.ProcessRecheckIdentified;
            if (presence.LastProcessCheck != default)
            {
                delay = presence.LastProcessCheck
                    + AgentDetectionPresence.ProcessRecheckIdentified
                    - _time.GetUtcNow();
            }

            _detectionScanner.ScheduleRecheck(
                paneId,
                pane.OccupantGeneration,
                delay);
            return;
        }

        _detectionScanner.ScheduleRecheck(
            paneId,
            pane.OccupantGeneration,
            PaneDetectionScanner.TickUnidentified);
    }

    private readonly record struct DetectionProcessProbe(
        string? ProcessName,
        bool ForegroundIsPaneShell);

    private static string? RejectUnsafeLabel(string? label, string name = "label")
    {
        if (string.IsNullOrEmpty(label))
            return label;
        if (SafeDisplayText.ContainsUnsafeControl(label))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"{name} must not contain control characters");
        }

        return label;
    }

    private string ResolveWorkspaceCwd(string? callerCwd)
    {
        string? source = null;
        var focused = _state.Snapshot().FocusedWorkspaceId;
        if (focused is { } id)
            source = _state.GetWorkspace(id)?.Cwd;
        return TerminalSpawnPolicy.ResolveNewCwd(
            AttachConfig.Terminal,
            EmptyToNull(callerCwd),
            source,
            Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Persist the in-memory session graph under a single control-plane gate.
    /// Snapshot is captured only while holding the gate, immediately before SaveAsync,
    /// so concurrent triggers cannot overwrite a newer graph with a stale snapshot.
    /// Best-effort callers coalesce into a single-flight loop of the latest graph.
    /// When <paramref name="requireDurable"/> is true, failure surfaces as
    /// <c>persistence_unavailable</c> (-32011).
    /// </summary>
    private async Task PersistGraphAsync(bool requireDurable, bool removeMissingPanes = false)
    {
        if (_store is null)
        {
            if (!IsShuttingDown)
                PersistPaneHistory();
            return;
        }

        if (removeMissingPanes)
            Interlocked.Exchange(ref _persistRemoveMissing, 1);

        var prunePending = Volatile.Read(ref _persistRemoveMissing) != 0;

        // After ShutdownAsync starts, do not spawn more best-effort writers. Testhost
        // teardown (and ClearPool) races an in-flight SaveAsync and can crash native
        // SQLite. Durable shutdown persist still runs and waits for the gate.
        // A pending prune must still run so a failed close write can retry.
        if (IsShuttingDown && !requireDurable && !removeMissingPanes && !prunePending)
            return;

        // Mark dirty so an in-flight writer re-snapshots the latest graph before exit.
        Interlocked.Exchange(ref _persistDirty, 1);

        if (!requireDurable && !removeMissingPanes && !prunePending)
        {
            // Best-effort single-flight: if a writer holds the gate, it will observe dirty.
            if (!await _persistGate.WaitAsync(0).ConfigureAwait(false))
                return;
        }
        else
        {
            await _persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            // Loop while concurrent mutations dirtied state after our last snapshot.
            while (true)
            {
                Interlocked.Exchange(ref _persistDirty, 0);
                // Keep event_cursor aligned with the journal allocator (avoid stale overwrite).
                SyncJournalFlagsToAppState();
                // Snapshot only under the gate, immediately before SaveAsync.
                var snap = _state.Snapshot();
                var remove = Volatile.Read(ref _persistRemoveMissing) != 0;

                try
                {
                    var result = await _store.SaveAsync(snap, CancellationToken.None, remove)
                        .ConfigureAwait(false);
                    if (!result.IsOk)
                    {
                        _logger.LogError(
                            "Failed to persist session graph: {Code} {Message}",
                            result.Error.Code, result.Error.Message);
                        if (requireDurable)
                        {
                            throw new ControlPlaneException(
                                ProtocolErrorCodes.PersistenceUnavailable,
                                result.Error.Message);
                        }
                    }
                    else if (remove)
                    {
                        Interlocked.Exchange(ref _persistRemoveMissing, 0);
                    }
                }
                catch (ControlPlaneException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected failure persisting session graph");
                    if (requireDurable)
                    {
                        throw new ControlPlaneException(
                            ProtocolErrorCodes.PersistenceUnavailable,
                            ProtocolErrors.MeaningOf(ProtocolErrorCodes.PersistenceUnavailable));
                    }
                }

                if (Volatile.Read(ref _persistDirty) == 0)
                    break;
            }

            if (!IsShuttingDown)
                PersistPaneHistory();
        }
        finally
        {
            _persistGate.Release();
            // Lost best-effort wake: a concurrent caller saw the gate busy after we cleared
            // dirty but before release. Re-enter if still dirty — never after shutdown.
            if (!IsShuttingDown && Volatile.Read(ref _persistDirty) != 0)
                _ = PersistGraphAsync(requireDurable: false);
        }
    }

    /// <summary>
    /// Flag off removes the history file. Flag on writes a separate file.
    /// </summary>
    private void PersistPaneHistory()
    {
        try
        {
            if (!AttachConfig.Experimental.PaneHistory)
            {
                var cleared = _paneHistoryStore.Clear();
                if (!cleared.IsOk)
                {
                    _logger.LogWarning(
                        "failed to clear session history file: {Message}",
                        cleared.Error.Message);
                }

                return;
            }

            Dictionary<string, IPaneRuntime> runtimes;
            lock (_gate)
            {
                runtimes = new Dictionary<string, IPaneRuntime>(_runtimes, StringComparer.Ordinal);
            }

            var snapshot = PaneHistorySnapshotter.Capture(
                _state,
                id => runtimes.TryGetValue(id.Value, out var runtime) ? runtime : null);
            var saved = _paneHistoryStore.Save(snapshot);
            if (!saved.IsOk)
            {
                _logger.LogWarning(
                    "failed to save session history file: {Message}",
                    saved.Error.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to persist pane screen history");
        }
    }

    // ── events / health / observe ──────────────────────────────────────

    private JsonObject EventsSubscribe(EventsSubscribeParams p, IClientConnection? connection)
    {
        if (_subscriptions is null || _journal is null)
            throw new ControlPlaneException(ProtocolErrorCodes.PersistenceUnavailable, "Event journal not configured");
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "events.subscribe requires a live connection");

        var healthBefore = _journal.GetHealth();
        var live = p.Live ?? true;
        var budget = p.ReplayBudget ?? 1000;
        if (budget < 0)
            budget = 0;

        // No from_seq means "what is retained, then live". A cursor below the
        // floor expires only when the client asks for replay from it; a
        // live-only subscribe (replay_budget 0) has lost nothing.
        var fromSeq = p.FromSeq ?? Math.Max(0, healthBefore.FloorSeq - 1);
        if (p.FromSeq is not null
            && budget > 0
            && healthBefore.FloorSeq > 0
            && fromSeq + 1 < healthBefore.FloorSeq)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "Journal cursor is below the retention floor",
                ProtocolErrors.CursorExpired,
                healthBefore.FloorSeq);
        }

        var filter = ParseSubscribeFilter(p.Types);
        var subId = "sub_" + Interlocked.Increment(ref _subCounter);

        _subscriptions.Register(new EventSubscriptionRequest
        {
            SubscriptionId = subId,
            ConnectionId = connection.ConnectionId,
            FromSeq = fromSeq,
            Classes = filter.Classes,
            ReplayBudget = budget,
            Live = live,
            Sink = connection,
            NamedTypes = filter.NamedTypes,
            HasClassTokens = filter.HasClassTokens,
            FilterPaneId = string.IsNullOrWhiteSpace(p.PaneId) ? null : p.PaneId.Trim(),
            FilterAgentStatus = string.IsNullOrWhiteSpace(p.AgentStatus)
                ? null
                : p.AgentStatus.Trim().ToLowerInvariant(),
        });

        if (live)
            _visibleSets.NoteAttached(connection.ConnectionId);

        var health = _journal.GetHealth();
        lock (_gate)
        {
            if (!_attachClientModes.ContainsKey(connection.ConnectionId))
                _attachClientModes[connection.ConnectionId] = "terminal";
        }

        return new JsonObject
        {
            ["subscription_id"] = subId,
            ["attach_client_id"] = connection.ConnectionId,
            ["from_seq"] = fromSeq,
            ["next_seq"] = health.NextSeq,
            ["replay_complete"] = health.ReplayComplete,
            ["attach_client_id"] = connection.ConnectionId,
        };
    }

    private JsonObject EventsUnsubscribe(EventsUnsubscribeParams p, IClientConnection? connection)
    {
        if (_subscriptions is null)
            throw new ControlPlaneException(ProtocolErrorCodes.PersistenceUnavailable, "Event hub not configured");
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "events.unsubscribe requires a live connection");

        var subId = RequireField(p.SubscriptionId, "subscription_id");
        var sub = _subscriptions.Get(subId);
        if (sub is null || sub.IsClosed)
        {
            // Idempotent: unknown/closed id reports closed=false without free of foreign slots.
            return new JsonObject
            {
                ["subscription_id"] = subId,
                ["closed"] = false,
            };
        }

        if (!string.Equals(sub.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "subscription does not belong to this connection");

        // Free attachment slots bound to this subscription (design §8.1 / max_attachments).
        _attachments.DropBySubscription(subId);
        var closed = _subscriptions.Unregister(subId);
        return new JsonObject
        {
            ["subscription_id"] = subId,
            ["closed"] = closed,
        };
    }

    private JsonObject RuntimeHealth()
    {
        var snap = _state.Snapshot();
        var health = _journal?.GetHealth() ?? new JournalHealth
        {
            NextSeq = snap.NextEventSeq,
            ReplayComplete = snap.ReplayComplete,
            Bytes = 0,
            ReplayError = snap.ReplayError,
        };

        var ready = !IsShuttingDown
            && (_journal is null
                || (health.ReplayComplete && string.IsNullOrEmpty(health.ReplayError)));

        return new JsonObject
        {
            ["ready"] = ready,
            ["runtime_session_id"] = snap.Id.Value,
            ["protocol_major"] = ProtocolVersion.Major,
            ["protocol_minor"] = ProtocolVersion.Minor,
            ["persistence_schema"] = RuntimePersistenceSchema.Version,
            ["journal"] = BuildJournalHealthObject(health),
            ["pty"] = new JsonObject
            {
                ["provider"] = _ptyProvider,
                ["interactive"] = _ptyInteractive,
            },
            ["vt"] = BuildVtHealthObject(),
            ["limits"] = new JsonObject
            {
                ["max_panes"] = 64,
                ["max_attachments"] = 16,
            },
            // Advertise method families that DispatchAsync currently implements
            // .
            ["capabilities"] = BuildCapabilitiesArray(),
        };
    }

    internal bool AdvertisesHandoff =>
        !OperatingSystem.IsWindows()
        && string.Equals(_ptyProvider, "hypa-pty-host", StringComparison.Ordinal);

    internal IReadOnlySet<string> AdvertisedCapabilitySet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            ProtocolCapabilities.Core,
            ProtocolCapabilities.Events,
            ProtocolCapabilities.Leases,
            ProtocolCapabilities.Terminal,
            ProtocolCapabilities.Binding,
            ProtocolCapabilities.AgentWait,
            ProtocolCapabilities.Layout,
        };
        if (_checkpoints is not null)
            set.Add(ProtocolCapabilities.Checkpoint);
        if (AdvertisesHandoff)
            set.Add(ProtocolCapabilities.Handoff);
        set.Add(ProtocolCapabilities.Notification);
        set.Add(ProtocolCapabilities.AttachEndpoint);
        return set;
    }

    private JsonArray BuildCapabilitiesArray()
    {
        // AOT-safe: JsonValue.Create + JsonArray(JsonNode[]) — avoid Add<T> on non-JsonNode.
        var advertised = AdvertisedCapabilitySet();
        var list = new List<JsonNode?>(advertised.Count);
        foreach (var token in ProtocolCapabilities.All)
        {
            if (advertised.Contains(token))
                list.Add(JsonValue.Create(token));
        }

        return new JsonArray(list.ToArray());
    }

    private JsonObject BuildVtHealthObject()
    {
        // Build JsonArray from string primitives only (AOT / trim safe — no generic Add).
        JsonNode[] capNodes = new JsonNode[_vtCapabilities.Length];
        for (var i = 0; i < _vtCapabilities.Length; i++)
            capNodes[i] = JsonValue.Create(_vtCapabilities[i])!;

        var vt = new JsonObject
        {
            ["provider"] = _vtProvider,
            ["capabilities"] = new JsonArray(capNodes),
            // Default pane / floor dimensions (not max over live panes).
            ["cols"] = VtFloorDefaults.DefaultCols,
            ["rows"] = VtFloorDefaults.DefaultRows,
        };

        // additive Ghostty fields — present only when non-null so Basic health stays minimal.
        if (!string.IsNullOrEmpty(_vtGhosttyVersion))
            vt["ghostty_version"] = _vtGhosttyVersion;
        if (!string.IsNullOrEmpty(_vtGhosttyBuild))
            vt["ghostty_build"] = _vtGhosttyBuild;
        if (!string.IsNullOrEmpty(_vtAbi))
            vt["abi"] = _vtAbi;
        if (!string.IsNullOrEmpty(_vtFallbackReason))
            vt["fallback_reason"] = _vtFallbackReason;

        return vt;
    }

    private async Task<JsonObject> RuntimeBindingSetAsync(BindingSetParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation("runtime.binding.set");
        if (p.Binding is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "runtime.binding.set requires binding object");
        }

        var binding = BindingFromDto(p.Binding);
        var paneId = EmptyToNull(p.PaneId);
        var governedParam = p.Governed;
        var paneScoped = !string.IsNullOrWhiteSpace(paneId);

        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        string? emitPaneId;
        try
        {
            // Re-check freeze under the same gate as the binding write.
            EnsureNotFrozenForMutation("runtime.binding.set");

            var snap = _state.Snapshot();
            // Governed is sticky for admit+persist: once the session is governed, an explicit
            // governed:false cannot downgrade the matrix (fail-closed local governed).
            var effectiveGoverned = snap.Governed || governedParam == true;

            // Central admission: matrix fail-closed for governed/remote on every write surface.
            // Process anchors (tenant/run) come from session + panes, not session.Binding alone.
            // Anchors are captured only after a successful apply (below).
            AdmitBinding(
                binding,
                paneScoped: paneScoped,
                placement: snap.Placement,
                governed: effectiveGoverned);

            emitPaneId = paneId;
            if (paneScoped)
            {
                // Pane-scoped: update only that pane (+ intelligence). Do not clobber session
                // binding or peer panes (design §7.2 multi-pane Step-owned isolation).
                var updated = _state.UpdatePane(new PaneId(paneId!), pane => pane with { Binding = binding });
                if (updated is null)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
                _intelligence.BindAtomic(new PaneId(paneId!), binding);

                // Sticky: only elevate Governed, never clear via pane-scoped set.
                if (governedParam == true)
                    _state.UpdateSession(s => s with { Governed = true });

                CaptureProcessAnchorsIfGoverned(
                    binding, placement: snap.Placement, governed: effectiveGoverned);

                emitPaneId = paneId;
            }
            else
            {
                // Session-scoped: store session binding and fill null panes under one AppState
                // lock so concurrent pane-scoped set cannot be clobbered by a stale null check.
                // SetSessionBindingFillNullPanes also keeps Governed sticky.
                var filled = _state.SetSessionBindingFillNullPanes(binding, effectiveGoverned);
                foreach (var filledId in filled)
                    _intelligence.BindAtomic(filledId, binding);

                CaptureProcessAnchorsIfGoverned(
                    binding, placement: snap.Placement, governed: effectiveGoverned);

                // Prefer a known pane for Atomic join on binding.changed.
                emitPaneId = snap.Panes.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        await EmitBindingChangedAsync(emitPaneId, binding, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["binding"] = BindingToJson(binding),
        };
    }

    private JsonObject RuntimeBindingGet(BindingGetParams p)
    {
        var snap = _state.Snapshot();
        var paneId = EmptyToNull(p.PaneId);
        AtomicBinding? binding = snap.Binding;
        if (!string.IsNullOrWhiteSpace(paneId))
        {
            var pane = _state.GetPane(new PaneId(paneId));
            if (pane is null)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            binding = pane.Binding ?? snap.Binding;
        }

        return new JsonObject
        {
            ["binding"] = BindingToJson(binding),
        };
    }

    private async Task<JsonObject> EventsExportAckAsync(ExportAckParams p, CancellationToken ct)
    {
        if (_evidence is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                "Evidence journal not configured");
        }

        var runtimeSessionId = RequireField(p.RuntimeSessionId, "runtime_session_id");
        var exportId = RequireField(p.ExportId, "export_id");
        var lastSeq = p.LastSeq ?? -1;
        var consumer = EmptyToNull(p.Consumer) ?? "atomic";

        // Design injection rule: reject CR/LF/ASCII controls in client-controlled identity
        // strings before admit so durable payloads and NDJSON frames stay well-formed.
        RejectControlCharacters(exportId, "export_id");
        RejectControlCharacters(consumer, "consumer");
        RejectControlCharacters(runtimeSessionId, "runtime_session_id");

        var snap = _state.Snapshot();
        if (!string.Equals(runtimeSessionId, snap.Id.Value, StringComparison.Ordinal))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                "runtime_session_id does not match live session");
        }

        if (lastSeq < 0)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "last_seq must be >= 0");
        }

        var nextSeq = _journal?.NextSeq ?? snap.NextEventSeq;
        // last_seq must refer to an already-allocated durable seq (strictly < next allocator).
        if (lastSeq >= nextSeq)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"last_seq must be < journal next_seq ({nextSeq})");
        }

        var ack = await _evidence.AckAsync(
            snap.Id.Value, exportId, lastSeq, consumer, ct).ConfigureAwait(false);
        if (!ack.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                ack.Error.Message);
        }

        // Emit and return the stored high-water (may be > requested last_seq on retry).
        var storedSeq = ack.Value.LastAckedSeq;
        var storedExportId = ack.Value.ExportId;
        await EmitExportAckedAsync(storedExportId, storedSeq, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["export_id"] = storedExportId,
            ["last_acked_seq"] = storedSeq,
            ["acked"] = true,
        };
    }

    private async Task EmitBindingChangedAsync(
        string? paneId, AtomicBinding binding, CancellationToken ct)
    {
        if (_journal is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteBindingChanged(paneId, binding);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.BindingChanged, payload);

        await PublishReliableAsync(
            EventClass.Control,
            ProtocolEventTypes.BindingChanged,
            payload,
            ct).ConfigureAwait(false);
    }

    private async Task EmitExportAckedAsync(string exportId, long ackedSeq, CancellationToken ct)
    {
        if (_journal is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteExportAcked(exportId, ackedSeq);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.ExportAcked, payload);

        await PublishReliableAsync(
            EventClass.Control,
            ProtocolEventTypes.ExportAcked,
            payload,
            ct).ConfigureAwait(false);
    }

    // ── checkpoint prepare / export / abort ─────────────────────────────

    private async Task<JsonObject> RuntimeCheckpointPrepareAsync(CheckpointPrepareParams p, CancellationToken ct)
    {
        if (_checkpoints is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                "Checkpoint service not configured");
        }

        var reason = EmptyToNull(p.Reason);
        if (reason is not null)
            RejectControlCharacters(reason, "reason");

        var includeScrollback = p.IncludeScrollback ?? false;

        // Gate order: checkpoint → binding → emit (never reverse) to avoid deadlock.
        await _checkpointGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Serialize with graph mutations so freeze+fingerprint is not raced by create/bind.
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            long nextSeq = 1;
            long barrierSeq = 0;
            string fingerprint = "";
            string sessionId = "";
            string placement = "";
            int placementGeneration = 0;
            bool replayComplete = false;
            bool includeWorkspaceFiles;
            string checkpointId;
            DateTimeOffset now = default;
            string priorLifecycle;
            ulong? pinDev = null;
            ulong? pinIno = null;
            bool? pinWasSymlink = null;
            bool pinTransferIncomplete = false;
            string? pinWarning = null;
            try
            {
                var pre = _state.Snapshot();
                if (SessionLifecycle.IsFrozen(pre.LifecycleState))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Session is already frozen_read_only");
                }

                if (pre.LifecycleState is SessionLifecycle.Stopping or SessionLifecycle.Stopped
                    or SessionLifecycle.Failed)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        $"Cannot prepare checkpoint in session state {pre.LifecycleState}");
                }

                priorLifecycle = pre.LifecycleState;
                // Default include_workspace_files: true when project_root present, else false.
                var hasProjectRoot = !string.IsNullOrWhiteSpace(pre.Binding?.ProjectRoot)
                    || pre.Workspaces.Values.Any(w => !string.IsNullOrWhiteSpace(w.Binding?.ProjectRoot)
                        || !string.IsNullOrWhiteSpace(w.Cwd));
                includeWorkspaceFiles = p.IncludeWorkspaceFiles ?? hasProjectRoot;

                // Barrier sample + freeze under emit gate and journal exclusive:
                // Output no longer takes _emitGate, so RunExclusive stops an in-flight
                // Output append from moving NextSeq between sample and FrozenReadOnly.
                await _emitGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    void SampleBarrierAndFreeze()
                    {
                        nextSeq = _journal?.NextSeq ?? Math.Max(1, pre.NextEventSeq);
                        if (nextSeq < 1)
                            nextSeq = 1;
                        barrierSeq = nextSeq <= 1 ? 0 : nextSeq - 1;

                        now = DateTimeOffset.UtcNow;
                        // Freeze first (in-memory), then fingerprint the frozen graph.
                        _state.UpdateSession(s => s with
                        {
                            LifecycleState = SessionLifecycle.FrozenReadOnly,
                            UpdatedAt = now,
                        });

                        var frozenSnap = _state.Snapshot();
                        fingerprint = SessionGraphFingerprint.Compute(frozenSnap);
                        sessionId = frozenSnap.Id.Value;
                        placement = frozenSnap.Placement;
                        placementGeneration = frozenSnap.PlacementGeneration;
                        replayComplete = frozenSnap.ReplayComplete;

                        // Pin under the same freeze gates as fingerprint. A later symlink
                        // is not an operator root. Fail closed when a walk or git needs a pin.
                        var root = ResolveSessionProjectRoot(frozenSnap);
                        if (!string.IsNullOrWhiteSpace(root)
                            && _checkpoints.TryPinProjectRoot(root, out var d, out var i, out var w))
                        {
                            pinDev = d;
                            pinIno = i;
                            pinWasSymlink = w;
                        }
                        else if (includeWorkspaceFiles)
                        {
                            includeWorkspaceFiles = false;
                            pinTransferIncomplete = true;
                            pinWarning = string.IsNullOrWhiteSpace(root)
                                ? "workspace: project_root missing; walk skipped"
                                : "workspace: project_root pin failed; walk skipped";
                        }
                    }

                    if (_journal is not null)
                        _journal.RunExclusive(SampleBarrierAndFreeze);
                    else
                        SampleBarrierAndFreeze();
                }
                finally
                {
                    _emitGate.Release();
                }

                checkpointId = "cp_" + Guid.NewGuid().ToString("N")[..12];
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            // Memory-frozen: mutations fail EnsureNotFrozen. Persist record then durable freeze.
            // Any failure rolls back lifecycle so the session is never stuck frozen without a row.
            try
            {
                var record = new CheckpointRecord
                {
                    CheckpointId = checkpointId,
                    SessionId = sessionId,
                    State = CheckpointStates.Prepared,
                    BarrierSeq = barrierSeq,
                    NextSeqAtPrepare = nextSeq,
                    SessionFingerprint = fingerprint,
                    Reason = reason,
                    IncludeScrollback = includeScrollback,
                    IncludeWorkspaceFiles = includeWorkspaceFiles,
                    TransferIncomplete = pinTransferIncomplete,
                    Warnings = pinWarning is null ? [] : [pinWarning],
                    CreatedAt = now,
                    Placement = placement,
                    PlacementGeneration = placementGeneration,
                    ReplayComplete = replayComplete,
                    ProjectRootDevice = pinDev,
                    ProjectRootInode = pinIno,
                    ProjectRootWasSymlink = pinWasSymlink,
                };

                var saved = await _checkpoints.SavePreparedAsync(record, ct).ConfigureAwait(false);
                if (!saved.IsOk)
                {
                    await RestoreLifecycleAfterPrepareFailureAsync(priorLifecycle).ConfigureAwait(false);
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.PersistenceUnavailable,
                        saved.Error.Message);
                }

                try
                {
                    await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
                }
                catch
                {
                    await RestoreLifecycleAfterPrepareFailureAsync(priorLifecycle).ConfigureAwait(false);
                    // Best-effort mark prepared row failed so export does not claim a live freeze.
                    try
                    {
                        var failed = record with { State = CheckpointStates.Failed };
                        _ = await _checkpoints.SavePreparedAsync(failed, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort
                    }

                    throw;
                }
            }
            catch (ControlPlaneException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure preparing checkpoint");
                await RestoreLifecycleAfterPrepareFailureAsync(priorLifecycle).ConfigureAwait(false);
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    ProtocolErrors.MeaningOf(ProtocolErrorCodes.PersistenceUnavailable));
            }

            await EmitCheckpointLifecycleAsync(
                checkpointId, CheckpointStates.Prepared, barrierSeq, ct).ConfigureAwait(false);

            var frozen = _state.Snapshot();
            return new JsonObject
            {
                ["checkpoint_id"] = checkpointId,
                ["state"] = CheckpointStates.Prepared,
                ["barrier_seq"] = barrierSeq,
                ["runtime_session_id"] = frozen.Id.Value,
                ["session_state"] = SessionLifecycle.FrozenReadOnly,
                ["placement_generation"] = frozen.PlacementGeneration,
            };
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    /// <summary>
    /// Best-effort restore of session lifecycle after prepare fails post-freeze.
    /// Memory restore first; durable persist when possible (never leaves sticky freeze).
    /// </summary>
    private async Task RestoreLifecycleAfterPrepareFailureAsync(string priorLifecycle)
    {
        var restore = string.IsNullOrWhiteSpace(priorLifecycle)
            ? SessionLifecycle.Ready
            : priorLifecycle;
        if (SessionLifecycle.IsFrozen(restore)
            || restore is SessionLifecycle.Stopping or SessionLifecycle.Stopped
                or SessionLifecycle.Failed)
        {
            restore = SessionLifecycle.Ready;
        }

        try
        {
            _state.UpdateSession(s => s with
            {
                LifecycleState = restore,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await PersistGraphAsync(requireDurable: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unfreeze session after prepare failure");
            // Ensure in-memory is at least Ready even if durable write failed.
            _state.UpdateSession(s => s with
            {
                LifecycleState = SessionLifecycle.Ready,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private async Task<JsonObject> RuntimeCheckpointExportAsync(CheckpointExportParams p, CancellationToken ct)
    {
        if (_checkpoints is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                "Checkpoint service not configured");
        }

        var checkpointId = RequireField(p.CheckpointId, "checkpoint_id");
        RejectControlCharacters(checkpointId, "checkpoint_id");

        await _checkpointGate.WaitAsync(ct).ConfigureAwait(false);
        var holdsGate = true;
        var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var loaded = await _checkpoints.GetAsync(checkpointId, ct).ConfigureAwait(false);
            if (!loaded.IsOk)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    loaded.Error.Message);
            }

            var prepared = loaded.Value;
            if (prepared is null)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    $"Checkpoint not found: {checkpointId}");
            }

            var snap = _state.Snapshot();
            if (!string.Equals(prepared.SessionId, snap.Id.Value, StringComparison.Ordinal))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.NotFound,
                    "checkpoint_id does not belong to this runtime session");
            }

            // Idempotent re-export of an already-materialized checkpoint.
            // Must run before fingerprint demotion: abort-after-export leaves the row as
            // exported while unfreezing; later graph mutation must not clobber Exported→Conflict.
            // Never transition exported → conflict.
            if (string.Equals(prepared.State, CheckpointStates.Exported, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(prepared.ManifestRelativePath)
                    && !string.IsNullOrWhiteSpace(prepared.ManifestSha256))
                {
                    return new JsonObject
                    {
                        ["checkpoint_id"] = prepared.CheckpointId,
                        ["state"] = CheckpointStates.Exported,
                        ["barrier_seq"] = prepared.BarrierSeq,
                        ["manifest_path"] = prepared.ManifestRelativePath,
                        ["manifest_sha256"] = prepared.ManifestSha256,
                        ["byte_count"] = prepared.ByteCount ?? 0,
                        ["transfer_incomplete"] = prepared.TransferIncomplete,
                    };
                }

                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "Checkpoint is exported but durable manifest is missing");
            }

            if (string.Equals(prepared.State, CheckpointStates.Aborted, StringComparison.Ordinal)
                || string.Equals(prepared.State, CheckpointStates.Conflict, StringComparison.Ordinal))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    $"Checkpoint is {prepared.State} and cannot be exported");
            }

            // Non-exported rows require a live freeze. Session-only abort unfreezes
            // and must not leave a prepared row exportable.
            if (!SessionLifecycle.IsFrozen(snap.LifecycleState))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "Session is not frozen_read_only; cannot export a non-exported checkpoint");
            }

            // Fingerprint conflict only for prepared / failed-for-retry (never for exported).
            // Terminal output / pane process exit do not change fingerprint.
            var currentFp = SessionGraphFingerprint.Compute(snap);
            if (!string.Equals(currentFp, prepared.SessionFingerprint, StringComparison.Ordinal))
            {
                // Mark conflict and auto-unfreeze so the session is not permanently immutable.
                try
                {
                    var conflicted = prepared with { State = CheckpointStates.Conflict };
                    _ = await _checkpoints.SavePreparedAsync(conflicted, ct).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }

                await UnfreezeSessionAsync(ct).ConfigureAwait(false);
                await EmitCheckpointLifecycleAsync(
                    checkpointId, CheckpointStates.Conflict, prepared.BarrierSeq, ct)
                    .ConfigureAwait(false);
                throw new ControlPlaneException(
                    ProtocolErrorCodes.CheckpointConflict,
                    "session graph changed after prepare barrier");
            }

            lock (_checkpointIoSync)
            {
                if (_checkpointIoCts is not null)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Checkpoint export already in progress");
                }

                _checkpointIoCts = ioCts;
            }

            // Snapshot/fingerprint stay under the gate; workspace walk must not pin abort.
            _checkpointGate.Release();
            holdsGate = false;

            RuntimeResult<CheckpointExportMaterial>? material = null;
            try
            {
                material = await _checkpoints.BuildExportAsync(prepared, snap, ioCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // abort or caller cancelled the walk
            }
            finally
            {
                await _checkpointGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                holdsGate = true;
                lock (_checkpointIoSync)
                {
                    if (ReferenceEquals(_checkpointIoCts, ioCts))
                        _checkpointIoCts = null;
                }
            }

            var latestLoaded = await _checkpoints.GetAsync(checkpointId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!latestLoaded.IsOk)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    latestLoaded.Error.Message);
            }

            var latest = latestLoaded.Value;
            if (latest is not null
                && string.Equals(latest.State, CheckpointStates.Exported, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(latest.ManifestRelativePath)
                && !string.IsNullOrWhiteSpace(latest.ManifestSha256))
            {
                // Abort-after-export: durable already exported wins even if the CTS is flagged.
                return ExportedCheckpointJson(latest);
            }

            if (ioCts.IsCancellationRequested
                || latest is null
                || string.Equals(latest.State, CheckpointStates.Aborted, StringComparison.Ordinal)
                || string.Equals(latest.State, CheckpointStates.Conflict, StringComparison.Ordinal))
            {
                if (ct.IsCancellationRequested)
                    ct.ThrowIfCancellationRequested();

                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "Checkpoint export cancelled");
            }

            if (material is not { } built)
            {
                await EmitCheckpointLifecycleAsync(
                    checkpointId, CheckpointStates.Failed, prepared.BarrierSeq, ct)
                    .ConfigureAwait(false);
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    "Checkpoint export failed");
            }

            if (!built.IsOk)
            {
                await EmitCheckpointLifecycleAsync(
                    checkpointId, CheckpointStates.Failed, prepared.BarrierSeq, ct)
                    .ConfigureAwait(false);
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    built.Error.Message);
            }

            await _bindingMutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var commitSnap = _state.Snapshot();
                if (!SessionLifecycle.IsFrozen(commitSnap.LifecycleState))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Checkpoint export cancelled");
                }

                var commitFp = SessionGraphFingerprint.Compute(commitSnap);
                if (!string.Equals(commitFp, prepared.SessionFingerprint, StringComparison.Ordinal))
                {
                    try
                    {
                        var conflicted = (latest ?? prepared) with { State = CheckpointStates.Conflict };
                        _ = await _checkpoints.SavePreparedAsync(conflicted, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort
                    }

                    await UnfreezeSessionAsync(CancellationToken.None).ConfigureAwait(false);
                    await EmitCheckpointLifecycleAsync(
                        checkpointId, CheckpointStates.Conflict, prepared.BarrierSeq, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.CheckpointConflict,
                        "session graph changed after prepare barrier");
                }
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            var committed = await _checkpoints.CommitExportedAsync(built.Value, CancellationToken.None)
                .ConfigureAwait(false);
            if (!committed.IsOk)
            {
                var afterFail = await _checkpoints.GetAsync(checkpointId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (afterFail is { IsOk: true, Value: { } won }
                    && string.Equals(won.State, CheckpointStates.Exported, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(won.ManifestRelativePath))
                {
                    return ExportedCheckpointJson(won);
                }

                if (committed.Error.Code == RuntimePersistenceError.ConflictError
                    || (afterFail.IsOk
                        && afterFail.Value is { } demoted
                        && (string.Equals(demoted.State, CheckpointStates.Aborted, StringComparison.Ordinal)
                            || string.Equals(demoted.State, CheckpointStates.Conflict, StringComparison.Ordinal))))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.InvalidState,
                        "Checkpoint export cancelled");
                }

                await EmitCheckpointLifecycleAsync(
                    checkpointId, CheckpointStates.Failed, prepared.BarrierSeq, ct)
                    .ConfigureAwait(false);
                throw new ControlPlaneException(
                    ProtocolErrorCodes.PersistenceUnavailable,
                    committed.Error.Message);
            }

            await EmitCheckpointLifecycleAsync(
                checkpointId, CheckpointStates.Exported, prepared.BarrierSeq, ct)
                .ConfigureAwait(false);

            return ExportedCheckpointJson(committed.Value);
        }
        finally
        {
            ioCts.Dispose();
            if (holdsGate)
                _checkpointGate.Release();
        }
    }

    /// <summary>
    /// recovery: unfreeze <c>frozen_read_only</c> → <c>ready</c>.
    /// Demotes non-exported checkpoint rows to <c>aborted</c>; exported rows stay durable
    /// exported (Atomic pull / idempotent re-export). Only emit <c>checkpoint.lifecycle</c>
    /// with <c>aborted</c> when the durable row actually transitions to aborted.
    /// </summary>
    private async Task<JsonObject> RuntimeCheckpointAbortAsync(CheckpointAbortParams p, CancellationToken ct)
    {
        if (_checkpoints is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                "Checkpoint service not configured");
        }

        var checkpointId = EmptyToNull(p.CheckpointId);
        if (checkpointId is not null)
            RejectControlCharacters(checkpointId, "checkpoint_id");

        var reason = EmptyToNull(p.Reason);
        if (reason is not null)
            RejectControlCharacters(reason, "reason");

        // Cancel in-flight export I/O before waiting for the snapshot gate so a
        // FIFO/device hang cannot brick unfreeze.
        lock (_checkpointIoSync)
            _checkpointIoCts?.Cancel();

        await _checkpointGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Export publishes _checkpointIoCts only after it holds this gate.
            // Cancel again so a CTS assigned while we waited is not missed.
            lock (_checkpointIoSync)
                _checkpointIoCts?.Cancel();

            long barrierSeq = 0;
            string? resolvedId = checkpointId;
            // Durable wire state returned to the caller (not always aborted).
            var resultState = CheckpointStates.Aborted;
            var demotedToAborted = false;

            if (!string.IsNullOrWhiteSpace(checkpointId))
            {
                var loaded = await _checkpoints.GetAsync(checkpointId, ct).ConfigureAwait(false);
                if (!loaded.IsOk)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.PersistenceUnavailable,
                        loaded.Error.Message);
                }

                var record = loaded.Value;
                if (record is null)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        $"Checkpoint not found: {checkpointId}");
                }

                var snap = _state.Snapshot();
                if (!string.Equals(record.SessionId, snap.Id.Value, StringComparison.Ordinal))
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.NotFound,
                        "checkpoint_id does not belong to this runtime session");
                }

                var one = await DemoteCheckpointOnAbortAsync(record, reason, ct).ConfigureAwait(false);
                barrierSeq = record.BarrierSeq;
                resolvedId = record.CheckpointId;
                resultState = one.State;
                demotedToAborted = one.Demoted;
            }
            else
            {
                // Session-only abort: demote every non-exported row so a later export
                // cannot CAS prepared → exported after unfreeze.
                var listed = await _checkpoints.ListBySessionAsync(
                    _state.Snapshot().Id.Value, ct).ConfigureAwait(false);
                if (!listed.IsOk)
                {
                    throw new ControlPlaneException(
                        ProtocolErrorCodes.PersistenceUnavailable,
                        listed.Error.Message);
                }

                foreach (var record in listed.Value)
                {
                    var one = await DemoteCheckpointOnAbortAsync(record, reason, ct)
                        .ConfigureAwait(false);
                    if (one.Demoted)
                    {
                        demotedToAborted = true;
                        barrierSeq = record.BarrierSeq;
                        resolvedId = record.CheckpointId;
                        resultState = CheckpointStates.Aborted;
                        await EmitCheckpointLifecycleAsync(
                            record.CheckpointId, CheckpointStates.Aborted, record.BarrierSeq, ct)
                            .ConfigureAwait(false);
                    }
                }
            }

            await UnfreezeSessionAsync(ct).ConfigureAwait(false);

            // Emit checkpoint.lifecycle aborted only for a single-id demotion.
            // Session-only abort already emitted per demoted row above.
            if (demotedToAborted
                && !string.IsNullOrWhiteSpace(checkpointId)
                && !string.IsNullOrWhiteSpace(resolvedId))
            {
                await EmitCheckpointLifecycleAsync(
                    resolvedId, CheckpointStates.Aborted, barrierSeq, ct).ConfigureAwait(false);
            }
            else
            {
                await EmitSessionLifecycleAsync(SessionLifecycle.Ready, ct).ConfigureAwait(false);
            }

            var after = _state.Snapshot();
            var result = new JsonObject
            {
                ["state"] = resultState,
                ["session_state"] = SessionLifecycle.Ready,
                ["runtime_session_id"] = after.Id.Value,
            };
            if (resolvedId is not null)
                result["checkpoint_id"] = resolvedId;
            return result;
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private async Task<(string State, bool Demoted)> DemoteCheckpointOnAbortAsync(
        CheckpointRecord record,
        string? reason,
        CancellationToken ct)
    {
        if (string.Equals(record.State, CheckpointStates.Exported, StringComparison.Ordinal))
            return (CheckpointStates.Exported, false);

        if (string.Equals(record.State, CheckpointStates.Aborted, StringComparison.Ordinal))
            return (CheckpointStates.Aborted, false);

        var aborted = record with
        {
            State = CheckpointStates.Aborted,
            Warnings = string.IsNullOrWhiteSpace(reason)
                ? record.Warnings
                : record.Warnings.Append("abort: " + reason).ToList(),
        };
        var saved = await _checkpoints!.SavePreparedAsync(aborted, ct).ConfigureAwait(false);
        if (!saved.IsOk)
        {
            var again = saved.Error.Code == RuntimePersistenceError.ConflictError
                ? await _checkpoints.GetAsync(record.CheckpointId, ct).ConfigureAwait(false)
                : default;
            if (again is { IsOk: true, Value: { } won }
                && string.Equals(won.State, CheckpointStates.Exported, StringComparison.Ordinal))
            {
                return (CheckpointStates.Exported, false);
            }

            throw new ControlPlaneException(
                ProtocolErrorCodes.PersistenceUnavailable,
                saved.Error.Message);
        }

        return (CheckpointStates.Aborted, true);
    }

    private static JsonObject ExportedCheckpointJson(CheckpointRecord rec) => new()
    {
        ["checkpoint_id"] = rec.CheckpointId,
        ["state"] = CheckpointStates.Exported,
        ["barrier_seq"] = rec.BarrierSeq,
        ["manifest_path"] = rec.ManifestRelativePath,
        ["manifest_sha256"] = rec.ManifestSha256,
        ["byte_count"] = rec.ByteCount ?? 0,
        ["transfer_incomplete"] = rec.TransferIncomplete,
    };

    private static string? ResolveSessionProjectRoot(SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(session.Binding?.ProjectRoot))
            return session.Binding.ProjectRoot;
        foreach (var ws in session.Workspaces.Values)
        {
            if (!string.IsNullOrWhiteSpace(ws.Binding?.ProjectRoot))
                return ws.Binding.ProjectRoot;
            if (!string.IsNullOrWhiteSpace(ws.Cwd))
                return ws.Cwd;
        }

        return null;
    }

    /// <summary>Restore session lifecycle to ready (memory + best-effort durable).</summary>
    private async Task UnfreezeSessionAsync(CancellationToken ct)
    {
        var snap = _state.Snapshot();
        if (!SessionLifecycle.IsFrozen(snap.LifecycleState)
            && snap.LifecycleState == SessionLifecycle.Ready)
        {
            return;
        }

        _state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable unfreeze failed; session is ready in memory");
            await PersistGraphAsync(requireDurable: false).ConfigureAwait(false);
        }

        await DrainPendingCommandOverlayClosesAsync().ConfigureAwait(false);
    }

    /// <summary>Emit <c>session.lifecycle</c> (used for unfreeze without checkpoint demotion).</summary>
    private async Task EmitSessionLifecycleAsync(string state, CancellationToken ct)
    {
        if (_journal is null)
            return;

        var snap = _state.Snapshot();
        var payload = RuntimeEventPayloadJson.WriteSessionLifecycle(snap.Id.Value, state);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.SessionLifecycle, payload);

        await PublishReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.SessionLifecycle,
            payload,
            ct).ConfigureAwait(false);
    }

    private async Task EmitCheckpointLifecycleAsync(
        string checkpointId, string state, long barrierSeq, CancellationToken ct)
    {
        if (_journal is null)
            return;

        var payload = RuntimeEventPayloadJson.WriteCheckpointLifecycle(checkpointId, state, barrierSeq);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.CheckpointLifecycle, payload);

        await PublishReliableAsync(
            EventClass.Control,
            ProtocolEventTypes.CheckpointLifecycle,
            payload,
            ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> RuntimeLeaseClaimAsync(
        LeaseClaimParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "runtime.lease.claim requires a live connection");

        var paneId = RequireField(p.PaneId, "pane_id");
        var scope = RequireField(p.Scope, "scope").ToLowerInvariant();
        var takeover = p.Takeover ?? false;
        var reason = EmptyToNull(p.Reason);
        var ttlMs = p.TtlMs;

        if (_state.GetPane(new PaneId(paneId)) is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");

        if (!LeaseScopes.IsKnown(scope))
        {
            return new JsonObject
            {
                ["outcome"] = LeaseOutcomes.Invalid,
                ["lease_id"] = null,
            };
        }

        // Claim + ordered audit under one gate so concurrent takeovers cannot reorder
        // lease.changed release/grant pairs.
        await _leaseAuditGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // paneAlive under the registry lock keeps claim/takeover atomic with liveness
            // so a closed pane cannot lose the prior holder without a successful new grant.
            var claim = _leases.Claim(
                paneId,
                scope,
                connection.ConnectionId,
                takeover,
                reason,
                ttlMs,
                paneAlive: id => _state.GetPane(new PaneId(id)) is not null);

            // Invalid from paneAlive (or other validation): fail closed when the pane is gone.
            if (claim.Outcome == LeaseOutcomes.Invalid &&
                _state.GetPane(new PaneId(paneId)) is null)
            {
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            }

            // Re-check liveness after grant: concurrent pane.close can RemovePane between
            // Claim unlock and this check. Roll back residual grants and always emit audit
            // for every holder transition that actually mutated the registry.
            if ((claim.Outcome is LeaseOutcomes.Granted or LeaseOutcomes.AlreadyHeld) &&
                _state.GetPane(new PaneId(paneId)) is null)
            {
                var dropped = _leases.DropPane(paneId);
                if (claim.PreviousLease is not null)
                {
                    await EmitLeaseChangedAsync(
                        claim.PreviousLease,
                        actorId: connection.ConnectionId,
                        oldHolderId: claim.PreviousLease.HolderId,
                        newHolderId: null,
                        reason: reason,
                        policyDecision: takeover ? "takeover_abort_pane_gone" : "grant_abort_pane_gone",
                        ct).ConfigureAwait(false);
                }

                var emittedIds = new HashSet<string>(StringComparer.Ordinal);
                if (claim.PreviousLease is not null)
                    emittedIds.Add(claim.PreviousLease.LeaseId);

                foreach (var lease in dropped)
                {
                    if (emittedIds.Add(lease.LeaseId))
                    {
                        await EmitLeaseChangedAsync(
                            lease,
                            actorId: connection.ConnectionId,
                            oldHolderId: lease.HolderId,
                            newHolderId: null,
                            reason: reason,
                            policyDecision: "pane_close_drop",
                            ct).ConfigureAwait(false);
                    }
                }

                if (claim.Lease is not null &&
                    claim.Outcome == LeaseOutcomes.Granted &&
                    emittedIds.Add(claim.Lease.LeaseId))
                {
                    var releasedGrant = claim.Lease with { State = LeaseStates.Released };
                    await EmitLeaseChangedAsync(
                        releasedGrant,
                        actorId: connection.ConnectionId,
                        oldHolderId: releasedGrant.HolderId,
                        newHolderId: null,
                        reason: reason,
                        policyDecision: "grant_abort_pane_gone",
                        ct).ConfigureAwait(false);
                }

                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            }

            if (claim.PreviousLease is not null)
            {
                await EmitLeaseChangedAsync(
                    claim.PreviousLease,
                    actorId: connection.ConnectionId,
                    oldHolderId: claim.PreviousLease.HolderId,
                    newHolderId: claim.Lease?.HolderId,
                    reason: reason,
                    policyDecision: takeover ? "takeover_release" : "replaced",
                    ct).ConfigureAwait(false);
            }

            if (claim.Lease is not null && claim.Outcome is LeaseOutcomes.Granted or LeaseOutcomes.AlreadyHeld)
            {
                var decision = claim.Outcome == LeaseOutcomes.AlreadyHeld
                    ? "already_held"
                    : takeover && claim.PreviousLease is not null
                        ? "takeover_grant"
                        : "grant";
                await EmitLeaseChangedAsync(
                    claim.Lease,
                    actorId: connection.ConnectionId,
                    oldHolderId: claim.PreviousLease?.HolderId,
                    newHolderId: claim.Lease.HolderId,
                    reason: reason,
                    policyDecision: decision,
                    ct).ConfigureAwait(false);
            }

            // Wire lease_id only for granted / already_held. Denied/invalid/pending_approval
            // must not leak the foreign holder id (clients treat non-null as authority).
            var leaseIdOnWire =
                claim.Outcome is LeaseOutcomes.Granted or LeaseOutcomes.AlreadyHeld
                    ? claim.Lease?.LeaseId
                    : null;

            return new JsonObject
            {
                ["outcome"] = claim.Outcome,
                ["lease_id"] = leaseIdOnWire,
                ["retry_after_ms"] = claim.RetryAfterMs,
            };
        }
        finally
        {
            _leaseAuditGate.Release();
        }
    }

    private async Task<JsonObject> RuntimeLeaseReleaseAsync(
        LeaseReleaseParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "runtime.lease.release requires a live connection");

        var leaseId = RequireField(p.LeaseId, "lease_id");
        var result = _leases.Release(leaseId, connection.ConnectionId);
        if (result.Released && result.Lease is not null)
            await EmitLeaseChangedAsync(result.Lease, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["lease_id"] = leaseId,
            ["released"] = result.Released,
        };
    }

    private JsonObject RuntimeLeaseRenew(LeaseRenewParams p, IClientConnection? connection)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "runtime.lease.renew requires a live connection");

        var leaseId = RequireField(p.LeaseId, "lease_id");
        var ttlMs = p.TtlMs;

        // Reject renew for ghost leases whose pane is already gone.
        var existing = _leases.Get(leaseId);
        if (existing is not null &&
            _state.GetPane(new PaneId(existing.PaneId)) is null)
        {
            _ = _leases.DropPane(existing.PaneId);
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Lease not found: {leaseId}");
        }

        var result = _leases.Renew(leaseId, connection.ConnectionId, ttlMs);
        if (result.Expired)
            throw new ControlPlaneException(ProtocolErrorCodes.LeaseExpired, "Lease expired");
        if (result.NotFound || result.NotHolder || result.Lease is null)
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Lease not found: {leaseId}");

        // Defense: pane may have closed between Get and Renew.
        if (_state.GetPane(new PaneId(result.Lease.PaneId)) is null)
        {
            _ = _leases.DropPane(result.Lease.PaneId);
            throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Lease not found: {leaseId}");
        }

        if (string.Equals(result.Lease.Scope, LeaseScopes.Input, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(p.ClientMode))
        {
            _bindingMutationGate.Wait();
            try
            {
                RejectBusyModeWhileReserved(connection.ConnectionId, p.ClientMode);
                NoteAttachClientMode(connection.ConnectionId, p.ClientMode);
            }
            finally
            {
                _bindingMutationGate.Release();
            }
        }

        return new JsonObject
        {
            ["lease_id"] = result.Lease.LeaseId,
            ["expires_at"] = result.Lease.ExpiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
    }

    private async Task<JsonObject> TerminalObserveAsync(TerminalObserveParams p, IClientConnection? connection, CancellationToken ct)
    {
        if (_subscriptions is null)
            throw new ControlPlaneException(ProtocolErrorCodes.PersistenceUnavailable, "Event hub not configured");
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "terminal.observe requires a live connection");

        var paneId = RequireField(p.PaneId, "pane_id");
        var subId = RequireField(p.SubscriptionId, "subscription_id");

        var side = GetPaneSideEffectLock(paneId);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GetPane(new PaneId(paneId)) is null)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");

            var sub = _subscriptions.Get(subId);
            if (sub is null || sub.IsClosed)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Subscription not found: {subId}");

            if (!string.Equals(sub.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "subscription does not belong to this connection");

            if (p.Replace == true)
                RetargetSubscriptionPane(sub, paneId, AttachmentModes.Observe);

            var created = _attachments.GetOrCreate(
                paneId, connection.ConnectionId, subId, AttachmentModes.Observe, leaseId: null);
            if (created.LimitExceeded)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "max_attachments exceeded");
            if (!created.Ok || created.Attachment is null)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Could not create observe attachment");

            // Reject ghost attaches if pane closed between check and bind.
            if (_state.GetPane(new PaneId(paneId)) is null)
            {
                if (!created.Reused)
                    _attachments.Drop(created.Attachment.AttachmentId);
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            }

            // Hold the pane emit gate before attach so in-flight Output cannot
            // PostLive a byte render ahead of the snapshot. Fence this subscriber
            // first: a Live sub already in the hub channel can still dequeue a
            // pre-attach byte render after the filter opens.
            var paneGate = GetPaneEmitGate(paneId);
            await paneGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _subscriptions.FenceAttachRender(subId, paneId, PeekNextRenderSeq(paneId));
                ForgetPostedFull(paneId);
                if (!_subscriptions.AttachObservePane(subId, paneId, created.Attachment.AttachmentId))
                {
                    if (!created.Reused)
                        _attachments.Drop(created.Attachment.AttachmentId);
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Could not attach observe");
                }

                if (_state.GetPane(new PaneId(paneId)) is null)
                {
                    _subscriptions.DetachPane(paneId);
                    if (!created.Reused)
                        _attachments.Drop(created.Attachment.AttachmentId);
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
                }

                PostAttachSnapshotCore(paneId);
                await PostPopupSnapshotAsync().ConfigureAwait(false);
            }
            finally
            {
                paneGate.Release();
            }

            if (TryGetRuntime(paneId, out var observed) && observed is not null)
                MaybeEmitScrollChanged(observed, originOnly: false);

            return new JsonObject
            {
                ["attachment_id"] = created.Attachment.AttachmentId,
                ["pane_id"] = paneId,
                ["mode"] = AttachmentModes.Observe,
                ["output_event"] = ProtocolEventTypes.TerminalOutput,
                ["render_event"] = ProtocolEventTypes.TerminalRender,
            };
        }
        finally
        {
            side.Release();
        }
    }

    private JsonObject TerminalVisibleSet(TerminalVisibleSetParams p, IClientConnection? connection)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "terminal.visible_set requires a live connection");

        var subId = RequireField(p.SubscriptionId, "subscription_id");
        if (p.PaneIds is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "pane_ids is required");

        if (_subscriptions is not null)
        {
            var sub = _subscriptions.Get(subId);
            if (sub is null || sub.IsClosed)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Subscription not found: {subId}");
            if (!string.Equals(sub.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "subscription does not belong to this connection");
        }

        Result<VisibleSetSnapshot, VisibleSetError> published;
        if (p.Failed == true)
        {
            published = _visibleSets.MarkFailed(connection.ConnectionId);
        }
        else
        {
            published = _visibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = connection.ConnectionId,
                PaneIds = p.PaneIds,
                OverlayPaneId = p.OverlayPaneId,
            });
        }

        if (!published.IsOk)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                published.Error.Message);
        }

        ApplyVisibleSetSnapshot(published.Value);
        var overlay = string.IsNullOrWhiteSpace(p.OverlayPaneId) ? null : p.OverlayPaneId.Trim();
        return new JsonObject
        {
            ["ok"] = true,
            ["pane_ids"] = new JsonArray([.. p.PaneIds.Select(id => (JsonNode)id)]),
            ["overlay_pane_id"] = overlay,
            ["revealed_pane_ids"] = new JsonArray(
                [.. published.Value.RevealedPaneIds.Select(id => (JsonNode)id)]),
            ["live_client_count"] = published.Value.LiveClientCount,
            ["fail_open"] = published.Value.FailOpen,
        };
    }

    private void ApplyVisibleSetSnapshot(VisibleSetSnapshot snapshot)
    {
        foreach (var paneId in snapshot.RevealedPaneIds)
            ForgetPostedFull(paneId);
    }

    private async Task<JsonObject> TerminalControlAsync(TerminalControlParams p, IClientConnection? connection, CancellationToken ct)
    {
        if (_subscriptions is null)
            throw new ControlPlaneException(ProtocolErrorCodes.PersistenceUnavailable, "Event hub not configured");
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "terminal.control requires a live connection");

        var paneId = RequireField(p.PaneId, "pane_id");
        var leaseId = RequireField(p.LeaseId, "lease_id");
        var subId = RequireField(p.SubscriptionId, "subscription_id");

        var side = GetPaneSideEffectLock(paneId);
        await side.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GetPane(new PaneId(paneId)) is null)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");

            var sub = _subscriptions.Get(subId);
            if (sub is null || sub.IsClosed)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Subscription not found: {subId}");

            if (!string.Equals(sub.ConnectionId, connection.ConnectionId, StringComparison.Ordinal))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "subscription does not belong to this connection");

            AuthorizeInput(paneId, leaseId, connection.ConnectionId);

            if (p.Replace == true)
                RetargetSubscriptionPane(sub, paneId, AttachmentModes.Control);

            var created = _attachments.GetOrCreate(
                paneId, connection.ConnectionId, subId, AttachmentModes.Control, leaseId);
            if (created.LimitExceeded)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "max_attachments exceeded");
            if (!created.Ok || created.Attachment is null)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Could not create control attachment");

            if (_state.GetPane(new PaneId(paneId)) is null)
            {
                if (!created.Reused)
                    _attachments.Drop(created.Attachment.AttachmentId);
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
            }

            var paneGate = GetPaneEmitGate(paneId);
            await paneGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _subscriptions.FenceAttachedPaneRender(paneId, PeekNextRenderSeq(paneId));
                ForgetPostedFull(paneId);
                if (!_subscriptions.AttachControlPane(subId, paneId, created.Attachment.AttachmentId))
                {
                    if (!created.Reused)
                        _attachments.Drop(created.Attachment.AttachmentId);
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Could not attach control");
                }

                if (_state.GetPane(new PaneId(paneId)) is null)
                {
                    _subscriptions.DetachPane(paneId);
                    if (!created.Reused)
                        _attachments.Drop(created.Attachment.AttachmentId);
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
                }

                PostAttachSnapshotCore(paneId);
                await PostPopupSnapshotAsync().ConfigureAwait(false);
            }
            finally
            {
                paneGate.Release();
            }

            if (TryGetRuntime(paneId, out var controlled) && controlled is not null)
                MaybeEmitScrollChanged(controlled, originOnly: false);

            return new JsonObject
            {
                ["attachment_id"] = created.Attachment.AttachmentId,
                ["pane_id"] = paneId,
                ["mode"] = AttachmentModes.Control,
                ["lease_id"] = leaseId,
                ["output_event"] = ProtocolEventTypes.TerminalOutput,
                ["render_event"] = ProtocolEventTypes.TerminalRender,
            };
        }
        finally
        {
            side.Release();
        }
    }

    /// <summary>
    /// Keep one observe and one control attachment per subscription. A focus
    /// retarget must drop the previous pane or max_attachments fills after
    /// eight unique panes (observe+control).
    /// </summary>
    private void RetargetSubscriptionPane(EventSubscription sub, string paneId, string mode)
    {
        foreach (var att in sub.ListAttachments())
        {
            if (!string.Equals(att.Mode, mode, StringComparison.Ordinal))
                continue;
            if (string.Equals(att.PaneId, paneId, StringComparison.Ordinal))
                continue;
            _attachments.Drop(att.AttachmentId);
            sub.DetachAttachment(
                att.AttachmentId,
                dropObserveFilter: string.Equals(mode, AttachmentModes.Observe, StringComparison.Ordinal));
        }
    }

    private async Task EmitLeaseChangedAsync(
        LeaseState lease,
        CancellationToken ct) =>
        await EmitLeaseChangedAsync(
            lease,
            actorId: lease.HolderId,
            oldHolderId: null,
            newHolderId: lease.State == LeaseStates.Granted ? lease.HolderId : null,
            reason: lease.Reason,
            policyDecision: lease.State,
            ct).ConfigureAwait(false);

    private async Task EmitLeaseChangedAsync(
        LeaseState lease,
        string? actorId,
        string? oldHolderId,
        string? newHolderId,
        string? reason,
        string policyDecision,
        CancellationToken ct)
    {
        if (_journal is null)
            return;

        var reasonHash = HashReason(reason);
        var payload = RuntimeEventPayloadJson.WriteLeaseChanged(
            lease.LeaseId,
            lease.PaneId,
            lease.Scope,
            lease.State,
            lease.HolderId,
            actorId ?? "",
            oldHolderId,
            newHolderId,
            reasonHash,
            policyDecision);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.LeaseChanged, payload);

        await PublishReliableAsync(
            EventClass.Control,
            ProtocolEventTypes.LeaseChanged,
            payload,
            ct).ConfigureAwait(false);
    }

    private static string HashReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
            return "";
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(reason));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static JsonObject BuildJournalHealthObject(JournalHealth health)
    {
        var journal = new JsonObject
        {
            ["next_seq"] = health.NextSeq,
            ["replay_complete"] = health.ReplayComplete,
            ["bytes"] = health.Bytes,
            ["floor_seq"] = health.FloorSeq,
            ["refusal_count"] = health.RefusalCount,
        };
        journal["last_refusal_reason"] = health.LastRefusalReason;
        // Design §10.1: incomplete recovery exposes cursor/error in runtime.health.
        if (!health.ReplayComplete)
            journal["replay_error"] = health.ReplayError;
        else if (health.ReplayError is not null)
            journal["replay_error"] = health.ReplayError;
        else
            journal["replay_error"] = null;
        return journal;
    }

    /// <summary>
    /// Parse <c>types</c> filter. Absent → empty set (all classes).
    /// Class tokens: control|lifecycle|output|render.
    /// Named tokens are additive. Unknown tokens fail closed.
    /// </summary>
    private static SubscribeTypeFilter ParseSubscribeFilter(IReadOnlyList<string>? types)
    {
        var classes = new HashSet<EventClass>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        if (types is null)
            return new SubscribeTypeFilter(classes, named, HasClassTokens: false);

        if (types.Count == 0)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "events.subscribe types must not be an empty array");
        }

        var hasClassTokens = false;
        foreach (var token in types)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    "events.subscribe types entries must be strings");
            }

            if (EventClassMap.TryParse(token, out var c))
            {
                classes.Add(c);
                hasClassTokens = true;
                continue;
            }

            if (ProtocolEventTypes.IsNamedSubscribeToken(token))
            {
                named.Add(token);
                classes.Add(EventClassMap.FromWireType(token));
                continue;
            }

            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                $"Unknown event class in types filter: {token}. " +
                "Use control, lifecycle, output, or render.");
        }

        return new SubscribeTypeFilter(classes, named, hasClassTokens);
    }

    private readonly record struct SubscribeTypeFilter(
        HashSet<EventClass> Classes,
        HashSet<string> NamedTypes,
        bool HasClassTokens);

    private async Task EmitLifecycleAsync(
        string type,
        string paneId,
        string state,
        int occupantGeneration,
        CancellationToken ct)
    {
        if (_journal is null)
            return;

        var payload = RuntimeEventPayloadJson.WritePaneLifecycle(paneId, state, occupantGeneration);
        // Redact before durable write; same payload for live fanout (design §12.4).
        payload = _redactor.RedactJsonPayload(type, payload);

        await PublishReliableAsync(
            EventClass.Lifecycle,
            type,
            payload,
            ct,
            notifyPlugin: true).ConfigureAwait(false);
    }

    private async Task EmitOutputAsync(
        IPaneRuntime runtime,
        ReadOnlyMemory<byte> data,
        long feedGeneration,
        bool requestPaint)
    {
        var paneId = runtime.Id.Value;
        try
        {
            if (_journal is null && _subscriptions is null)
                return;

            var delay = DelayOutputEmitAsync;
            if (delay is not null)
                await delay(runtime).ConfigureAwait(false);

            var paneGate = GetPaneEmitGate(paneId);
            await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // Occupant identity, not FeedGeneration: a new runtime restarts at 0/1
                // while an in-flight emit still carries the old high generation.
                bool stillOccupant;
                lock (_gate)
                    stillOccupant = IsRegisteredOccupantUnlocked(runtime);
                if (!stillOccupant)
                    return;

                // Keyed redact under the pane emit gate so carry stays ordered with flush.
                // Grid snapshot posting is independent of journal redact emptiness: the VT
                // grid already advanced on Feed, so secret-carry holds must not skip paint.
                var redacted = _redactor.RedactTerminalBytes(paneId, data);

                // Render first: live-only, never takes journal _gate or _emitGate, so
                // another pane does not wait on a Reliable DurablySync. An immediate
                // snapshot paint runs under this pane gate, then Output journals.
                // Fail closed on append. Render still posts when the journal
                // is null or the Output append fails. Drop Render when this Feed is
                // already in a posted VT snapshot.
                var dropRender = feedGeneration > 0
                    && _paneSnapshotEpoch.TryGetValue(paneId, out var epoch)
                    && feedGeneration <= epoch;
                if (_subscriptions is not null && !dropRender)
                {
                    // Snapshot-capable runtimes post the server grid. Byte Remap
                    // is only FlushCoalescedPaneAsync / attach pack-or-post
                    // failure, not a stale-feed second writer.
                    if (runtime is IPaneVtSnapshot)
                    {
                        // visibility before render. A pane with no live
                        // observer schedules no coalescer flush: the attach
                        // path captures a fresh Full frame on observe.
                        if (requestPaint && ShouldCaptureLive(paneId))
                        {
                            // result.request_render && render_dirty.request_pty.
                            // request_render = !synchronized_output.
                            // a pending closer when CSI ?2026h advances the
                            // live FeedGeneration. Use the captured pair.
                            // Do not query live 2026. Latest-wins recaptures
                            // the current VT under the pane emit gate.
                            var due = _renderCoalescer.Request(paneId, feedGeneration);
                            if (due is { } decision)
                                FlushCoalescedPaneUnderGate(
                                    decision.PaneId,
                                    decision.FeedGeneration,
                                    force: false);
                        }
                        else if (runtime is IPaneVtSnapshot parked)
                        {
                            MaybeEmitParkedScrollMetrics(parked);
                        }
                    }
                    else if (!redacted.IsEmpty)
                        WriteTerminalRenderCore(paneId, redacted);
                }

                if (!redacted.IsEmpty)
                    WriteTerminalOutputCore(paneId, redacted);
            }
            finally
            {
                paneGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Output event emit failed for {PaneId}", paneId);
        }
        finally
        {
            EndOutputEmit(paneId);
        }
    }

    private async Task FlushThenEmitExitAsync(PaneState pane)
    {
        try
        {
            await FlushTerminalOutputAsync(pane.Id.Value).ConfigureAwait(false);
            await EmitLifecycleAsync(
                ProtocolEventTypes.PaneLifecycle,
                pane.Id.Value,
                PaneLifecycle.Exited,
                pane.OccupantGeneration,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exit flush/lifecycle emit failed for {PaneId}", pane.Id);
        }
    }

    private async Task FlushTerminalOutputAsync(string paneId)
    {
        await WaitOutputEmitsIdleAsync(paneId).ConfigureAwait(false);

        if (_journal is null && _subscriptions is null)
        {
            _ = _redactor.FlushTerminalStream(paneId);
            return;
        }

        try
        {
            var paneGate = GetPaneEmitGate(paneId);
            await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var leftover = _redactor.FlushTerminalStream(paneId);
                // An empty carry still reaches the drain below: earlier emits may
                // have queued output that close must deliver first.
                if (!leftover.IsEmpty)
                {
                    // Snapshot-capable panes paint from the grid. Never Remap leftover
                    // ANSI.
                    if (_subscriptions is not null)
                    {
                        if (TryGetRuntime(paneId, out var flushRuntime)
                            && IsSnapshotCapableRuntime(flushRuntime))
                        {
                            _renderCoalescer.Cancel(paneId);
                            if (!PostLiveSnapshotCore(paneId))
                                RetrySnapshotPaint(paneId);
                        }
                        else
                            WriteTerminalRenderCore(paneId, leftover);
                    }

                    WriteTerminalOutputCore(paneId, leftover);
                }
            }
            finally
            {
                paneGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminal carry flush failed for {PaneId}", paneId);
        }

        // Deliver the flush tail before pane.close drops observe. A queued
        // terminal.output is otherwise filtered out at delivery time.
        if (_subscriptions is not null)
            await _subscriptions.WhenQueuedDeliveredAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Live-only terminal.output. Caller must hold the pane emit gate.
    /// HYJR does not store these bytes. Keeps the size cap and the redaction
    /// the caller already applied.
    /// </summary>
    private void WriteTerminalOutputCore(string paneId, ReadOnlyMemory<byte> redacted)
    {
        if (redacted.IsEmpty || _subscriptions is null)
            return;
        if (!_subscriptions.MayObserveOutput(paneId))
            return;

        var span = redacted.Span;
        var maxRaw = Math.Min(MaxTerminalOutputRawBytes, CachedMaxRawBytesForTerminalJson(paneId));
        if (span.Length > maxRaw)
            span = span[..maxRaw];

        // RedactTerminalBytes already applied secret rules to raw content. Base64 is opaque
        // wire encoding — do not run full-text RedactJsonPayload over it (false positives
        // from password=/AKIA/sk- patterns inside base64 corrupt live payloads).
        using var buffer = new PooledUtf8Buffer(span.Length + 128);
        if (!TerminalOutputPayloadWriter.TryWriteTerminalOutput(buffer, paneId, span))
            return;

        var seq = _paneOutputSeq.AddOrUpdate(paneId, 1L, static (_, prev) => prev + 1);
        var live = buffer.WrittenSpan.ToArray();
        _subscriptions.PostLive(new RuntimeEventRecord
        {
            Seq = seq,
            Class = EventClass.Output,
            Reliability = EventReliability.Output,
            Type = ProtocolEventTypes.TerminalOutput,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = Encoding.UTF8.GetString(live),
            PayloadUtf8 = live,
            PaneKey = paneId,
        });
    }

    /// <summary>
    /// After a new <see cref="IPaneRuntime"/> is committed for an existing pane,
    /// drop the previous attach epoch and repaint current observers.
    /// Caller holds <see cref="_bindingMutationGate"/>. Lock order: binding → emit.
    /// Snapshot failure does not fail the occupant commit.
    /// </summary>
    private async Task RepaintAfterOccupantSwapAsync(string paneId, CancellationToken ct)
    {
        try
        {
            var paneGate = GetPaneEmitGate(paneId);
            await paneGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Drop the old occupant's keyed hold. Do not paint or journal it.
                _ = _redactor.FlushTerminalStream(paneId);
                _ = _paneSnapshotEpoch.TryRemove(paneId, out _);
                ForgetPostedFull(paneId);
                _subscriptions?.FenceAttachedPaneRender(paneId, PeekNextRenderSeq(paneId));
                PostAttachSnapshotCore(paneId);
            }
            finally
            {
                paneGate.Release();
            }
        }
        catch (Exception ex)
        {
            _ = _paneSnapshotEpoch.TryRemove(paneId, out _);
            ForgetPostedFull(paneId);
            _logger.LogDebug(ex, "Attach snapshot after occupant swap failed for {PaneId}", paneId);
        }
    }

    /// <summary>
    /// First-paint attach snapshot. Caller holds the pane side-effect lock
    /// and the per-pane emit gate (acquired before observe/control attach).
    /// Never journals. Capture or pack failure does not fail attach.
    /// Packs and queues every slice before the feed epoch is published.
    /// A pack or post failure leaves the epoch unchanged and writes visible-text
    /// byte fallback so a new pane can paint without a later click.
    /// </summary>
    private void PostAttachSnapshotCore(string paneId)
    {
        if (TryPostVtSnapshotCore(paneId, attachPath: true))
            return;
        FailClosedSnapshotPaintOrByteFallback(paneId, () => WriteAttachByteFallback(paneId));
    }

    /// <summary>
    /// Live output snapshot after <c>IVtEngine.Feed</c> committed the chunk.
    /// Caller holds the per-pane emit gate. Never journals. Same wire shape as attach.
    /// Returns false when capture, pack, or post fails so the caller can write bytes.
    /// </summary>
    private bool PostLiveSnapshotCore(string paneId, CaptureAdmission? admission = null) =>
        TryPostVtSnapshotCore(paneId, attachPath: false, liveAdmission: admission);

    /// <summary>
    // / Hypa render-target check.
    /// <c>src/server/headless/render.rs:272-279</c>
    /// <c>pty_sources_visible_to_any_render_target</c> returns false at once
    /// when no app target and no direct terminal target exist. Hypa render
    /// targets are the live observers of the pane.
    /// </summary>
    private bool HasLiveObserver(string paneId) =>
        _subscriptions is not null && _subscriptions.HasLiveObservers(paneId);

    private CaptureAdmission AdmitLivePaint(string paneId) =>
        _visibleSets.AdmitCapture(paneId, _time.GetUtcNow());

    private bool ShouldCaptureLive(string paneId) =>
        _visibleSets.MayCapture(paneId, _time.GetUtcNow());

    private void CommitLivePaint(string paneId, CaptureAdmission admission)
    {
        if (!admission.ShouldCapture)
            return;
        _visibleSets.CommitCapture(paneId, admission.Generation);
    }

    private void NoteSkippedLivePaint(string paneId, IPaneVtSnapshot? snapshot)
    {
        AttachPathTrace.RecordPhase(
            AttachPathTrace.DomainOutput,
            AttachPathTrace.StageSkipped,
            paneId);
        if (snapshot is not null)
            MaybeEmitParkedScrollMetrics(snapshot);
    }

    /// <summary>
    /// Admit a self-contained ANSI blit. Commit LastAdmittedFrame only after
    /// writer admission. Returns false when no semantic frame is available so
    /// the JSON snapshot path can run.
    /// </summary>
    private bool TryPostVtFrameBlit(
        string paneId,
        IPaneVtSnapshot snapshot,
        bool attachPath,
        bool force,
        CaptureAdmission admission)
    {
        if (_subscriptions is null)
            return false;

        // pty_sources_visible_to_any_render_target and emits
        // render.skipped.hidden_sources without render.
        // src/server/headless/render.rs:272-279 returns false at once when
        // no app target and no direct terminal target exist. Classify first:
        // a live paint with no live observer runs no capture. The skip
        // clears no dirty state and advances no epoch or generation, so the
        // later attach capture still reads the full grid. Attach path stays
        // unchanged and always captures.
        if (!attachPath && !admission.ShouldCapture)
        {
            NoteSkippedLivePaint(paneId, snapshot);
            return true;
        }

        VtFrame? captured = null;
        VtDirtyRowPatch? patch = null;
        long feedGeneration = 0;
        PaneVtPaintKind kind;
        AttachPathTrace.RecordPhase(
            AttachPathTrace.DomainOutput,
            AttachPathTrace.StageCapture,
            paneId,
            bound: AttachPathTrace.BoundStart);
        try
        {
            if (attachPath)
            {
                if (!snapshot.TryCaptureAttachFrame(out captured, out feedGeneration) || captured is null)
                    return false;
                kind = PaneVtPaintKind.Full;
            }
            else if (!snapshot.TryCaptureLivePaintFrame(out captured, out feedGeneration, out kind, out patch))
            {
                return false;
            }
            else if (kind == PaneVtPaintKind.Clean)
            {
                // Origin paint is a Full attach frame. Live Clean is not a posted Full.
                // Ghostty mouse DECSET does not dirty cells; keep the modes-only
                // frame so a token change can pack.
                if (force)
                    return false;
                if (captured is null)
                    return true;
            }
            // live paint is a cheap Full stamp. Do not apply dirty-row
            // patches. A null frame is completed below.
            _ = patch;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VT frame capture failed for {PaneId}", paneId);
            return false;
        }
        finally
        {
            AttachPathTrace.RecordPhase(
                AttachPathTrace.DomainOutput,
                AttachPathTrace.StageCapture,
                paneId,
                0,
                feedGeneration,
                bound: AttachPathTrace.BoundEnd);
        }

        if (_paneSnapshotEpoch.TryGetValue(paneId, out var epoch) && feedGeneration < epoch)
            return false;
        if (!force
            && !attachPath
            && _paneSnapshotEpoch.TryGetValue(paneId, out epoch)
            && feedGeneration <= epoch)
        {
            return true;
        }

        if (kind == PaneVtPaintKind.Clean)
        {
            if (!LiveInputModesNeedPost(paneId, captured!))
                return true;
        }

        // was encoded. Redact the attach frame before the identity and the
        // encode so the baseline commit holds the frame the client
        // received. The live route stays as it is.
        if (attachPath && captured is not null)
            captured = AttachFrameRedactor.RedactCells(paneId, captured, _redactor);

        var occupantGeneration = SnapshotOccupantGeneration(new PaneId(paneId));
        var generation = _paneAttachSnapshotGeneration.AddOrUpdate(
            paneId, 1L, static (_, prev) => prev + 1);
        var viewportOrigin = snapshot.TryGetScrollOrigin(out var origin)
            ? origin
            : captured?.ViewportOrigin ?? patch?.ViewportOrigin ?? 0;
        AttachPathTrace.RecordPhase(
            AttachPathTrace.DomainOutput,
            AttachPathTrace.StageIdentity,
            paneId,
            0,
            feedGeneration,
            generation,
            occupantGeneration);

        // Same injected pack failure as the JSON and popup routes. A failed
        // pack keeps the pending full so the next capture is complete.
        if (Interlocked.Exchange(ref _failNextSnapshotPack, 0) == 1)
        {
            _logger.LogDebug("Cells pack failed for {PaneId}", paneId);
            ForgetPostedFull(paneId);
            return false;
        }

        var observers = _subscriptions.ListLiveObservers(paneId);
        if (observers.Count == 0)
        {
            // Live paints no longer reach this branch: the visibility gates
            // in TryPostVtSnapshotCore and TryPostVtFrameBlit return before
            // any capture. The attach path captured a complete frame above,
            // so its commit stays paired with that capture. A live paint
            // must never commit dirty state or publish an epoch for a
            // generation that no capture read.
            if (!attachPath)
                return true;
            snapshot.CommitPostedPaint();
            _paneSnapshotEpoch[paneId] = feedGeneration;
            CommitLivePaint(paneId, admission);
            return true;
        }

        foreach (var sub in observers)
        {
            if (!sub.Sink.UsesWriterLanes)
                return false;
        }

        VtFrame? cachedFull = null;
        VtFrame? CompleteFrame()
        {
            if (cachedFull is not null)
                return cachedFull;
            if (captured is not null)
            {
                cachedFull = captured with
                {
                    OccupantGeneration = occupantGeneration,
                    Generation = generation,
                    ViewportOrigin = viewportOrigin,
                };
                return cachedFull;
            }

            if (snapshot.TryCaptureAttachFrame(out var full, out _) && full is not null)
            {
                cachedFull = full with
                {
                    OccupantGeneration = occupantGeneration,
                    Generation = generation,
                    ViewportOrigin = viewportOrigin,
                };
            }

            return cachedFull;
        }

        string? sharedIdentity = null;
        VtFrame? sharedFrame = null;
        if (kind != PaneVtPaintKind.Clean)
        {
            var complete = CompleteFrame();
            if (complete is null)
                return false;
            sharedFrame = complete;
            sharedIdentity = VtFrameIdentity.Token(complete);
        }

        var witnessId = observers[0].SubscriptionId;
        var witnessIdBytes = Encoding.UTF8.GetByteCount(witnessId);
        var witnessCap = observers[0].Sink.MaxLineBytes;
        for (var i = 1; i < observers.Count; i++)
        {
            var witnessSub = observers[i];
            var idBytes = Encoding.UTF8.GetByteCount(witnessSub.SubscriptionId);
            if (idBytes > witnessIdBytes)
            {
                witnessId = witnessSub.SubscriptionId;
                witnessIdBytes = idBytes;
            }

            var cap = witnessSub.Sink.MaxLineBytes;
            if (cap > 0 && (witnessCap <= 0 || cap < witnessCap))
                witnessCap = cap;
        }

        if (witnessCap < 256)
            witnessCap = AttachCellsLinePacker.MaxNdjsonLineBytes;

        // _paneAttachSnapshotGeneration only increases per pane. A committed
        // frame carries the generation that the counter produced. Two
        // different frames for one pane therefore cannot hold the same
        // generation. ForceFull is a separate memo key so a pending
        // reanchor does not reuse a delta buffer.
        var encodings = new Dictionary<(bool ForceFull, long Generation), LiveCellsEncoding?>();
        var sliceCache = new Dictionary<(bool ForceFull, long Generation), IReadOnlyList<AttachCellsPackedSlice>?>();
        var anyAdmitted = false;
        var anySkippedEqual = false;
        foreach (var sub in observers)
        {
            if (!TryAdmitAttachSurfaceRender(
                    sub.ConnectionId,
                    (ulong)generation,
                    (ulong)feedGeneration,
                    out var emitBinding))
            {
                continue;
            }

            var baseline = sub.LastAdmittedFrame(paneId);
            var admittedGeneration = sub.AdmittedGeneration(paneId);
            // !repaint_pending && is_current. Else encode with prev = None
            // (src/protocol/render_ansi.rs:88-92). Origin force captures an
            // attach frame and must not skip an equal admitted grid.
            var reanchorGeneration = sub.ReanchorGeneration(paneId);
            if (kind == PaneVtPaintKind.Clean)
                BeforeCleanReanchorDecisionForTests?.Invoke(paneId);
            var latestGeneration = sub.ReanchorGeneration(paneId);
            if (latestGeneration != reanchorGeneration)
                reanchorGeneration = latestGeneration;
            var reanchorPending = reanchorGeneration != 0;
            var forceFull = (attachPath && force) || reanchorPending;
            var encodeBaseline = forceFull ? null : baseline;
            VtFrame frame;
            if (kind == PaneVtPaintKind.Clean)
            {
                if (captured is null
                    || (!reanchorPending
                        && (baseline is null
                            || !InputModesChanged(captured.Modes, baseline.Modes))))
                {
                    anySkippedEqual = true;
                    continue;
                }

                if (reanchorPending)
                {
                    // A pending re-anchor must carry the current VT grid.
                    // The last admitted frame can omit cells from a dropped
                    // admission. Use the complete current capture.
                    var complete = CompleteFrame();
                    if (complete is null)
                    {
                        anySkippedEqual = true;
                        continue;
                    }

                    frame = complete;
                }
                else
                {
                    // Modes-only live snapshot. Stamp input modes onto the
                    // admitted cells so the body is a delta with empty rows.
                    frame = baseline! with
                    {
                        Modes = captured.Modes,
                        OccupantGeneration = occupantGeneration,
                        Generation = generation,
                    };
                }
            }
            else
            {
                if (sharedFrame is null || sharedIdentity is null)
                {
                    anySkippedEqual = true;
                    continue;
                }

                frame = sharedFrame;
            }

            var key = (forceFull, forceFull ? 0 : admittedGeneration);
            if (!encodings.TryGetValue(key, out var packedEncoding))
            {
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageEncode,
                    paneId,
                    0,
                    feedGeneration,
                    generation,
                    occupantGeneration,
                    bound: AttachPathTrace.BoundStart);
                packedEncoding = _liveCellsEncoder.Encode(
                    frame,
                    encodeBaseline,
                    paneId,
                    generation,
                    encodeBaseline?.Generation ?? 0,
                    occupantGeneration);
                AttachPathTrace.RecordPhase(
                    AttachPathTrace.DomainOutput,
                    AttachPathTrace.StageEncode,
                    paneId,
                    packedEncoding?.Payload.ChangedCells ?? 0,
                    feedGeneration,
                    generation,
                    occupantGeneration,
                    bound: AttachPathTrace.BoundEnd);
                encodings[key] = packedEncoding;
                sliceCache[key] = packedEncoding is null
                    ? null
                    : SharedLiveCellsSlices(packedEncoding.Value, witnessId, witnessCap);
            }

            if (packedEncoding is null)
            {
                anySkippedEqual = true;
                continue;
            }

            var encoding = packedEncoding.Value;
            var packedSlices = sliceCache[key];
            var wireBytes = encoding.Payload.WireBytes;
            AttachPathTrace.RecordPhase(
                AttachPathTrace.DomainOutput,
                AttachPathTrace.StageWriterAdmit,
                paneId,
                wireBytes,
                feedGeneration,
                generation,
                occupantGeneration,
                bound: AttachPathTrace.BoundStart);
            BeforeLiveCellsAdmitForTests?.Invoke(paneId);
            var (admitted, dropped) = AdmitLiveCells(
                sub,
                paneId,
                encoding,
                packedSlices,
                sharedIdentity ?? VtFrameIdentity.Token(frame),
                emitBinding);
            AttachPathTrace.RecordPhase(
                AttachPathTrace.DomainOutput,
                AttachPathTrace.StageWriterAdmit,
                paneId,
                wireBytes,
                feedGeneration,
                generation,
                occupantGeneration,
                bound: AttachPathTrace.BoundEnd);
            if (dropped)
            {
                sub.MarkReanchorPending(paneId);
                anySkippedEqual = true;
                continue;
            }

            if (admitted.IsOk)
            {
                sub.CommitLastAdmittedFrame(paneId, frame, reanchorGeneration);
                anyAdmitted = true;
            }
            else if (admitted.IsFull)
            {
                sub.DeferLatestFrame(paneId, frame, emitBinding);
                anyAdmitted = true;
            }
            else if (admitted.IsClosed)
            {
                ForgetPostedFull(paneId);
                return false;
            }
        }

        if (!anyAdmitted && !anySkippedEqual)
            return false;

        snapshot.CommitPostedPaint();
        _paneSnapshotEpoch[paneId] = feedGeneration;
        CommitLivePaint(paneId, admission);
        if (anyAdmitted)
        {
            AttachPathTrace.RecordOutput(
                AttachPathTrace.StageSnapshot,
                AttachPathTrace.RouteSnapshotFull,
                paneId,
                1,
                feedGeneration: feedGeneration,
                snapshotGeneration: generation);
        }

        if (kind != PaneVtPaintKind.Clean)
            MaybeEmitParkedScrollMetrics(snapshot);
        return true;
    }

    /// <summary>
    /// Slice the shared payload one time for the worst observer. Null means
    /// every observer formats one line from the shared buffer. Empty means
    // / drop the frame.
    /// an oversized frame without a panic.
    /// </summary>
    private static IReadOnlyList<AttachCellsPackedSlice>? SharedLiveCellsSlices(
        in LiveCellsEncoding encoding,
        string witnessId,
        int witnessCap)
    {
        if (witnessCap < 256)
            witnessCap = AttachCellsLinePacker.MaxNdjsonLineBytes;

        var witnessLine = AttachCellsLinePacker.FormatLineUtf8(
            encoding.Payload, witnessId, payloadUtf8: encoding.PayloadUtf8);
        if (AttachCellsLinePacker.FitsNdjsonLine(witnessLine, witnessCap))
            return null;

        return AttachCellsLinePacker.SliceToFitPacked(
            encoding.Payload, witnessId, witnessCap, skipCompleteFitCheck: true);
    }

    /// <summary>
    /// Ghostty DECSET for mouse, bracketed paste, and application cursor
    /// does not dirty cells. Post a cells body when an admitted observer
    /// still holds a different input mode, or when that observer's
    /// re-anchor is pending. A change to synchronized output alone does
    /// not post.
    /// </summary>
    private bool LiveInputModesNeedPost(string paneId, VtFrame captured)
    {
        if (_subscriptions is null)
            return false;
        foreach (var sub in _subscriptions.ListLiveObservers(paneId))
        {
            if (sub.ReanchorPending(paneId))
                return true;
            var baseline = sub.LastAdmittedFrame(paneId);
            if (baseline is null)
                continue;
            if (InputModesChanged(captured.Modes, baseline.Modes))
                return true;
        }

        return false;
    }

    private static bool InputModesChanged(VtFrameModes current, VtFrameModes admitted) =>
        !string.Equals(current.Mouse, admitted.Mouse, StringComparison.Ordinal)
        || !string.Equals(current.MouseEncoding, admitted.MouseEncoding, StringComparison.Ordinal)
        || current.BracketedPaste != admitted.BracketedPaste
        || current.ApplicationCursor != admitted.ApplicationCursor;

    /// <summary>
    /// Capture, pack, and post <c>terminal.render</c> kind=snapshot slices.
    /// Returns true when the feed epoch was published after every slice posted.
    /// An older capture feed generation cannot replace a newer epoch.
    /// </summary>
    private bool TryPostVtSnapshotCore(
        string paneId,
        bool attachPath,
        bool force = false,
        CaptureAdmission? liveAdmission = null)
    {
        if (_subscriptions is null)
            return false;

        if (!TryGetRuntime(paneId, out var runtime) || runtime is not IPaneVtSnapshot snapshot)
        {
            if (attachPath)
                _logger.LogDebug("Attach snapshot skipped for {PaneId}: no VT snapshot port", paneId);
            return false;
        }

        // One visibility gate above both capture ports.
        // src/server/headless.rs:575-580 skips before render; the blit path
        // carries its own copy of this check. A skipped live paint runs no
        // capture, clears no dirty state, and advances neither
        // _paneSnapshotEpoch nor _paneAttachSnapshotGeneration. Attach path
        // always captures a complete frame.
        var admission = liveAdmission ?? AdmitLivePaint(paneId);
        if (!attachPath)
        {
            if (!admission.ShouldCapture)
            {
                NoteSkippedLivePaint(paneId, snapshot);
                return true;
            }

            if (admission.RequiresFull)
            {
                ForgetPostedFull(paneId);
                attachPath = true;
                force = true;
            }
        }

        if (TryPostVtFrameBlit(paneId, snapshot, attachPath, force, admission))
            return true;

        string json = "";
        VtAttachSnapshot? typedSnapshot = null;
        long feedGeneration;
        var kind = PaneVtPaintKind.Full;
        IReadOnlyList<int>? dirtyRows = null;
        try
        {
            if (attachPath)
            {
                // typed frame into one message. The mux does not turn
                // that frame into a string first.
                if (!snapshot.TryCaptureAttachSnapshot(out typedSnapshot, out feedGeneration)
                    || typedSnapshot is null)
                    return false;
            }
            else
            {
                if (!snapshot.TryCaptureLivePaintJson(out json, out feedGeneration, out kind, out dirtyRows))
                {
                    ForgetPostedFull(paneId);
                    return false;
                }

                if (kind == PaneVtPaintKind.Clean)
                {
                    if (force)
                        return false;
                    return true;
                }
                if (string.IsNullOrEmpty(json))
                {
                    ForgetPostedFull(paneId);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                attachPath ? "Attach snapshot capture failed for {PaneId}" : "Live snapshot capture failed for {PaneId}",
                paneId);
            ForgetPostedFull(paneId);
            return false;
        }

        // Defensive: under the per-pane emit gate, capture is usually current.
        // Occupant swap / attach re-anchor clear the epoch first. Keep this guard
        // for any caller that posts a capture taken before a newer epoch publish.
        if (_paneSnapshotEpoch.TryGetValue(paneId, out var epoch) && feedGeneration < epoch)
        {
            _logger.LogDebug(
                "Snapshot skipped for {PaneId}: feed {Feed} behind epoch {Epoch}",
                paneId, feedGeneration, epoch);
            return false;
        }

        if (!force
            && !attachPath
            && _paneSnapshotEpoch.TryGetValue(paneId, out epoch)
            && feedGeneration <= epoch)
        {
            return true;
        }

        var occupantGeneration = SnapshotOccupantGeneration(new PaneId(paneId));
        var scrollOrigin = snapshot.TryGetScrollOrigin(out var origin) ? origin : 0;
        string identity = "";
        if (attachPath)
        {
            // Read the grid size from the typed frame. No second parse.
            if (typedSnapshot is null
                || typedSnapshot.Frame.Cols < 1
                || typedSnapshot.Frame.Rows < 1)
            {
                ForgetPostedFull(paneId);
                return false;
            }
        }
        else
        {
            identity = VtFrameIdentity.Compute(paneId, occupantGeneration, scrollOrigin, json);
            if (_subscriptions.AllLiveHaveFrame(paneId, identity)
                && Volatile.Read(ref _failNextSnapshotPack) == 0)
            {
                snapshot.CommitPostedPaint();
                _paneSnapshotEpoch[paneId] = feedGeneration;
                CommitLivePaint(paneId, admission);
                // The rate window was stamped when the coalescer dispatched this
                // paint. Do not advance it again here: a skipped or completed post
                // must not push the next eligible frame a further 16 ms out.
                return true;
            }
        }

        // JSON fallback is a complete Full batch on the ordered lane. Never put
        // row-slice patches in the replaceable latest-state slot.
        kind = PaneVtPaintKind.Full;
        dirtyRows = null;

        IReadOnlyList<byte[]> payloads;
        long generation;
        try
        {
            if (Interlocked.Exchange(ref _failNextSnapshotPack, 0) == 1)
                throw new InvalidOperationException("snapshot pack failed");
            generation = _paneAttachSnapshotGeneration.AddOrUpdate(
                paneId, 1L, static (_, prev) => prev + 1);
            if (attachPath && typedSnapshot is not null)
            {
                var stamped = typedSnapshot.Frame with
                {
                    Generation = generation,
                    OccupantGeneration = occupantGeneration,
                    ViewportOrigin = scrollOrigin,
                };
                // baseline token per client. The typed token is the same
                // value that the cells route uses, so the two routes agree.
                identity = VtFrameIdentity.Token(stamped);
                payloads = AttachSnapshotPacker.Pack(
                    paneId,
                    typedSnapshot with { Frame = stamped },
                    _redactor,
                    UnixSocketServerOptions.DefaultMaxLineBytes,
                    generation,
                    kind == PaneVtPaintKind.Patch ? dirtyRows : null,
                    SnapshotOccupantGeneration(new PaneId(paneId)));
            }
            else
            {
                var parsed = AttachSnapshotPacker.ParseSnapshotJson(paneId, json);
                payloads = AttachSnapshotPacker.Pack(
                    paneId,
                    parsed,
                    _redactor,
                    UnixSocketServerOptions.DefaultMaxLineBytes,
                    generation,
                    kind == PaneVtPaintKind.Patch ? dirtyRows : null,
                    SnapshotOccupantGeneration(new PaneId(paneId)));
            }

            if (payloads.Count == 0)
            {
                ForgetPostedFull(paneId);
                return false;
            }

            if (!AttachSnapshotPacker.FitsClientBatchCap(
                    payloads, Volatile.Read(ref _snapshotClientBatchBytes)))
            {
                ForgetPostedFull(paneId);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                attachPath ? "Attach snapshot pack failed for {PaneId}" : "Live snapshot pack failed for {PaneId}",
                paneId);
            ForgetPostedFull(paneId);
            return false;
        }

        var records = new RuntimeEventRecord[payloads.Count];
        var pinGroup = paneId + ":" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var postedBytes = 0;
        try
        {
            for (var i = 0; i < payloads.Count; i++)
            {
                // Keep the first-observe packer's UTF-8 when redaction does
                // not change the JSON. Trust that envelope payload so the
                // formatter does not copy per observer.
                var partJson = Encoding.UTF8.GetString(payloads[i]);
                var redacted = _redactor.RedactJsonPayload(ProtocolEventTypes.TerminalRender, partJson);
                byte[]? payloadUtf8;
                if (string.Equals(redacted, partJson, StringComparison.Ordinal))
                    payloadUtf8 = payloads[i];
                else
                    payloadUtf8 = EventSubscriptionHub.TryEncodeValidatedPayload(redacted, out var bytes)
                        ? bytes
                        : null;
                postedBytes += payloadUtf8?.Length ?? payloads[i].Length;
                var seq = _paneRenderSeq.AddOrUpdate(paneId, 1L, static (_, prev) => prev + 1);
                records[i] = new RuntimeEventRecord
                {
                    Seq = seq,
                    Class = EventClass.Render,
                    Reliability = EventReliability.Render,
                    Type = ProtocolEventTypes.TerminalRender,
                    OccurredAt = DateTimeOffset.UtcNow,
                    PayloadJson = redacted,
                    PayloadUtf8 = payloadUtf8,
                    PayloadUtf8Trusted = payloadUtf8 is not null,
                    LivePinGroup = pinGroup,
                    Lane = WriterLaneNames.Ordered,
                    FramePaneId = paneId,
                    FrameIdentity = identity,
                };
            }

            // Race backstop for the pre-capture visibility gate in
            // TryPostVtSnapshotCore: a client can detach between that gate
            // and this post.
            var observers = _subscriptions.ListLiveObservers(paneId);
            var writerLanes = observers.Count > 0;
            for (var i = 0; i < observers.Count; i++)
            {
                if (!observers[i].Sink.UsesWriterLanes)
                {
                    writerLanes = false;
                    break;
                }
            }

            if (writerLanes)
            {
                if (!TryAdmitJsonFullOnWriterLanes(
                        observers,
                        records,
                        paneId,
                        identity,
                        feedGeneration,
                        snapshot,
                        admission,
                        attachPath,
                        generation))
                {
                    ForgetPostedFull(paneId);
                    return false;
                }
            }
            else if (!TryPostAttachLiveBatch(
                         observers, records, paneId, identity, attachPath, generation, feedGeneration))
            {
                _logger.LogDebug(
                    attachPath
                        ? "Attach snapshot post rejected for {PaneId}"
                        : "Live snapshot post rejected for {PaneId}",
                    paneId);
                ForgetPostedFull(paneId);
                return false;
            }
            else
            {
                snapshot.CommitPostedPaint();
                // The rate window was stamped when the coalescer dispatched this paint.
                // Restamping after capture/pack/admission would measure 16 ms from the
                // end of the post, not its start, and directly worsens input lag.
                // Attach path posts outside the coalescer and must not move it either.
                _paneSnapshotEpoch[paneId] = feedGeneration;
                CommitLivePaint(paneId, admission);
            }

            // JSON snapshot is not a packed VtFrame.
            // commits the frame that was encoded. Leave LastAdmittedFrame null so the next
            // kind=cells paint is a full reanchor instead of a later capture as baseline.
            if (observers.Count > 0)
                MaybeEmitParkedScrollMetrics(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                attachPath ? "Attach snapshot post failed for {PaneId}" : "Live snapshot post failed for {PaneId}",
                paneId);
            ForgetPostedFull(paneId);
            return false;
        }

        AttachPathTrace.RecordOutput(
            AttachPathTrace.StageSnapshot,
            kind == PaneVtPaintKind.Patch
                ? AttachPathTrace.RouteSnapshotPatch
                : AttachPathTrace.RouteSnapshotFull,
            paneId,
            postedBytes,
            feedGeneration: feedGeneration,
            snapshotGeneration: generation);
        return true;
    }

    private bool TryAdmitJsonFullOnWriterLanes(
        IReadOnlyList<EventSubscription> observers,
        IReadOnlyList<RuntimeEventRecord> records,
        string paneId,
        string identity,
        long feedGeneration,
        IPaneVtSnapshot snapshot,
        CaptureAdmission admission,
        bool attachPath,
        long surfaceGeneration)
    {
        var allOk = true;
        foreach (var sub in observers)
        {
            AttachSurfaceEmitBinding? emitBinding = null;
            if (attachPath
                && !TryAdmitAttachSurfaceRender(
                    sub.ConnectionId,
                    (ulong)surfaceGeneration,
                    (ulong)feedGeneration,
                    out emitBinding))
            {
                allOk = false;
                continue;
            }

            var lines = new ReadOnlyMemory<byte>[records.Count];
            for (var i = 0; i < records.Count; i++)
            {
                var lineRecord = emitBinding is null ? records[i] : emitBinding.Stamp(records[i]);
                lines[i] = EventSubscriptionHub.FormatRuntimeEventNdjsonLine(lineRecord, sub.SubscriptionId);
            }

            if (emitBinding is not null && !RecheckAttachSurfaceEmit(emitBinding))
            {
                allOk = false;
                continue;
            }

            var admitted = sub.Sink.EnqueueOrderedRenderBatch(lines, emitBinding);
            if (admitted.IsOk)
            {
                sub.CommitFrameIdentity(paneId, identity);
            }
            else if (admitted.IsFull)
            {
                allOk = false;
                sub.DeferJsonFull(
                    paneId,
                    new DeferredJsonFull(lines, identity, feedGeneration, emitBinding));
            }
            else
            {
                return false;
            }
        }

        if (allOk)
        {
            snapshot.CommitPostedPaint();
            _paneSnapshotEpoch[paneId] = feedGeneration;
            CommitLivePaint(paneId, admission);
        }

        return true;
    }

    private void ForgetPostedFull(string paneId)
    {
        _subscriptions?.ClearPaneFrameIdentities(paneId);
    }

    /// <summary>
    /// Coalesced live paint on the timer path. Waits for the per-pane emit
    /// gate. Immediate Request work runs <see cref="FlushCoalescedPaneUnderGate"/>
    /// under the emit-owned gate so capture cannot wait on journal append.
    /// Pack or post failure remaps the current visible grid, not the last
    /// coalesced chunk.
    /// </summary>
    private async Task FlushCoalescedPaneAsync(
        string paneId,
        long feedGeneration,
        bool force)
    {
        var paneGate = GetPaneEmitGate(paneId);
        await paneGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            FlushCoalescedPaneUnderGate(paneId, feedGeneration, force);
        }
        finally
        {
            paneGate.Release();
        }
    }

    /// <summary>
    /// Caller holds the pane emit gate. Posts the live snapshot or byte
    /// fallback, then returns so the emit path can append Output.
    /// Origin paint uses <paramref name="force"/> so a viewport-only move
    /// is not dropped as <c>feedGeneration &lt;= epoch</c>. Force posts an
    /// attach Full (<c>TryCaptureAttachFrame</c>), not live Clean.
    /// </summary>
    private void FlushCoalescedPaneUnderGate(
        string paneId,
        long feedGeneration,
        bool force)
    {
        if (IsShuttingDown)
            return;

        if (string.Equals(paneId, PopupRuntimeKey, StringComparison.Ordinal))
        {
            if (!PostPopupSnapshotCore())
                RetryPopupSnapshotPaint();
            return;
        }

        bool stillOccupant;
        lock (_gate)
        {
            stillOccupant = TryGetRuntime(paneId, out var runtime)
                && runtime is not null
                && IsRegisteredOccupantUnlocked(runtime);
        }
        if (!stillOccupant)
            return;

        var admission = AdmitLivePaint(paneId);
        if (!admission.ShouldCapture)
        {
            if (TryGetRuntime(paneId, out var parked) && parked is IPaneVtSnapshot snap)
                MaybeEmitParkedScrollMetrics(snap);
            return;
        }

        if (admission.RequiresFull)
            force = true;

        if (!force
            && _paneSnapshotEpoch.TryGetValue(paneId, out var epoch)
            && feedGeneration <= epoch)
            return;

        if (force)
        {
            // snapshot. The next frame stamps the current viewport
            // (src/pane/terminal.rs:2094-2173, 2741-2756). attachPath:true is
            // that frame for every engine, including Ghostty return-to-zero.
            // Live Clean is not a posted Full.
            if (!TryPostVtSnapshotCore(paneId, attachPath: true, force: true, liveAdmission: admission))
            {
                FailClosedSnapshotPaintOrByteFallback(
                    paneId, () => WriteAttachByteFallback(paneId));
            }
            return;
        }

        if (!PostLiveSnapshotCore(paneId, admission))
        {
            FailClosedSnapshotPaintOrByteFallback(
                paneId,
                () => WriteCoalescedLiveByteFallback(paneId));
        }
    }

    /// <summary>
    /// Live-only Control emit for <c>config.reloaded</c>. Never journals.
    /// Uses the render seq domain so journal seq is not torn.
    /// Reliability is Reliable so a full attach control queue does not drop it.
    /// </summary>
    private void PostLiveConfigReloaded(ConfigReloadResult result)
    {
        if (_subscriptions is null)
            return;

        var payload = JsonSerializer.Serialize(result, ProtocolJsonContext.Default.ConfigReloadResult);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.ConfigReloaded, payload);
        var subscriptions = _subscriptions;
        var occurredAt = _time.GetUtcNow();
        EmitInAllocationOrder(
            _configReloadedEmitGate,
            () => _paneRenderSeq.AddOrUpdate(
                ProtocolEventTypes.ConfigReloaded, 1L, static (_, prev) => prev + 1),
            seq => subscriptions.PostLive(new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Control,
                Reliability = EventReliability.Reliable,
                Type = ProtocolEventTypes.ConfigReloaded,
                OccurredAt = occurredAt,
                PayloadJson = payload,
            }));
    }

    /// <summary>
    /// JSON-RPC notification fault. Live <c>pane.send_keys</c> reports
    /// lease and admission errors as <c>pane.input_rejected</c>.
    /// <c>apply_terminal_attach_input</c> failure and continues. No reverse Input reply.
    /// </summary>
    public Task EmitNotificationFaultAsync(
        string method,
        JsonElement? parameters,
        int code,
        string message,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (!string.Equals(method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal))
            return Task.CompletedTask;
        return EmitPaneInputRejectedAsync(parameters, method, code, message, connection, ct);
    }

    /// <summary>
    /// Live-only Control emit for <c>pane.input_rejected</c>. Never journals.
    /// Uses the render seq domain so journal seq is not torn.
    /// Reliability is Reliable so a full attach control queue does not drop it.
    /// </summary>
    private async Task EmitPaneInputRejectedAsync(
        JsonElement? parameters,
        string method,
        int code,
        string message,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        var paneId = "";
        var leaseId = "";
        if (parameters is { ValueKind: JsonValueKind.Object } obj)
        {
            if (obj.TryGetProperty("pane_id", out var paneEl)
                && paneEl.ValueKind == JsonValueKind.String)
            {
                paneId = paneEl.GetString() ?? "";
            }

            if (obj.TryGetProperty("lease_id", out var leaseEl)
                && leaseEl.ValueKind == JsonValueKind.String)
            {
                leaseId = leaseEl.GetString() ?? "";
            }
        }

        var payload = RuntimeEventPayloadJson.WritePaneInputRejected(
            paneId, method, code, message ?? "", leaseId);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneInputRejected, payload);
        payload = RuntimeEventPayloadJson.FitPaneInputRejectedPayload(
            payload, LineCapFor(connection));
        var subscriptions = _subscriptions;
        var occurredAt = _time.GetUtcNow();
        await EmitInAllocationOrderAsync(
                _paneInputRejectedEmitGate,
                () => _paneRenderSeq.AddOrUpdate(
                    ProtocolEventTypes.PaneInputRejected, 1L, static (_, prev) => prev + 1),
                async (seq, token) =>
                {
                    var rec = new RuntimeEventRecord
                    {
                        Seq = seq,
                        Class = EventClass.Control,
                        Reliability = EventReliability.Reliable,
                        Type = ProtocolEventTypes.PaneInputRejected,
                        OccurredAt = occurredAt,
                        PayloadJson = payload,
                    };
                    if (connection is not null && !string.IsNullOrEmpty(connection.ConnectionId))
                    {
                        await subscriptions.FanoutLiveToConnectionAsync(
                                rec, connection.ConnectionId, token)
                            .ConfigureAwait(false);
                        return;
                    }

                    await subscriptions.FanoutLiveAsync(rec, token).ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ghostty / Basic production panes implement <see cref="IPaneVtSnapshot"/>.
    /// Non-snapshot test stubs may still byte-fallback.
    /// </summary>
    private static bool IsSnapshotCapableRuntime(IPaneRuntime? runtime)
    {
        return runtime is IPaneVtSnapshot;
    }

    private static int LineCapFor(IEventPushSink? sink)
    {
        var cap = sink?.MaxLineBytes ?? 0;
        return cap > 0 ? cap : UnixSocketServerOptions.DefaultMaxLineBytes;
    }

    private static bool IsPaneInputRejected(RuntimeEventRecord rec) =>
        string.Equals(rec.Type, ProtocolEventTypes.PaneInputRejected, StringComparison.Ordinal);

    /// <summary>
    /// Failed capture, pack, or post for a snapshot-capable pane. Keep the
    // / epoch unpublished. Re-arm origin Full.
    /// writes only Frame. Do not post raw data.
    /// </summary>
    private void RetrySnapshotPaint(string paneId)
    {
        if (IsShuttingDown || _subscriptions is null)
            return;
        _renderCoalescer.RequestOriginPaint(paneId);
    }

    private void FailClosedSnapshotPaintOrByteFallback(string paneId, Action byteFallback)
    {
        ArgumentNullException.ThrowIfNull(byteFallback);
        if (TryGetRuntime(paneId, out var runtime) && IsSnapshotCapableRuntime(runtime))
        {
            RetrySnapshotPaint(paneId);
            return;
        }

        byteFallback();
    }

    /// <summary>
    /// Byte Remap when attach snapshot capture, pack, or post failed.
    /// Epoch stays unpublished. Uses current visible text, not a later click.
    /// Non-<see cref="IPaneVtSnapshot"/> stubs only.
    /// </summary>
    private void WriteAttachByteFallback(string paneId)
    {
        if (!TryGetRuntime(paneId, out var runtime) || runtime is null)
            return;

        var text = runtime.ReadVisibleText();
        if (string.IsNullOrEmpty(text))
            return;

        var original = Encoding.UTF8.GetBytes(text);
        var redacted = _redactor.RedactTerminalBytes(paneId, original);
        WriteLiveByteFallback(paneId, original, redacted);
    }

    /// <summary>
    /// Coalesced live pack/post failure for non-snapshot runtimes only.
    /// Snapshot panes retry origin Full. Prefer the current visible grid.
    /// </summary>
    private void WriteCoalescedLiveByteFallback(string paneId)
    {
        if (!TryGetRuntime(paneId, out var runtime) || runtime is null)
            return;

        var text = runtime.ReadVisibleText();
        if (string.IsNullOrEmpty(text))
            return;

        var original = Encoding.UTF8.GetBytes(text);
        var isolated = _redactor.RedactClosedTerminalBytes(original);
        if (!isolated.IsEmpty)
        {
            WriteTerminalRenderCore(
                paneId, isolated, AttachPathTrace.RouteByteFallback);
            return;
        }

        WriteLiveByteFallback(paneId, original, isolated);
    }

    /// <summary>
    /// Byte Remap fallback when this emit did not publish a live snapshot.
    /// Keyed redacted bytes are preferred. An empty keyed result peeks the
    /// current hold (PEM / terminator / open token) and never posts original.
    /// Does not flush keyed journal carry.
    /// </summary>
    private void WriteLiveByteFallback(
        string paneId,
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> keyedRedacted)
    {
        if (!keyedRedacted.IsEmpty)
        {
            WriteTerminalRenderCore(
                paneId, keyedRedacted, AttachPathTrace.RouteByteFallback);
            return;
        }

        if (original.IsEmpty)
            return;

        var closed = _redactor.PeekClosedTerminalBytes(paneId, original);
        if (!closed.IsEmpty)
            WriteTerminalRenderCore(
                paneId, closed, AttachPathTrace.RouteByteFallback);
    }

    /// <summary>
    /// Live-only Render emit. Caller must hold the pane emit gate.
    /// Never journals. Does not take <see cref="_emitGate"/> or journal _gate.
    /// Empty after redact does not emit.
    /// </summary>
    private void WriteTerminalRenderCore(
        string paneId,
        ReadOnlyMemory<byte> redacted,
        string? route = null)
    {
        if (redacted.IsEmpty || _subscriptions is null)
            return;

        foreach (var part in TerminalRenderChunker.Split(redacted.Span))
        {
            if (part.Length == 0)
                continue;

            var seq = _paneRenderSeq.AddOrUpdate(paneId, 1L, static (_, prev) => prev + 1);
            var b64 = Convert.ToBase64String(part);
            var payload = RuntimeEventPayloadJson.WriteTerminalRender(
                paneId, b64, part.Length, route);
            var rec = new RuntimeEventRecord
            {
                Seq = seq,
                Class = EventClass.Render,
                Reliability = EventReliability.Render,
                Type = ProtocolEventTypes.TerminalRender,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = payload,
            };
            _subscriptions.PostLive(rec);
            AttachPathTrace.RecordOutput(
                AttachPathTrace.StageSnapshot,
                route ?? AttachPathTrace.RouteByteFallback,
                paneId,
                part.Length);
        }
    }

    private void BeginOutputEmit(string paneId)
    {
        var flight = _outputFlights.GetOrAdd(paneId, static _ => new OutputEmitFlight());
        lock (flight)
        {
            flight.Count++;
            flight.Idle = null;
        }
    }

    private void EndOutputEmit(string paneId)
    {
        if (!_outputFlights.TryGetValue(paneId, out var flight))
            return;

        TaskCompletionSource<bool>? idle = null;
        lock (flight)
        {
            if (flight.Count > 0)
                flight.Count--;
            if (flight.Count == 0)
            {
                idle = flight.Idle;
                flight.Idle = null;
            }
        }

        idle?.TrySetResult(true);
    }

    private async Task WaitOutputEmitsIdleAsync(string paneId)
    {
        if (!_outputFlights.TryGetValue(paneId, out var flight))
            return;

        while (true)
        {
            Task wait;
            lock (flight)
            {
                if (flight.Count == 0)
                    return;
                flight.Idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                wait = flight.Idle.Task;
            }

            await wait.ConfigureAwait(false);
        }
    }

    private sealed class OutputEmitFlight
    {
        public int Count;
        public TaskCompletionSource<bool>? Idle;
    }

    /// <summary>
    /// One pooled PTY chunk on the per-pane emit channel.
    /// Channel capacity is 8 items: 8 x 8 KiB = 64 KiB, the same order as a
    // / kernel PTY buffer.
    /// <c>src/pane.rs:1924-1927</c> blocks on <c>content_write_lock</c>.
    /// </summary>
    private readonly struct PaneOutputChunk
    {
        public PaneOutputChunk(
            IPaneRuntime runtime,
            byte[] buffer,
            int length,
            long feedGeneration,
            bool requestPaint)
        {
            Runtime = runtime;
            Buffer = buffer;
            Length = length;
            FeedGeneration = feedGeneration;
            RequestPaint = requestPaint;
        }

        public IPaneRuntime Runtime { get; }
        public byte[] Buffer { get; }
        public int Length { get; }
        public long FeedGeneration { get; }
        public bool RequestPaint { get; }

        public ReadOnlyMemory<byte> AsMemory() => Buffer.AsMemory(0, Length);

        public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
    }

    private sealed class PaneOutputEmitLoop
    {
        // 8 x 8 KiB reader chunks. Bound by bytes of one typical burst, not by
        // an unbounded item count.
        private const int ChannelCapacity = 8;

        public PaneOutputEmitLoop(string paneId)
        {
            PaneId = paneId;
            var channel = Channel.CreateBounded<PaneOutputChunk>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            Writer = channel.Writer;
            Reader = channel.Reader;
        }

        public string PaneId { get; }
        public ChannelWriter<PaneOutputChunk> Writer { get; }
        public ChannelReader<PaneOutputChunk> Reader { get; }
        public int LoggedBackpressure;
        public Task? DrainTask { get; private set; }

        public void EnsureStarted(ControlPlaneService owner)
        {
            if (DrainTask is not null)
                return;
            lock (this)
            {
                DrainTask ??= Task.Run(() => owner.DrainPaneOutputAsync(this));
            }
        }
    }

    // Cap raw PTY bytes. Journal MaxPayloadBytes must fit base64(raw) plus wrapper.
    private const int MaxTerminalOutputRawBytes = 64 * 1024;

    private int CachedMaxRawBytesForTerminalJson(string paneId) =>
        _terminalOutputRawCapByPaneId.GetOrAdd(paneId, static id => MaxRawBytesForTerminalJson(id));

    /// <summary>
    /// Largest raw length whose constructed terminal.output JSON is ≤ journal MaxPayloadBytes.
    /// </summary>
    private static int MaxRawBytesForTerminalJson(string paneId)
    {
        // Measure the source-generated wrapper with empty data; add base64 + digit growth.
        var empty = RuntimeEventPayloadJson.WriteTerminalOutput(paneId, "", 0);
        var emptyBytes = Encoding.UTF8.GetByteCount(empty);
        var n = MaxTerminalOutputRawBytes;
        while (n > 0)
        {
            var b64 = ((n + 2) / 3) * 4;
            var total = emptyBytes + b64 + (n.ToString().Length - 1);
            if (total <= HyjrPayloadJson.MaxPayloadBytes)
                return n;
            var overflow = total - HyjrPayloadJson.MaxPayloadBytes;
            n -= Math.Max(3, ((overflow + 3) / 4) * 3);
        }

        return 0;
    }

    private void SyncJournalFlagsToAppState()
    {
        if (_journal is null)
            return;
        var health = _journal.GetHealth();
        _state.UpdateSession(s => s with
        {
            NextEventSeq = health.NextSeq,
            ReplayComplete = health.ReplayComplete,
            ReplayError = health.ReplayError,
        });
    }

    /// <summary>
    /// Reject CR/LF and other ASCII control characters in client-controlled identity
    /// strings (design injection rule for NDJSON/durable payloads).
    /// </summary>
    private static void RejectControlCharacters(string value, string fieldName)
    {
        foreach (var ch in value)
        {
            if (ch <= 0x1F || ch == 0x7F)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    $"{fieldName} must not contain ASCII control characters");
            }
        }
    }

    /// <summary>
    /// Minimal presentation when host does not inject <see cref="IAgentPresentationCompressor"/>.
    /// Body only (no Atomic headers); <see cref="AgentRead"/> adds headers separately.
    /// </summary>
    private sealed class FallbackAgentPresentationCompressor : IAgentPresentationCompressor
    {
        public FallbackAgentPresentationCompressor(IIntelligencePipeline _)
        {
        }

        public PresentationResult Compress(PresentationRequest request)
        {
            var raw = request.RawText ?? string.Empty;
            var maxBytes = request.MaxCompressedBytes > 0 ? request.MaxCompressedBytes : 32 * 1024;
            var text = CapUtf8(raw, maxBytes);
            return new PresentationResult
            {
                Text = text,
                Truncated = text.Length < raw.Length,
                CompressorId = "fallback",
                ElapsedMs = 0,
                UsedFallback = true,
            };
        }
    }
}

public sealed class ControlPlaneException : Exception
{
    public int Code { get; }

    public string? ErrorCode { get; }

    public long? FloorSeq { get; }

    public ControlPlaneException(
        int code, string message, string? errorCode = null, long? floorSeq = null) : base(message)
    {
        Code = code;
        ErrorCode = errorCode;
        FloorSeq = floorSeq;
    }
}

/// <summary>Client connect or call budget elapsed. CLI maps this to exit 3.</summary>
public sealed class ControlPlaneClientTimeoutException : Exception
{
    public ControlPlaneClientTimeoutException(string message) : base(message)
    {
    }
}

/// <summary>Mux CLI parse / usage failure. CLI maps this to exit 4.</summary>
public sealed class ControlPlaneCliUsageException : Exception
{
    public ControlPlaneCliUsageException(string message) : base(message)
    {
    }
}
