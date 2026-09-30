using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

public static class CubesConnectActions
{
    public const string Noop = "noop";
    public const string Retargeted = "retargeted";
}

public static class CubesConnectReasons
{
    public const string NestedAttachBlocked = "nested_attach_blocked";
    public const string DestUnreachable = "dest_unreachable";
    public const string DestConnectFailed = "dest_connect_failed";
    public const string DestLeaseFailed = "dest_lease_failed";
    public const string DestLeaseDenied = "dest_lease_denied";
}

public sealed record CubesConnectRequest
{
    public required SidebarCubeItem Destination { get; init; }
    public IAttachCommandPort? SourceControl { get; init; }
    public string? SourceInputLease { get; init; }
    public string? SourceResizeLease { get; init; }
    public IMuxAliveProbe? SourceAlive { get; init; }
    public AttachClientConfig? Config { get; init; }
    public string? HypaEnv { get; init; }
    public PaneProcessList? PaneProcesses { get; init; }
    public Action<IAttachEndpoint>? OnEndpointResolved { get; init; }
    public Func<CancellationToken, ValueTask<bool>>? AuthorizeConnectAsync { get; init; }
    public Action<string, ulong, SshAttachEndpoint>? RetireSshPlacement { get; init; }
    public CubesConnectSshAttempt? SshAttempt { get; init; }
    public Func<ulong, IAttachEndpoint, bool>? IsSshConnectActive { get; init; }
    public string? ClientId { get; init; }
    public AttachGeometry? HostGeometry { get; init; }
    public string? SourceEndpointId { get; init; }
    public ControlPlaneClient? SourceClient { get; init; }
    public bool SourceSurfaceActive { get; init; }
    public AttachEndpointTransportEnvelope? TargetTransportEnvelope { get; init; }
    public AttachEndpointTransportEnvelope? SourceTransportEnvelope { get; init; }
    public ulong ExistingSourceConnectionGeneration { get; init; }
    public string? ExistingSourceBootId { get; init; }
    internal CubesConnectStageClock? StageClock { get; init; }
}

public sealed class CubesConnectSshAttempt
{
    internal ulong Generation { get; set; }
}

public sealed record CubesConnectRetargetOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public required string Action { get; init; }
    public required SidebarCubeKind DestinationKind { get; init; }
    public required string TransportKind { get; init; }
    public required bool SourceMuxAlive { get; init; }
    public required bool NestedAttachBlocked { get; init; }
    public required bool NestedAttachEnabled { get; init; }
    public required bool SpawnsHypaAttach { get; init; }
    public required bool WorkMoved { get; init; }
    public required bool AllowNestedMutated { get; init; }
    public required bool CalledServerStop { get; init; }
    public required int ProcessStartCount { get; init; }
    public required IReadOnlyList<string> ProcessStartCommands { get; init; }
    public required IReadOnlyList<string> PaneProcessCommands { get; init; }
    public JsonElement? DestSnapshot { get; init; }
    public string? DestSession { get; init; }
    public string? DestPaneId { get; init; }
    public string? DestWorkspaceId { get; init; }
    public string? DestTabId { get; init; }
    public string? DestInputLease { get; init; }
    public string? DestResizeLease { get; init; }
    public string? PaintedMuxLabel { get; init; }
    public IAttachEndpoint? DestEndpoint { get; init; }
    public ControlPlaneClient? DestClient { get; init; }
    public ulong SshConnectGeneration { get; init; }
    public string? PreflightBootId { get; init; }
    public string? TargetBootId { get; init; }
    public string? SourceBootId { get; init; }
    public ulong TargetConnectionGeneration { get; init; }
    public ulong SourceConnectionGeneration { get; init; }
    public string? DestSubscribeId { get; init; }
    public string? DestAttachClientId { get; init; }

    public static CubesConnectRetargetOutcome LocalNoop(
        SidebarCubeItem destination,
        bool sourceMuxAlive,
        IReadOnlyList<string> paneProcessCommands,
        int processStartCount,
        IReadOnlyList<string> processStartCommands) =>
        new()
        {
            Ok = true,
            Action = CubesConnectActions.Noop,
            DestinationKind = destination.Kind,
            TransportKind = AttachEndpointKinds.Unix,
            SourceMuxAlive = sourceMuxAlive,
            NestedAttachBlocked = false,
            NestedAttachEnabled = AttachClientConfig.Default.Experimental.AllowNested,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = processStartCount,
            ProcessStartCommands = processStartCommands,
            PaneProcessCommands = paneProcessCommands,
        };

    public static CubesConnectRetargetOutcome NestedBlocked(
        SidebarCubeItem destination,
        string transportKind,
        IReadOnlyList<string> paneProcessCommands,
        int processStartCount,
        IReadOnlyList<string> processStartCommands) =>
        new()
        {
            Ok = false,
            Reason = CubesConnectReasons.NestedAttachBlocked,
            Detail = NestedAttachGuard.Message,
            Action = CubesConnectActions.Noop,
            DestinationKind = destination.Kind,
            TransportKind = transportKind,
            SourceMuxAlive = true,
            NestedAttachBlocked = true,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = processStartCount,
            ProcessStartCommands = processStartCommands,
            PaneProcessCommands = paneProcessCommands,
        };
}

internal sealed class AttachControlSlot
{
    public required ControlPlaneClient Client { get; set; }
}

public interface IMuxAliveProbe
{
    Task<bool> IsAliveAsync(CancellationToken cancellationToken = default);
}

public interface ICubesConnectEndpointResolver
{
    ValueTask<IAttachEndpoint?> ResolveAsync(
        SidebarCubeItem destination,
        CancellationToken cancellationToken = default);
}

internal interface ICubesConnectStageResolver
{
    ValueTask<IAttachEndpoint?> ResolveAsync(
        SidebarCubeItem destination,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken = default);
}

public interface ICubesConnectRetargeter
{
    Task<CubesConnectRetargetOutcome> ConnectAsync(
        CubesConnectRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MapCubesConnectEndpointResolver : ICubesConnectEndpointResolver
{
    private readonly Func<SidebarCubeItem, CancellationToken, ValueTask<IAttachEndpoint?>> _resolve;

    public MapCubesConnectEndpointResolver(
        Func<SidebarCubeItem, CancellationToken, ValueTask<IAttachEndpoint?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    public MapCubesConnectEndpointResolver(IReadOnlyDictionary<string, IAttachEndpoint> endpoints)
        : this((destination, _) =>
        {
            ArgumentNullException.ThrowIfNull(destination);
            return ValueTask.FromResult(
                endpoints.TryGetValue(destination.Id, out var endpoint) ? endpoint : null);
        })
    {
        ArgumentNullException.ThrowIfNull(endpoints);
    }

    public ValueTask<IAttachEndpoint?> ResolveAsync(
        SidebarCubeItem destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return _resolve(destination, cancellationToken);
    }
}

public sealed class UnixMuxAliveProbe : IMuxAliveProbe
{
    public UnixMuxAliveProbe(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        SocketPath = socketPath;
    }

    public string SocketPath { get; }

    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        var ping = await MuxControlPlane.TryPingAsync(SocketPath, cancellationToken)
            .ConfigureAwait(false);
        return ping is not null;
    }
}

/// <summary>
/// Cubes Connect retargets the local attach client. The local client stays
/// the frame owner. Source mux stays up. Leases are taken on dest.
/// Connect does not move Work and does not enable nested attach.
/// </summary>
public sealed class CubesConnectRetargetService : ICubesConnectRetargeter
{
    private readonly ICubesConnectEndpointResolver _resolver;
    private readonly IAttachProcessStarter _processStarter;
    private readonly IPaneRuntimeFactory? _paneFactory;
    public CubesConnectRetargetService(
        ICubesConnectEndpointResolver resolver,
        IAttachProcessStarter? processStarter = null,
        IPaneRuntimeFactory? paneFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _processStarter = processStarter ?? new ObservingProcessStarter();
        _paneFactory = paneFactory;
    }

    public IAttachProcessStarter ProcessStarter => _processStarter;

    public IPaneRuntimeFactory? PaneFactory => _paneFactory;

    public async Task<CubesConnectRetargetOutcome> ConnectAsync(
        CubesConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Destination);

        var config = request.Config ?? AttachClientConfig.Default;
        var panes = request.PaneProcesses ?? new PaneProcessList();
        var startsBefore = _processStarter.StartCount;
        var spawnBefore = panes.SpawnCount;
        var commandCountBefore = panes.Commands.Count;

        if (request.Destination.Kind == SidebarCubeKind.Local)
        {
            var localAlive = await ProbeSourceAsync(request.SourceAlive, cancellationToken)
                .ConfigureAwait(false);
            return CubesConnectRetargetOutcome.LocalNoop(
                request.Destination,
                localAlive,
                Snapshot(panes),
                _processStarter.StartCount,
                SnapshotStarts());
        }

        if (NestedAttachGuard.IsBlocked(config, request.HypaEnv))
        {
            return CubesConnectRetargetOutcome.NestedBlocked(
                request.Destination,
                AttachEndpointKinds.Connectivity,
                Snapshot(panes),
                _processStarter.StartCount,
                SnapshotStarts());
        }

        IAttachEndpoint? endpoint;
        try
        {
            endpoint = _resolver is ICubesConnectStageResolver staged
                ? await staged.ResolveAsync(request.Destination, request.StageClock, cancellationToken)
                    .ConfigureAwait(false)
                : await _resolver.ResolveAsync(request.Destination, cancellationToken)
                    .ConfigureAwait(false);
            if (request.StageClock is { } clock && !clock.HasStamped(CubesConnectStages.Resolve))
                clock.Stamp(CubesConnectStages.Resolve);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (request.StageClock is { } timedOut && !timedOut.HasStamped(CubesConnectStages.Resolve))
                timedOut.Stamp(CubesConnectStages.Resolve);
            return Fail(
                request.Destination,
                CubesConnectReasons.DestConnectFailed,
                "destination connect timed out",
                AttachEndpointKinds.Connectivity,
                panes,
                startsBefore,
                spawnBefore,
                commandCountBefore,
                sourceAlive: await ProbeSourceAsync(request.SourceAlive, CancellationToken.None)
                    .ConfigureAwait(false),
                config: config);
        }

        if (endpoint is null)
        {
            var catalogJoin = HasCatalogJoinTarget(request.Destination);
            return Fail(
                request.Destination,
                catalogJoin
                    ? CubesConnectReasons.DestConnectFailed
                    : CubesConnectReasons.DestUnreachable,
                catalogJoin
                    ? "destination did not accept the join"
                    : "destination has no attach endpoint",
                AttachEndpointKinds.Connectivity,
                panes,
                startsBefore,
                spawnBefore,
                commandCountBefore,
                sourceAlive: await ProbeSourceAsync(request.SourceAlive, cancellationToken)
                    .ConfigureAwait(false),
                config: config);
        }

        request.OnEndpointResolved?.Invoke(endpoint);
        ControlPlaneClient? destClient = null;
        try
        {
            destClient = endpoint.CreateClient();
            await destClient.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var clientId = ResolveConnectClientId(request);
            var geometry = ResolveConnectGeometry(request);
            AttachEndpointConnectPreflightResult? preflight;
            EndpointActivationPreflightError? preflightError;
            try
            {
                (preflight, preflightError) = await AttachEndpointConnectPreflight.RunAsync(
                        destClient,
                        request.SourceSurfaceActive ? request.SourceClient : null,
                        clientId,
                        geometry,
                        request.SourceSurfaceActive && request.SourceClient is not null,
                        cancellationToken,
                        request.ExistingSourceConnectionGeneration,
                        request.ExistingSourceBootId)
                    .ConfigureAwait(false);
            }
            finally
            {
                request.StageClock?.Stamp(CubesConnectStages.Hello);
            }

            if (preflightError is not null || preflight is null)
            {
                var transportKind = endpoint.Kind;
                await destClient.DisposeAsync().ConfigureAwait(false);
                destClient = null;
                await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
                endpoint = null;
                return Fail(
                    request.Destination,
                    AttachEndpointErrorCodes.Incompatible,
                    preflightError?.Message ?? "endpoint preflight failed",
                    transportKind,
                    panes,
                    startsBefore,
                    spawnBefore,
                    commandCountBefore,
                    sourceAlive: preflight?.SourceAvailable ?? false,
                    config: config);
            }

            // An old expiry does not block it.
            request.TargetTransportEnvelope?.ResetForActivation(preflight.TargetConnectionGeneration);
            if (preflight.SourceAvailable)
                request.SourceTransportEnvelope?.StampServerGeneration(preflight.SourceConnectionGeneration);

            var sourceAlive = request.SourceSurfaceActive
                ? preflight.SourceAvailable
                : preflight.SourceAvailable
                    || await ProbeSourceAsync(request.SourceAlive, cancellationToken).ConfigureAwait(false);

            // Hello already proved this link. Herdr does not send ping after hello.
            JsonElement snapshot;
            try
            {
                snapshot = await destClient.CallAsync(
                        ProtocolMethods.SessionSnapshot,
                        ct: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                request.StageClock?.Stamp(CubesConnectStages.Snapshot);
            }

            var paneId = AttachSession.ChooseFocusedPaneId(snapshot) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(paneId))
            {
                await destClient.DisposeAsync().ConfigureAwait(false);
                destClient = null;
                await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
                endpoint = null;
                return Fail(
                    request.Destination,
                    CubesConnectReasons.DestConnectFailed,
                    "destination mux has no live pane",
                    AttachEndpointKinds.Connectivity,
                    panes,
                    startsBefore,
                    spawnBefore,
                    commandCountBefore,
                    sourceAlive: sourceAlive,
                    config: config,
                    destSnapshot: snapshot);
            }

            if (request.AuthorizeConnectAsync is not null)
            {
                var authorized = await request.AuthorizeConnectAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!authorized)
                {
                    var transportKind = endpoint.Kind;
                    await destClient.DisposeAsync().ConfigureAwait(false);
                    destClient = null;
                    await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
                    endpoint = null;
                    return Fail(
                        request.Destination,
                        CubesConnectReasons.DestConnectFailed,
                        "destination placement is no longer authorized",
                        transportKind,
                        panes,
                        startsBefore,
                        spawnBefore,
                        commandCountBefore,
                        sourceAlive: sourceAlive,
                        config: config,
                        destSnapshot: snapshot);
                }
            }

            var sshGeneration = request.SshAttempt?.Generation ?? 0UL;
            if (sshGeneration > 0
                && request.IsSshConnectActive is not null
                && !request.IsSshConnectActive(sshGeneration, endpoint))
            {
                var transportKind = endpoint.Kind;
                await destClient.DisposeAsync().ConfigureAwait(false);
                destClient = null;
                await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
                endpoint = null;
                return Fail(
                    request.Destination,
                    CubesConnectReasons.DestConnectFailed,
                    "destination placement is no longer available",
                    transportKind,
                    panes,
                    startsBefore,
                    spawnBefore,
                    commandCountBefore,
                    sourceAlive: sourceAlive,
                    config: config,
                    destSnapshot: snapshot);
            }

            var workspaceId = AttachSession.ChooseWorkspaceIdForPane(snapshot, paneId)
                ?? ReadString(snapshot, "focused_workspace_id");
            var tabId = AttachSession.ChooseTabIdForPane(snapshot, paneId)
                ?? AttachSession.ChooseFocusedTabId(snapshot);
            var painted = ReadWorkspaceLabel(snapshot, workspaceId) ?? workspaceId;
            var destSession = ReadString(snapshot, "session_id");
            var spawns = ObservedHypaAttachSpawn(
                startsBefore, spawnBefore, commandCountBefore, panes);
            // ClientShell dest observe needs no exclusive dest input or resize.
            AttachSession.AttachSubscribeResult destSubscribe;
            try
            {
                destSubscribe = await AttachSession.SubscribeDestClientAsync(destClient, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                request.StageClock?.Stamp(CubesConnectStages.Subscribe);
            }

            // events.subscribe alone does not create a pane attachment.
            await destClient.CallAsync(
                    ProtocolMethods.TerminalVisibleSet,
                    new JsonObject
                    {
                        ["subscription_id"] = destSubscribe.SubscriptionId,
                        ["pane_ids"] = new JsonArray(paneId),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await destClient.CallAsync(
                        ProtocolMethods.TerminalObserve,
                        new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["subscription_id"] = destSubscribe.SubscriptionId,
                            ["replace"] = true,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                request.StageClock?.Stamp(CubesConnectStages.Observe);
            }

            var handed = destClient;
            destClient = null;
            return new CubesConnectRetargetOutcome
            {
                Ok = true,
                Action = CubesConnectActions.Retargeted,
                DestinationKind = request.Destination.Kind,
                TransportKind = endpoint.Kind,
                SourceMuxAlive = sourceAlive,
                NestedAttachBlocked = false,
                NestedAttachEnabled = config.Experimental.AllowNested,
                SpawnsHypaAttach = spawns,
                WorkMoved = false,
                AllowNestedMutated = false,
                CalledServerStop = false,
                ProcessStartCount = _processStarter.StartCount,
                ProcessStartCommands = SnapshotStarts(),
                PaneProcessCommands = Snapshot(panes),
                DestSnapshot = snapshot,
                DestSession = destSession,
                DestPaneId = paneId,
                DestWorkspaceId = workspaceId,
                DestTabId = tabId,
                DestInputLease = null,
                DestResizeLease = null,
                PaintedMuxLabel = painted,
                DestEndpoint = endpoint,
                DestClient = handed,
                SshConnectGeneration = request.SshAttempt?.Generation ?? 0UL,
                PreflightBootId = preflight.TargetWelcome.BootId,
                TargetBootId = preflight.TargetWelcome.BootId,
                SourceBootId = preflight.SourceWelcome?.BootId,
                TargetConnectionGeneration = preflight.TargetConnectionGeneration,
                SourceConnectionGeneration = preflight.SourceConnectionGeneration,
                DestSubscribeId = destSubscribe.SubscriptionId,
                DestAttachClientId = destSubscribe.AttachClientId,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (destClient is not null)
            {
                try { await destClient.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            var transportKind = endpoint?.Kind ?? AttachEndpointKinds.Connectivity;
            if (destClient is not null)
            {
                try { await destClient.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
            return Fail(
                request.Destination,
                CubesConnectReasons.DestConnectFailed,
                "destination connect timed out",
                transportKind,
                panes,
                startsBefore,
                spawnBefore,
                commandCountBefore,
                sourceAlive: await ProbeSourceAsync(request.SourceAlive, CancellationToken.None)
                    .ConfigureAwait(false),
                config: config);
        }
        catch (Exception ex) when (IsDestinationConnectFailure(ex))
        {
            var transportKind = endpoint?.Kind ?? AttachEndpointKinds.Connectivity;
            if (destClient is not null)
            {
                try { await destClient.DisposeAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            await AbortResolvedEndpointAsync(request, endpoint).ConfigureAwait(false);
            return Fail(
                request.Destination,
                CubesConnectReasons.DestConnectFailed,
                ex.Message,
                transportKind,
                panes,
                startsBefore,
                spawnBefore,
                commandCountBefore,
                sourceAlive: await ProbeSourceAsync(request.SourceAlive, cancellationToken)
                    .ConfigureAwait(false),
                config: config);
        }
    }

    private static bool HasCatalogJoinTarget(SidebarCubeItem destination)
    {
        if (!destination.ConnectEnabled)
            return false;
        if (destination.Kind is not (SidebarCubeKind.Peer or SidebarCubeKind.Cube))
            return false;
        var suffix = destination.ProviderSuffix;
        return !string.IsNullOrWhiteSpace(suffix)
            && (suffix.StartsWith("QUIC", StringComparison.Ordinal)
                || suffix.StartsWith("SSH", StringComparison.Ordinal));
    }

    private static bool IsDestinationConnectFailure(Exception ex) =>
        ex is ControlPlaneException
            or ControlPlaneClientTimeoutException
            or IOException
            or SocketException
            or UnauthorizedAccessException
            or InvalidOperationException;

    private CubesConnectRetargetOutcome Fail(
        SidebarCubeItem destination,
        string reason,
        string detail,
        string transportKind,
        PaneProcessList panes,
        int startsBefore,
        int spawnBefore,
        int commandCountBefore,
        bool sourceAlive,
        AttachClientConfig config,
        JsonElement? destSnapshot = null) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
            Action = CubesConnectActions.Noop,
            DestinationKind = destination.Kind,
            TransportKind = transportKind,
            SourceMuxAlive = sourceAlive,
            NestedAttachBlocked = false,
            NestedAttachEnabled = config.Experimental.AllowNested,
            SpawnsHypaAttach = ObservedHypaAttachSpawn(
                startsBefore, spawnBefore, commandCountBefore, panes),
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = _processStarter.StartCount,
            ProcessStartCommands = SnapshotStarts(),
            PaneProcessCommands = Snapshot(panes),
            DestSnapshot = destSnapshot,
        };

    private static async Task AbortResolvedEndpointAsync(
        CubesConnectRequest request,
        IAttachEndpoint? endpoint)
    {
        if (endpoint is SshAttachEndpoint ssh
            && request.RetireSshPlacement is not null
            && request.SshAttempt is { Generation: > 0 } sshAttempt
            && !string.IsNullOrWhiteSpace(request.Destination.Id))
        {
            request.RetireSshPlacement(request.Destination.Id, sshAttempt.Generation, ssh);
            return;
        }

        await DisposeEndpointQuietAsync(endpoint).ConfigureAwait(false);
    }

    private static async Task DisposeEndpointQuietAsync(IAttachEndpoint? endpoint)
    {
        if (endpoint is not IAsyncDisposable disposable)
            return;
        try { await disposable.DisposeAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private static readonly TimeSpan SourceReleaseTimeout = TimeSpan.FromSeconds(2);

    private static Task _backgroundReleaseTask = Task.CompletedTask;

    internal static void ReleaseSourceLeasesQuiet(
        IAttachCommandPort control,
        string? inputLease,
        string? resizeLease)
    {
        _backgroundReleaseTask = ReleaseSourceLeasesBackground(
            control,
            inputLease,
            resizeLease);
    }

    internal static Task WhenBackgroundReleasesCompleteForTests() =>
        Volatile.Read(ref _backgroundReleaseTask);

    private static Task ReleaseSourceLeasesBackground(
        IAttachCommandPort control,
        string? inputLease,
        string? resizeLease)
    {
        return Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(SourceReleaseTimeout);
            try
            {
                await ReleaseSourceLeasesAsync(
                        control,
                        inputLease,
                        resizeLease,
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        });
    }

    internal static async Task ReleaseSourceLeasesAsync(
        IAttachCommandPort? control,
        string? inputLease,
        string? resizeLease,
        CancellationToken cancellationToken)
    {
        if (control is null)
            return;

        await ReleaseLeaseQuietAsync(control, inputLease, cancellationToken)
            .ConfigureAwait(false);
        await ReleaseLeaseQuietAsync(control, resizeLease, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ReleaseLeaseQuietAsync(
        IAttachCommandPort control,
        string? leaseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        try
        {
            await control.CallAsync(
                    ProtocolMethods.RuntimeLeaseRelease,
                    new JsonObject { ["lease_id"] = leaseId },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ControlPlaneClientTimeoutException)
        {
        }
    }

    private static Task ReleaseLeaseQuietAsync(
        ControlPlaneClient client,
        string? leaseId,
        CancellationToken cancellationToken) =>
        ReleaseLeaseQuietAsync(new ControlPlaneAttachCommandPort(client), leaseId, cancellationToken);

    private static string ResolveConnectClientId(CubesConnectRequest request) =>
        string.IsNullOrWhiteSpace(request.ClientId)
            ? AttachSession.NewAttachClientId()
            : request.ClientId!;

    private static AttachGeometry ResolveConnectGeometry(CubesConnectRequest request) =>
        request.HostGeometry ?? new AttachGeometry
        {
            Columns = 80,
            Rows = 24,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };

    private static async Task<bool> ProbeSourceAsync(
        IMuxAliveProbe? probe,
        CancellationToken cancellationToken)
    {
        if (probe is null)
            return true;
        try
        {
            return await probe.IsAliveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (ControlPlaneException)
        {
            return false;
        }
    }

    private bool ObservedHypaAttachSpawn(
        int startsBefore,
        int spawnBefore,
        int commandCountBefore,
        PaneProcessList panes)
    {
        if (_processStarter.StartCount > startsBefore)
            return true;
        if (panes.SpawnCount > spawnBefore)
            return true;
        for (var i = commandCountBefore; i < panes.Commands.Count; i++)
        {
            if (LooksLikeHypaAttach(panes.Commands[i]))
                return true;
        }

        foreach (var command in _processStarter.StartedCommands)
        {
            if (LooksLikeHypaAttach(command))
                return true;
        }

        return false;
    }

    private static bool LooksLikeHypaAttach(string command) =>
        command.Contains("hypa attach", StringComparison.Ordinal)
        || (command.Contains("hypa", StringComparison.Ordinal)
            && command.Contains("attach", StringComparison.Ordinal));

    private static IReadOnlyList<string> Snapshot(PaneProcessList panes) =>
        [.. panes.Commands];

    private IReadOnlyList<string> SnapshotStarts() =>
        [.. _processStarter.StartedCommands];

    private static string? ReadString(JsonElement snap, string name)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return null;
        if (!snap.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadWorkspaceLabel(JsonElement snap, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)
            || snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("workspaces", out var workspaces)
            || workspaces.ValueKind != JsonValueKind.Array)
        {
            return workspaceId;
        }

        foreach (var workspace in workspaces.EnumerateArray())
        {
            if (workspace.ValueKind != JsonValueKind.Object)
                continue;
            if (ReadString(workspace, "workspace_id") != workspaceId)
                continue;
            return ReadString(workspace, "label") ?? workspaceId;
        }

        return workspaceId;
    }
}
