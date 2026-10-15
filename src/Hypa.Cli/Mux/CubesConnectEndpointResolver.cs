using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Connectivity.Application;
using ConnectivityReasons = Hypa.Connectivity.Domain.ConnectivityReasons;
using DeviceId = Hypa.Connectivity.Domain.DeviceId;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.Cli.Mux;

using EndpointResult = Hypa.Runtime.Domain.Common.Result<Hypa.Cli.Attach.IAttachEndpoint, ConnectError>;
using EndpointDial = Hypa.Runtime.Domain.Common.Result<IFramedSession, ConnectError>;

/// <summary>
/// Production Cubes Connect resolver. Peer and Cube rows use the Placement
/// directory. A live uid-private Unix socket is the same-host Cube path.
/// A Peer row joins Connectivity even when that Unix socket is live.
/// Every failure returns a <see cref="ConnectError"/> with a stable code.
/// </summary>
public sealed class DirectoryCubesConnectEndpointResolver : ICubesConnectEndpointResolver, ICubesConnectStageResolver
{
    private readonly IPlacementDirectory? _directory;
    private readonly Func<IPlacementDirectory> _directoryFactory;
    private readonly Func<string, string> _unixSocketForMux;
    private readonly Func<string, CancellationToken, ValueTask<string?>> _probe;
    private readonly ICubesConnectJoinMaterialSource _joinMaterial;
    private readonly ICubesConnectDirectMaterialSource _directMaterial;
    private readonly Func<CubesConnectJoinMaterial, CancellationToken, Task<EndpointDial>> _connect;
    private readonly Func<CubesConnectDirectMaterial, CancellationToken, Task<EndpointDial>> _directConnect;
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
        Func<CubesConnectJoinMaterial, CancellationToken, Task<EndpointDial>>? connect = null,
        Func<CubesConnectDirectMaterial, CancellationToken, Task<EndpointDial>>? directConnect = null,
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
        Func<CubesConnectJoinMaterial, CancellationToken, Task<EndpointDial>>? connect = null,
        Func<CubesConnectDirectMaterial, CancellationToken, Task<EndpointDial>>? directConnect = null,
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

    public ValueTask<EndpointResult> ResolveAsync(
        SidebarCubeItem destination,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(destination, stages: null, cancellationToken);

    ValueTask<EndpointResult> ICubesConnectStageResolver.ResolveAsync(
        SidebarCubeItem destination,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(destination, stages, cancellationToken);

    private async ValueTask<EndpointResult> ResolveCoreAsync(
        SidebarCubeItem destination,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Kind == SidebarCubeKind.Local)
            return Failed(CubesConnectErrorReasons.LocalDestination);

        var sshCube = IsSshCube(destination);
        var quicCube = IsQuicCube(destination);
        if (!sshCube
            && !quicCube
            && destination.Reachability is SidebarCubeReachability.Unreachable
                or SidebarCubeReachability.Asleep)
        {
            return Failed(
                CubesConnectErrorReasons.Unreachable,
                $"sidebar reachability is {destination.Reachability}");
        }

        if ((sshCube || quicCube) && !destination.ConnectEnabled)
            return Failed(CubesConnectErrorReasons.ConnectDisabled);

        if (!PlacementId.TryParse(destination.Id, out var placementId))
            return Failed(CubesConnectErrorReasons.PlacementIdInvalid, "destination id is not a placement id");

        if (!ProcessLocalOperatorIdentity.TryResolve(out var requester))
            return Failed(CubesConnectErrorReasons.OperatorIdentityUnavailable);

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
            {
                return found.Reason == PlacementReasons.PlacementNotFound
                    ? Failed(CubesConnectErrorReasons.PlacementNotFound, found.Detail)
                    : Failed(
                        CubesConnectErrorReasons.DirectoryUnreadable,
                        ConnectErrors.OneLine(found.Reason ?? found.Detail));
            }

            held = new ConnectPlacementHold(directory, requester, placementId, found.Value, stamp);
        }
        catch (IOException ex)
        {
            return Failed(ConnectErrors.FromException(CubesConnectErrorReasons.DirectoryUnreadable, ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(ConnectErrors.FromException(CubesConnectErrorReasons.DirectoryUnreadable, ex));
        }
        catch (InvalidDataException ex)
        {
            return Failed(ConnectErrors.FromException(CubesConnectErrorReasons.DirectoryUnreadable, ex));
        }

        var placement = held.Record;
        stages?.Stamp(CubesConnectStages.Resolve);

        if (placement.Kind == PlacementDirectoryKind.Local)
            return Failed(CubesConnectErrorReasons.LocalDestination);
        if (placement.Ssh is { Enabled: false } || placement.Quic is { Enabled: false })
            return Failed(CubesConnectErrorReasons.PlacementDisabled, "placement transport is disabled");

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
            return Failed(
                CubesConnectErrorReasons.Unreachable,
                $"placement reachability is {placement.Reachability}");
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
                            return Failed(CubesConnectErrorReasons.PlacementChanged);
                        }

                        return Opened(new UnixAttachEndpoint(socket));
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

    private async ValueTask<EndpointResult> JoinConnectivityAsync(
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
        catch (IOException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.JoinMaterialUnavailable, ex);
            stages?.Stamp(CubesConnectStages.Material, error.Reason);
            return Failed(error);
        }
        catch (InvalidDataException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.JoinMaterialUnavailable, ex);
            stages?.Stamp(CubesConnectStages.Material, error.Reason);
            return Failed(error);
        }

        if (material is null)
        {
            stages?.Stamp(CubesConnectStages.Material, CubesConnectErrorReasons.JoinMaterialUnavailable);
            return Failed(CubesConnectErrorReasons.JoinMaterialUnavailable);
        }

        stages?.Stamp(CubesConnectStages.Material);

        EndpointDial dialed;
        try
        {
            dialed = await _connect(material, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.DialFailed, ex);
            StampDialJoin(stages, null, error);
            return Failed(error);
        }
        catch (InvalidOperationException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.DialFailed, ex);
            StampDialJoin(stages, null, error);
            return Failed(error);
        }

        if (!dialed.IsOk)
        {
            StampDialJoin(stages, null, dialed.Error);
            return Failed(dialed.Error);
        }

        var session = dialed.Value;
        StampDialJoin(stages, session, error: null);
        if (!await AcceptHeldAsync(held, stages, session, cancellationToken).ConfigureAwait(false))
            return Failed(CubesConnectErrorReasons.PlacementChanged);

        return Opened(new ConnectivityAttachEndpoint(session));
    }

    /// <summary>
    /// Stamps dial and join for one connect. A failed connect stamps the
    /// failing stage with the reason. A joined session stamps join as ok.
    /// </summary>
    private static void StampDialJoin(CubesConnectStageClock? stages, IFramedSession? session, ConnectError? error)
    {
        if (stages is null)
            return;

        if (session is null)
        {
            var reason = error?.Reason ?? CubesConnectErrorReasons.DialFailed;
            if (stages.DialSucceeded)
            {
                // The transport opened, then the join failed.
                if (!stages.HasStamped(CubesConnectStages.Join))
                    stages.Stamp(CubesConnectStages.Join, reason);
                return;
            }

            // Failed before a joined session: the connect did not dial.
            if (!stages.HasStamped(CubesConnectStages.Dial))
                stages.Stamp(CubesConnectStages.Dial, reason);
            return;
        }

        // The regular Connectivity path opens and joins in one call. Without a
        // separate dial stamp, join covers both instead of a fake equal dial.
        if (!stages.HasStamped(CubesConnectStages.Join))
            stages.Stamp(CubesConnectStages.Join);
    }

    private static IPlacementDirectory OpenDirectory() =>
        new PlacementDirectoryService(
            new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment()));

    private static string DefaultUnixSocket(string muxIdentity) =>
        UnixSocketServer.ResolveSocketPath(muxIdentity, honorEnvironment: false);

    private static ValueTask<string?> DefaultProbe(string socketPath, CancellationToken cancellationToken) =>
        new(MuxControlPlane.TryPingAsync(socketPath, cancellationToken));

    private async ValueTask<EndpointResult> OpenQuicEndpointAsync(
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        var placement = held.Record;
        IReadOnlyList<CubesConnectDirectAttempt> candidates;
        try
        {
            candidates = await _directMaterial
                .GetCandidatesAsync(placement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.DirectMaterialUnavailable, ex);
            stages?.Stamp(CubesConnectStages.Material, error.Reason);
            return Failed(error);
        }
        catch (InvalidDataException ex)
        {
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.DirectMaterialUnavailable, ex);
            stages?.Stamp(CubesConnectStages.Material, error.Reason);
            return Failed(error);
        }

        if (candidates.Count == 0)
        {
            stages?.Stamp(CubesConnectStages.Material, CubesConnectErrorReasons.DirectMaterialUnavailable);
            return Failed(CubesConnectErrorReasons.DirectMaterialUnavailable);
        }

        // Only a host denial moves the join to the next device. Any other
        // failure (unreachable, pin mismatch, timeout) ends the connect.
        // An attempt that may be followed by another one does not stamp the
        // dial and join stages, because a stage is stamped once.
        // Each capability is made just before its own dial, so it does not age
        // while earlier devices are denied.
        for (var i = 0; i < candidates.Count; i++)
        {
            var last = i == candidates.Count - 1;
            CubesConnectDirectMaterial minted;
            try
            {
                minted = await candidates[i].MintAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                var error = ConnectErrors.FromException(CubesConnectErrorReasons.DirectMaterialUnavailable, ex);
                if (i == 0)
                    stages?.Stamp(CubesConnectStages.Material, error.Reason);
                return Failed(error);
            }

            if (i == 0)
                stages?.Stamp(CubesConnectStages.Material);
            var material = minted with
            {
                StageClock = last ? stages : null,
                QuicProbe = _quicProbe,
            };

            EndpointDial dialed;
            try
            {
                dialed = await _directConnect(material, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                var error = ConnectErrors.FromException(CubesConnectErrorReasons.DialFailed, ex);
                StampDialJoin(stages, null, error);
                return Failed(error);
            }
            catch (InvalidOperationException ex)
            {
                var error = ConnectErrors.FromException(CubesConnectErrorReasons.DialFailed, ex);
                StampDialJoin(stages, null, error);
                return Failed(error);
            }

            if (!dialed.IsOk)
            {
                if (!last && dialed.Error.Reason == ConnectivityReasons.JoinDenied)
                    continue;

                StampDialJoin(stages, null, dialed.Error);
                return Failed(WithDeviceHint(dialed.Error, placement));
            }

            var session = dialed.Value;
            StampDialJoin(stages, session, error: null);
            if (!await AcceptHeldAsync(held, stages, session, cancellationToken).ConfigureAwait(false))
                return Failed(CubesConnectErrorReasons.PlacementChanged);

            await EnrollAcceptedDeviceAsync(
                    held,
                    material.Bootstrap.Capability.DeviceId,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
            return Opened(new ConnectivityAttachEndpoint(session));
        }

        return Failed(CubesConnectErrorReasons.DirectMaterialUnavailable);
    }

    /// <summary>
    /// A host that denied every candidate device gets a message that names the
    /// option that picks the device. A denial of an enrolled device is shown as is.
    /// </summary>
    private static ConnectError WithDeviceHint(ConnectError error, PlacementRecord placement)
    {
        if (error.Reason != ConnectivityReasons.JoinDenied
            || !string.IsNullOrWhiteSpace(placement.Quic?.EnrolledDeviceId))
        {
            return error;
        }

        return new ConnectError(
            error.Reason,
            $"{error.Detail ?? "join denied"}. The host denied every paired device. "
            + "Run add-quic --device DEVICE_ID with a device that the host trusts.");
    }

    /// <summary>
    /// Saves the accepted device as the enrolled device, through the directory that
    /// loaded the placement, so the save goes to the same store as the load.
    /// A placement that has an enrolled value is not changed. A failed save does
    /// not fail the connect.
    /// </summary>
    private static async ValueTask EnrollAcceptedDeviceAsync(
        ConnectPlacementHold held,
        DeviceId device,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        var placement = held.Record;
        if (!string.IsNullOrWhiteSpace(placement.Quic?.EnrolledDeviceId)
            || held.Directory is not IQuicDeviceEnrollment enrollment)
        {
            return;
        }

        string? failure = null;
        try
        {
            var saved = await enrollment
                .EnrollQuicDeviceAsync(placement.Owner, placement.Id, device.Value, cancellationToken)
                .ConfigureAwait(false);
            if (!saved.Ok)
                failure = saved.Detail ?? saved.Reason ?? "placement was not saved";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failure = ex.Message;
        }

        if (failure is not null)
            stages?.NoteEnrollmentFailed(failure);
    }

    private async ValueTask<EndpointResult> OpenSshEndpointAsync(
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
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // An expected open failure is a typed error, not an exception.
            var error = ConnectErrors.FromException(CubesConnectErrorReasons.SshOpenFailed, ex);
            stages?.Stamp(CubesConnectStages.Dial, error.Reason);
            return Failed(error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stages?.Stamp(CubesConnectStages.Dial, CubesConnectErrorReasons.SshOpenFailed);
            throw;
        }

        if (!opened.Ok || opened.Path is null)
        {
            var detail = opened.Reason is null
                ? opened.Detail
                : $"{opened.Reason}: {opened.Detail}";
            var error = new ConnectError(
                CubesConnectErrorReasons.SshOpenFailed,
                ConnectErrors.OneLine(detail));
            stages?.Stamp(CubesConnectStages.Dial, error.Reason);
            return Failed(error);
        }

        stages?.Stamp(CubesConnectStages.Dial);
        if (!await AcceptHeldAsync(held, stages, opened.Path, cancellationToken).ConfigureAwait(false))
            return Failed(CubesConnectErrorReasons.PlacementChanged);

        return Opened(new SshAttachEndpoint(opened.Path));
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

    private static EndpointResult Opened(IAttachEndpoint endpoint) => EndpointResult.Ok(endpoint);

    private static EndpointResult Failed(string reason, string? detail = null) =>
        EndpointResult.Fail(new ConnectError(reason, detail));

    private static EndpointResult Failed(ConnectError error) => EndpointResult.Fail(error);
}
