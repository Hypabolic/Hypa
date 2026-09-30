using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Domain;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Open the attach client against the mux that owns the active Run.
/// Local Runs use the stored Unix socket. Peer Runs join Connectivity.
/// This does not move Work.
/// </summary>
public interface IWorkAttachClient
{
    Task<WorkAttachClientOutcome> AttachAsync(
        WorkAttachClientRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkAttachClientRequest
{
    public required WorkAttachTarget Target { get; init; }
    public required PlacementRecord Placement { get; init; }
    public required IPlacementDirectory PlacementDirectory { get; init; }
    public bool Once { get; init; }
    public bool InputRedirected { get; init; }
    public bool OutputRedirected { get; init; }
    public AttachClientConfig? Config { get; init; }
    public string? HypaEnv { get; init; }
}

public sealed record WorkAttachClientOutcome
{
    public required bool Ok { get; init; }
    public required int ExitCode { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? TransportKind { get; init; }
    public bool UsedRelay { get; init; }
    public bool CalledServerStop { get; init; }

    public static WorkAttachClientOutcome Fail(string reason, string detail, int exit = 2) =>
        new()
        {
            Ok = false,
            ExitCode = exit,
            Reason = reason,
            Detail = detail,
        };
}

public sealed class WorkAttachClientService : IWorkAttachClient
{
    private readonly ICubesConnectEndpointResolver _resolver;
    private readonly IMuxAttachDriver _driver;

    public WorkAttachClientService()
        : this(
            new DirectoryCubesConnectEndpointResolver(),
            new AttachSession(hostReach: new NetworkInterfaceHostReachCatalog()))
    {
    }

    public WorkAttachClientService(
        ICubesConnectEndpointResolver resolver,
        IMuxAttachDriver driver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public async Task<WorkAttachClientOutcome> AttachAsync(
        WorkAttachClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Placement);
        ArgumentNullException.ThrowIfNull(request.PlacementDirectory);

        var config = request.Config ?? AttachClientConfig.Default;
        if (NestedAttachGuard.IsBlocked(config, request.HypaEnv))
        {
            return WorkAttachClientOutcome.Fail(
                CubesConnectReasons.NestedAttachBlocked,
                NestedAttachGuard.Message,
                exit: 1);
        }

        var resolved = await ResolveEndpointAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.Ok || resolved.Endpoint is null)
        {
            return WorkAttachClientOutcome.Fail(
                resolved.Reason ?? ContinuityReasons.PeerUnavailable,
                resolved.Detail ?? "attach target is not reachable");
        }

        var socketPath = resolved.Endpoint is UnixAttachEndpoint unix
            ? unix.SocketPath
            : "";
        var ready = new MuxReadyInfo(
            request.Target.WorkId.Value,
            socketPath,
            PingJson: "{}",
            resolved.Endpoint);
        var attachRequest = new MuxAttachRequest(
            request.Once,
            request.InputRedirected,
            request.OutputRedirected,
            Config: config,
            RemoteDestination: request.Placement.Kind != PlacementDirectoryKind.Local,
            PlacementDisplayName: request.Placement.DisplayName,
            PlacementKind: ToCubeKind(request.Placement.Kind),
            PlacementId: request.Placement.Id.Value);

        int exit;
        try
        {
            exit = await _driver.RunAsync(ready, attachRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new WorkAttachClientOutcome
            {
                Ok = false,
                ExitCode = 130,
                TransportKind = resolved.Endpoint.Kind,
                UsedRelay = IsRelay(resolved.Endpoint),
            };
        }

        var calledStop = _driver is AttachSession session && session.CalledServerStop;
        return new WorkAttachClientOutcome
        {
            Ok = exit == 0,
            ExitCode = exit,
            TransportKind = resolved.Endpoint.Kind,
            UsedRelay = IsRelay(resolved.Endpoint),
            CalledServerStop = calledStop,
        };
    }

    private async Task<(bool Ok, IAttachEndpoint? Endpoint, string? Reason, string? Detail)> ResolveEndpointAsync(
        WorkAttachClientRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Placement.Kind == PlacementDirectoryKind.Local)
            return await ResolveLocalUnixAsync(request.Target, cancellationToken).ConfigureAwait(false);

        if (request.Placement.Reachability is PlacementReachability.Unreachable
            or PlacementReachability.Asleep)
        {
            return (false, null, ContinuityReasons.PeerUnavailable, "peer Work is not reachable");
        }

        IAttachEndpoint? endpoint;
        try
        {
            endpoint = await BindResolver(request)
                    .ResolveAsync(
                        ToCube(request.Placement),
                        cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return (false, null, ContinuityReasons.PeerUnavailable, "Connectivity join failed");
        }
        catch (InvalidOperationException)
        {
            return (false, null, ContinuityReasons.PeerUnavailable, "Connectivity join failed");
        }

        if (endpoint is null)
            return (false, null, ContinuityReasons.PeerUnavailable, "Connectivity join failed");

        if (endpoint is UnixAttachEndpoint)
        {
            return (
                false,
                null,
                ContinuityReasons.PeerUnavailable,
                "peer Work uses Connectivity");
        }

        return (true, endpoint, null, null);
    }

    private static async Task<(bool Ok, IAttachEndpoint? Endpoint, string? Reason, string? Detail)> ResolveLocalUnixAsync(
        WorkAttachTarget target,
        CancellationToken cancellationToken)
    {
        if (!TryUnixSocketPath(target.MuxEndpoint, out var path))
        {
            return (
                false,
                null,
                CubesConnectReasons.DestUnreachable,
                "local Run has no Unix socket");
        }

        var ping = await MuxControlPlane.TryPingAsync(path, cancellationToken).ConfigureAwait(false);
        if (ping is null)
        {
            return (
                false,
                null,
                CubesConnectReasons.DestUnreachable,
                "local mux socket is not reachable");
        }

        return (true, new UnixAttachEndpoint(path), null, null);
    }

    private ICubesConnectEndpointResolver BindResolver(WorkAttachClientRequest request)
    {
        if (_resolver is not DirectoryCubesConnectEndpointResolver production)
            return _resolver;
        return production.WithDirectory(request.PlacementDirectory);
    }

    internal static bool TryUnixSocketPath(string? muxEndpoint, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(muxEndpoint))
            return false;

        var trimmed = muxEndpoint.Trim();
        if (trimmed.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["unix:".Length..];
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Contains("://", StringComparison.Ordinal))
            return false;

        path = trimmed;
        return true;
    }

    internal static SidebarCubeItem ToCube(PlacementRecord placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new SidebarCubeItem
        {
            Id = placement.Id.Value,
            Name = placement.DisplayName,
            Kind = ToCubeKind(placement.Kind),
            Reachability = ToCubeReachability(placement.Reachability),
        };
    }

    private static SidebarCubeKind ToCubeKind(PlacementDirectoryKind kind) =>
        kind switch
        {
            PlacementDirectoryKind.Local => SidebarCubeKind.Local,
            PlacementDirectoryKind.Peer => SidebarCubeKind.Peer,
            PlacementDirectoryKind.Cube => SidebarCubeKind.Cube,
            _ => SidebarCubeKind.Peer,
        };

    private static SidebarCubeReachability ToCubeReachability(PlacementReachability reachability) =>
        reachability switch
        {
            PlacementReachability.Local => SidebarCubeReachability.Local,
            PlacementReachability.Reachable => SidebarCubeReachability.Reachable,
            PlacementReachability.Unreachable => SidebarCubeReachability.Unreachable,
            PlacementReachability.Asleep => SidebarCubeReachability.Asleep,
            _ => SidebarCubeReachability.Unreachable,
        };

    private static bool IsRelay(IAttachEndpoint endpoint) =>
        string.Equals(endpoint.Kind, AttachEndpointKinds.Connectivity, StringComparison.Ordinal);
}
