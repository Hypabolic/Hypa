using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// OpenSSH adapter for <see cref="IRemoteMuxPath"/>.
/// The local listener bridges accepted connections over SSH stdio to the remote mux.
/// </summary>
public sealed class OpenSshRemoteMuxAdapter : IRemoteMuxPath
{
    private readonly IOpenSshProcess _ssh;
    private readonly RemoteConnectionFence _fence;
    private readonly ISshStdioBridgeFactory _bridgeFactory;
    private readonly bool _unixClient;

    public OpenSshRemoteMuxAdapter()
        : this(
            new ProcessOpenSshProcess(),
            new RemoteConnectionFence(),
            new SshStdioBridgeFactory(),
            unixClient: OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
    }

    public OpenSshRemoteMuxAdapter(
        IOpenSshProcess ssh,
        RemoteConnectionFence fence,
        ISshStdioBridgeFactory? bridgeFactory = null,
        bool? unixClient = null)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _fence = fence ?? throw new ArgumentNullException(nameof(fence));
        _bridgeFactory = bridgeFactory ?? new SshStdioBridgeFactory();
        _unixClient = unixClient ?? (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
    }

    public async ValueTask<RemoteMuxOutcome> OpenAsync(
        RemoteMuxOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveProfile(request, out var profile, out var resolveError))
            return resolveError!;

        if (!profile.Enabled)
        {
            return RemoteMuxOutcome.Failure(
                RemoteMuxReasons.ProfileDisabled,
                "Disabled or removed profiles cannot start a new connection.");
        }

        if (request.LiveHandoff && !RemoteRestartPolicy.LiveHandoffAllowed(_unixClient, requested: true))
        {
            return RemoteMuxOutcome.Failure(
                RemoteMuxReasons.HandoffUnsupported,
                "--handoff is experimental and Unix-only.");
        }

        if (!_unixClient)
        {
            return RemoteMuxOutcome.Failure(
                RemoteMuxReasons.PlatformUnsupported,
                "SSH stdio bridge requires a Unix local client.");
        }

        var key = RemoteConnectionFence.EndpointKey(profile.Target, profile.Session);
        var generation = _fence.Assign(key);

        ManagedSshConfig? managed = null;
        ISshStdioBridge? bridge = null;
        RemoteBridgeDirectory? bridgeDir = null;
        try
        {
            if (request.ManageSshConfig)
            {
                managed = ManagedSshConfigWriter.Write(
                    ManagedSshConfigWriter.DefaultUserConfigPath(),
                    includeControlSocket: ManagedSshConfigWriter.UsesControlSocket);
            }

            var probe = await ProbeAsync(profile, request, managed, cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Ok)
                return FailAndRetire(key, generation, probe.Failure!);

            var plan = RemoteRestartPolicy.Decide(
                probe.RestartReason,
                request.LiveHandoff,
                request.LiveHandoffEnabled);
            if (plan == RemoteServerRestartPlan.StopRequired)
            {
                return FailAndRetire(
                    key,
                    generation,
                    RemoteMuxOutcome.Failure(
                        RemoteMuxReasons.HandoffRequired,
                        "Remote mux is incompatible. Retry with --handoff on an interactive Unix terminal."));
            }

            if (RemoteRestartPolicy.RequiresConsent(plan) && !request.OperatorConsent)
            {
                return FailAndRetire(
                    key,
                    generation,
                    RemoteMuxOutcome.Failure(
                        RemoteMuxReasons.RestartRequired,
                        RemoteRestartPolicy.ConsentCopy()));
            }

            var started = false;
            var requestedHandoff = false;
            if (plan == RemoteServerRestartPlan.LiveHandoff)
            {
                requestedHandoff = true;
                var handoff = await RunRemoteAsync(
                        profile,
                        request,
                        managed,
                        RemoteMuxCommands.LiveHandoffCommand(profile.Session),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!handoff.Success)
                {
                    return FailAndRetire(
                        key,
                        generation,
                        RemoteMuxOutcome.Failure(
                            RemoteMuxReasons.Incompatible,
                            "Remote live handoff failed. The remote mux was not replaced."));
                }
            }
            else if (!probe.ServerRunning)
            {
                var start = await RunRemoteAsync(
                        profile,
                        request,
                        managed,
                        RemoteMuxCommands.RemoteStartCommand(profile.Session),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!start.Success)
                {
                    return FailAndRetire(
                        key,
                        generation,
                        RemoteMuxOutcome.Failure(
                            RemoteMuxReasons.Incompatible,
                            "Remote mux is not ready. Install a matching hypa on the remote host."));
                }

                started = true;
            }

            if (!_fence.Accept(key, generation))
            {
                return RemoteMuxOutcome.Failure(
                    RemoteMuxReasons.GenerationStale,
                    "Stale connection generation was rejected.");
            }

            bridgeDir = RemoteBridgeDirectory.Create();
            try
            {
                bridge = _bridgeFactory.Start(new SshStdioBridgeStartRequest
                {
                    Target = profile.Target,
                    Session = profile.Session,
                    LocalSocketPath = bridgeDir.SocketPath,
                    ConfigPath = managed?.ConfigPath,
                    ControlPath = managed?.ControlPath,
                    BatchMode = !request.Interactive,
                    FreshDirectory = true,
                });
            }
            catch (Exception ex)
            {
                bridgeDir.Dispose();
                return FailAndRetire(
                    key,
                    generation,
                    RemoteMuxOutcome.Failure(
                        RemoteMuxReasons.Internal,
                        ex.Message));
            }

            var capturedManaged = managed;
            var capturedBridge = bridge;
            var capturedBridgeDir = bridgeDir;
            managed = null;
            bridge = null;
            bridgeDir = null;
            return RemoteMuxOutcome.Success(new RemoteMuxPath
            {
                LocalSocketPath = capturedBridgeDir.SocketPath,
                Session = profile.Session,
                Target = profile.Target,
                Generation = generation,
                ProfileId = profile.Id,
                StartedRemoteServer = started,
                RequestedLiveHandoff = requestedHandoff,
                DisposeAsyncAction = async () =>
                {
                    capturedManaged?.Dispose();
                    if (capturedBridge is not null)
                        await capturedBridge.DisposeAsync().ConfigureAwait(false);
                    capturedBridgeDir.Dispose();
                },
            });
        }
        catch (OperationCanceledException)
        {
            _fence.Retire(key, generation);
            throw;
        }
        finally
        {
            if (bridge is not null)
                await bridge.DisposeAsync().ConfigureAwait(false);
            bridgeDir?.Dispose();
            managed?.Dispose();
        }
    }

    private async Task<RemoteMuxProbeResult> ProbeAsync(
        PeerProfile profile,
        RemoteMuxOpenRequest request,
        ManagedSshConfig? managed,
        CancellationToken cancellationToken)
    {
        var result = await RunRemoteAsync(
                profile,
                request,
                managed,
                RemoteMuxProbe.BuildShell(profile.Session),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            return RemoteMuxProbeResult.Fail(ClassifySshFailure(profile.Target, result));

        return RemoteMuxProbe.Parse(result.Stdout);
    }

    private async Task<OpenSshProcessResult> RunRemoteAsync(
        PeerProfile profile,
        RemoteMuxOpenRequest request,
        ManagedSshConfig? managed,
        string remoteCommand,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(5));
        return await _ssh.RunAsync(
                new OpenSshProcessRequest
                {
                    Target = profile.Target,
                    RemoteCommand = remoteCommand,
                    ConfigPath = managed?.ConfigPath,
                    ControlPath = managed?.ControlPath,
                    BatchMode = !request.Interactive,
                },
                attempt.Token)
            .ConfigureAwait(false);
    }

    private static RemoteMuxOutcome ClassifySshFailure(string target, OpenSshProcessResult result)
    {
        var quotedTarget = OpenSshArgumentBuilder.Quote(target);
        var text = result.Stderr + result.Stdout;
        if (text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authenticity of host", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteMuxOutcome.Failure(
                RemoteMuxReasons.ApprovalRequired,
                "OpenSSH approval required for "
                + target
                + ". Run ssh "
                + quotedTarget
                + " in a terminal, then retry.");
        }

        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteMuxOutcome.Failure(
                RemoteMuxReasons.AuthenticationFailed,
                "OpenSSH authentication failed for "
                + target
                + ". Check your SSH configuration, then retry.");
        }

        return RemoteMuxOutcome.Failure(
            RemoteMuxReasons.Incompatible,
            "Remote mux is not reachable through OpenSSH.");
    }

    private RemoteMuxOutcome FailAndRetire(string key, ulong generation, RemoteMuxOutcome outcome)
    {
        _fence.Retire(key, generation);
        return outcome;
    }

    private static bool TryResolveProfile(
        RemoteMuxOpenRequest request,
        out PeerProfile profile,
        out RemoteMuxOutcome? error)
    {
        profile = null!;
        error = null;
        if (request.Profile is { } supplied)
        {
            if (!PeerProfile.TryCreate(
                    supplied.Id,
                    supplied.Label,
                    supplied.Target,
                    supplied.Session,
                    supplied.Enabled,
                    supplied.Provider,
                    out profile,
                    out var profileError))
            {
                error = RemoteMuxOutcome.Failure(
                    RemoteMuxReasons.ProfileInvalid,
                    profileError ?? "Invalid SSH Placement fields. Check the label, target, and session.");
                return false;
            }

            return true;
        }

        if (string.IsNullOrEmpty(request.ExplicitTarget))
        {
            error = RemoteMuxOutcome.Failure(
                RemoteMuxReasons.TargetInvalid,
                "missing value for --remote");
            return false;
        }

        if (!PeerProfile.TryCreate(
                PeerProfile.NewId(),
                request.ExplicitTarget,
                request.ExplicitTarget,
                request.Session,
                enabled: true,
                PeerProviders.Ssh,
                out profile,
                out var explicitError))
        {
            error = RemoteMuxOutcome.Failure(
                RemoteMuxReasons.TargetInvalid,
                explicitError ?? "missing value for --remote");
            return false;
        }

        return true;
    }
}
