using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Connectivity.Application;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Mux;

/// <summary>
/// Production Cubes Connect resolver. Peer and Cube rows use the Placement
/// directory. A live uid-private Unix socket is the same-host Cube path.
/// A Peer row joins Connectivity even when that Unix socket is live.
/// </summary>
public sealed class DirectoryCubesConnectEndpointResolver : ICubesConnectEndpointResolver, ICubesConnectStageResolver
{
    private readonly IPlacementDirectory? _directory;
    private readonly Func<IPlacementDirectory> _directoryFactory;
    private readonly Func<string, string> _unixSocketForMux;
    private readonly Func<string, CancellationToken, ValueTask<string?>> _probe;
    private readonly ICubesConnectJoinMaterialSource _joinMaterial;
    private readonly ICubesConnectDirectMaterialSource _directMaterial;
    private readonly Func<CubesConnectJoinMaterial, CancellationToken, Task<IFramedSession?>> _connect;
    private readonly Func<CubesConnectDirectMaterial, CancellationToken, Task<IFramedSession?>> _directConnect;
    private readonly IRemoteMuxPath? _remoteMux;
    private readonly IQuicTransportCapabilityProbe? _quicProbe;

    public DirectoryCubesConnectEndpointResolver()
        : this(directory: null)
    {
    }

    public DirectoryCubesConnectEndpointResolver(
        IPlacementDirectory? directory,
        Func<string, string>? unixSocketForMux = null,
        Func<string, CancellationToken, ValueTask<string?>>? probe = null,
        ICubesConnectJoinMaterialSource? joinMaterial = null,
        ICubesConnectDirectMaterialSource? directMaterial = null,
        Func<CubesConnectJoinMaterial, CancellationToken, Task<IFramedSession?>>? connect = null,
        Func<CubesConnectDirectMaterial, CancellationToken, Task<IFramedSession?>>? directConnect = null,
        IRemoteMuxPath? remoteMux = null,
        IQuicTransportCapabilityProbe? quicProbe = null)
        : this(
            directory,
            OpenDirectory,
            unixSocketForMux,
            probe,
            joinMaterial,
            directMaterial,
            connect,
            directConnect,
            remoteMux,
            quicProbe)
    {
    }

    public DirectoryCubesConnectEndpointResolver(
        IPlacementDirectory? directory,
        Func<IPlacementDirectory> directoryFactory,
        Func<string, string>? unixSocketForMux = null,
        Func<string, CancellationToken, ValueTask<string?>>? probe = null,
        ICubesConnectJoinMaterialSource? joinMaterial = null,
        ICubesConnectDirectMaterialSource? directMaterial = null,
        Func<CubesConnectJoinMaterial, CancellationToken, Task<IFramedSession?>>? connect = null,
        Func<CubesConnectDirectMaterial, CancellationToken, Task<IFramedSession?>>? directConnect = null,
        IRemoteMuxPath? remoteMux = null,
        IQuicTransportCapabilityProbe? quicProbe = null)
    {
        ArgumentNullException.ThrowIfNull(directoryFactory);
        _directory = directory;
        _directoryFactory = directoryFactory;
        _unixSocketForMux = unixSocketForMux ?? DefaultUnixSocket;
        _probe = probe ?? DefaultProbe;
        _joinMaterial = joinMaterial ?? new EnvironmentCubesConnectJoinMaterialSource();
        _directMaterial = directMaterial ?? new PairingCubesConnectDirectMaterialSource();
        _connect = connect ?? CubesConnectOutboundJoin.ConnectAsync;
        _directConnect = directConnect ?? CubesConnectDirectOutboundJoin.ConnectAsync;
        _remoteMux = remoteMux;
        _quicProbe = quicProbe;
    }

    public DirectoryCubesConnectEndpointResolver WithDirectory(IPlacementDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new DirectoryCubesConnectEndpointResolver(
            directory,
            _directoryFactory,
            _unixSocketForMux,
            _probe,
            _joinMaterial,
            _directMaterial,
            _connect,
            _directConnect,
            _remoteMux,
            _quicProbe);
    }

    public ValueTask<IAttachEndpoint?> ResolveAsync(
        SidebarCubeItem destination,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(destination, stages: null, cancellationToken);

    ValueTask<IAttachEndpoint?> ICubesConnectStageResolver.ResolveAsync(
        SidebarCubeItem destination,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(destination, stages, cancellationToken);

    private async ValueTask<IAttachEndpoint?> ResolveCoreAsync(
        SidebarCubeItem destination,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Kind == SidebarCubeKind.Local)
            return null;

        var sshCube = IsSshCube(destination);
        var quicCube = IsQuicCube(destination);
        if (!sshCube
            && !quicCube
            && destination.Reachability is SidebarCubeReachability.Unreachable
                or SidebarCubeReachability.Asleep)
        {
            return null;
        }

        if ((sshCube || quicCube) && !destination.ConnectEnabled)
            return null;

        if (!PlacementId.TryParse(destination.Id, out var placementId))
            return null;

        if (!ProcessLocalOperatorIdentity.TryResolve(out var requester))
            return null;

        ConnectPlacementHold held;
        try
        {
            var directory = _directory ?? _directoryFactory();
            PlacementDirectoryChangeStamp? stamp = null;
            if (directory is IPlacementDirectoryChangeStamp probe)
            {
                try
                {
                    stamp = probe.ReadChangeStamp();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var found = await directory.GetForConnectAsync(requester, placementId, cancellationToken)
                .ConfigureAwait(false);
            if (!found.Ok || found.Value is null)
                return null;
            held = new ConnectPlacementHold(directory, requester, placementId, found.Value, stamp);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var placement = held.Record;
        stages?.Stamp(CubesConnectStages.Resolve);

        if (placement.Kind == PlacementDirectoryKind.Local)
            return null;
        if (placement.Ssh is { Enabled: false } || placement.Quic is { Enabled: false })
            return null;

        stages?.RememberPlacement(placement, held.Stamp, held.Directory);

        if (placement.Ssh is { Enabled: true } sshProfile)
        {
            return await OpenSshEndpointAsync(
                    sshProfile,
                    held,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (placement.Quic is { Enabled: true })
        {
            return await OpenQuicEndpointAsync(
                    held,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (placement.Reachability is PlacementReachability.Unreachable
            or PlacementReachability.Asleep)
        {
            return null;
        }

        var peer = placement.Kind == PlacementDirectoryKind.Peer
            || destination.Kind == SidebarCubeKind.Peer;
        if (!peer)
        {
            try
            {
                var socket = _unixSocketForMux(placement.MuxIdentity.Value);
                if (!string.IsNullOrWhiteSpace(socket))
                {
                    var ping = await _probe(socket, cancellationToken).ConfigureAwait(false);
                    if (ping is not null)
                    {
                        if (!await ConnectPlacementRecheck.StillCurrentAsync(
                                held,
                                stages,
                                cancellationToken)
                            .ConfigureAwait(false))
                        {
                            return null;
                        }

                        return new UnixAttachEndpoint(socket);
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
        }

        return await JoinConnectivityAsync(held, stages, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSshCube(SidebarCubeItem destination) =>
        !string.IsNullOrWhiteSpace(destination.ProviderSuffix)
        && destination.ProviderSuffix.StartsWith("SSH", StringComparison.Ordinal);

    private static bool IsQuicCube(SidebarCubeItem destination) =>
        !string.IsNullOrWhiteSpace(destination.ProviderSuffix)
        && destination.ProviderSuffix.StartsWith("QUIC", StringComparison.Ordinal);

    private async ValueTask<IAttachEndpoint?> JoinConnectivityAsync(
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        var placement = held.Record;
        CubesConnectJoinMaterial? material;
        try
        {
            material = await _joinMaterial.GetAsync(placement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            stages?.Stamp(CubesConnectStages.Material);
            return null;
        }
        catch (InvalidDataException)
        {
            stages?.Stamp(CubesConnectStages.Material);
            return null;
        }

        stages?.Stamp(CubesConnectStages.Material);
        if (material is null)
            return null;

        IFramedSession? session;
        try
        {
            session = await _connect(material, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            StampDialJoin(stages, null);
            return null;
        }
        catch (InvalidOperationException)
        {
            StampDialJoin(stages, null);
            return null;
        }

        StampDialJoin(stages, session);
        if (session is null)
            return null;
        if (!await AcceptHeldAsync(held, stages, session, cancellationToken).ConfigureAwait(false))
            return null;

        return new ConnectivityAttachEndpoint(session);
    }

    private static void StampDialJoin(CubesConnectStageClock? stages, IFramedSession? session)
    {
        if (stages is null)
            return;
        var joined = session is not null || stages.DialSucceeded;
        if (!stages.HasStamped(CubesConnectStages.Dial) && !joined)
        {
            // Failed before a joined session: the connect reached the dial.
            stages.Stamp(CubesConnectStages.Dial);
            return;
        }

        // The regular Connectivity path opens and joins in one call. Without a
        // separate dial stamp, join covers both instead of a fake equal dial.
        if (!stages.HasStamped(CubesConnectStages.Join) && joined)
            stages.Stamp(CubesConnectStages.Join);
    }

    private static IPlacementDirectory OpenDirectory() =>
        new PlacementDirectoryService(
            new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));

    private static string DefaultUnixSocket(string muxIdentity) =>
        UnixSocketServer.ResolveSocketPath(muxIdentity, honorEnvironment: false);

    private static ValueTask<string?> DefaultProbe(string socketPath, CancellationToken cancellationToken) =>
        new(MuxControlPlane.TryPingAsync(socketPath, cancellationToken));

    private async ValueTask<IAttachEndpoint?> OpenQuicEndpointAsync(
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        var placement = held.Record;
        CubesConnectDirectMaterial? material;
        try
        {
            material = await _directMaterial
                .GetAsync(placement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            stages?.Stamp(CubesConnectStages.Material);
            return null;
        }
        catch (InvalidDataException)
        {
            stages?.Stamp(CubesConnectStages.Material);
            return null;
        }

        stages?.Stamp(CubesConnectStages.Material);
        if (material is null)
            return null;
        material = material with { StageClock = stages, QuicProbe = _quicProbe };

        IFramedSession? session;
        try
        {
            session = await _directConnect(material, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            StampDialJoin(stages, null);
            return null;
        }
        catch (InvalidOperationException)
        {
            StampDialJoin(stages, null);
            return null;
        }

        StampDialJoin(stages, session);

        if (session is null)
            return null;
        if (!await AcceptHeldAsync(held, stages, session, cancellationToken).ConfigureAwait(false))
            return null;

        return new ConnectivityAttachEndpoint(session);
    }

    private async ValueTask<IAttachEndpoint?> OpenSshEndpointAsync(
        SshPlacementProfile sshProfile,
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        var remoteMux = _remoteMux ?? new OpenSshRemoteMuxAdapter();
        var peer = sshProfile.ToPeerProfile();
        stages?.NoteTransport("ssh");
        RemoteMuxOutcome opened;
        try
        {
            opened = await remoteMux.OpenAsync(
                    RemoteMuxOpenRequest.FromProfile(peer) with { Interactive = false },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            stages?.Stamp(CubesConnectStages.Dial);
        }

        if (!opened.Ok || opened.Path is null)
            return null;
        if (!await AcceptHeldAsync(held, stages, opened.Path, cancellationToken).ConfigureAwait(false))
            return null;

        return new SshAttachEndpoint(opened.Path);
    }

    private static async ValueTask<bool> AcceptHeldAsync(
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        IAsyncDisposable resource,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await ConnectPlacementRecheck.StillCurrentAsync(held, stages, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        catch
        {
            await DisposeQuietlyAsync(resource).ConfigureAwait(false);
            throw;
        }

        await DisposeQuietlyAsync(resource).ConfigureAwait(false);
        return false;
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }
}
