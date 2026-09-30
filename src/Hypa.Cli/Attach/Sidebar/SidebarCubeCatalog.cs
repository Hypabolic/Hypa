using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>
/// Maps Placement directory rows to Cubes sidebar items.
/// Reachability comes from Placement. Connect transport is not here.
/// </summary>
public sealed record SidebarCubeCatalogLoad(
    IReadOnlyList<SidebarCubeItem> Items,
    SidebarCubeCatalogState State)
{
    public static SidebarCubeCatalogLoad Ready(IReadOnlyList<SidebarCubeItem> items) =>
        new(items, SidebarCubeCatalogState.Ready);

    public static SidebarCubeCatalogLoad Unavailable { get; } =
        new([], SidebarCubeCatalogState.Unavailable);
}

public interface ISidebarCubeCatalogSource
{
    ValueTask<SidebarCubeCatalogLoad> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class EnvironmentSidebarCubeCatalogSource : ISidebarCubeCatalogSource
{
    private readonly string? _placementDirectory;

    public EnvironmentSidebarCubeCatalogSource(ProcessOperatorPaths? paths = null)
    {
        _placementDirectory = paths?.PlacementDirectory;
    }

    public ValueTask<SidebarCubeCatalogLoad> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_placementDirectory))
            return SidebarCubeCatalog.LoadAsync(cancellationToken);
        return SidebarCubeCatalog.LoadFromDirectoryAsync(_placementDirectory, cancellationToken);
    }
}

public static class SidebarCubeCatalog
{
    public static IReadOnlyList<SidebarCubeItem> FromRows(IEnumerable<PlacementRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var materialized = rows.ToList();
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in materialized)
        {
            if (!labelCounts.TryAdd(row.DisplayName, 1))
                labelCounts[row.DisplayName]++;
        }

        var items = new List<SidebarCubeItem>();
        foreach (var row in materialized)
        {
            if (!TryKind(row.Kind, out var kind))
                continue;
            if (!TryReachability(row.Reachability, out var reachability))
                continue;
            var (suffix, connectEnabled, connectDetail, mappedReachability) = MapPeerPresentation(row, reachability);
            items.Add(new SidebarCubeItem
            {
                Id = row.Id.Value,
                Name = row.DisplayName,
                Kind = kind,
                Reachability = mappedReachability,
                WorkTitle = string.IsNullOrWhiteSpace(row.ActiveWorkId) ? null : row.ActiveWorkId,
                ProviderSuffix = suffix,
                ConnectEnabled = connectEnabled,
                ConnectDetail = connectDetail,
                ShowPlacementId = labelCounts[row.DisplayName] > 1,
                EnrolledDeviceId = row.Quic?.EnrolledDeviceId,
            });
        }

        return items;
    }

    public static ValueTask<SidebarCubeCatalogLoad> LoadAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            configuredIdentity: null,
            userName: Environment.UserName,
            cancellationToken);

    public static ValueTask<SidebarCubeCatalogLoad> LoadAsync(
        string? configuredIdentity,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return LoadFromDirectoryAsync(
                PlacementStatePaths.ResolveFromEnvironment(),
                configuredIdentity,
                userName,
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            return ValueTask.FromResult(SidebarCubeCatalogLoad.Unavailable);
        }
    }

    public static ValueTask<SidebarCubeCatalogLoad> LoadFromDirectoryAsync(
        string placementDirectory,
        CancellationToken cancellationToken = default) =>
        LoadFromDirectoryAsync(
            placementDirectory,
            configuredIdentity: null,
            userName: Environment.UserName,
            cancellationToken);

    public static async ValueTask<SidebarCubeCatalogLoad> LoadFromDirectoryAsync(
        string placementDirectory,
        string? configuredIdentity,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ProcessLocalOperatorIdentity.TryResolve(configuredIdentity, userName, out var actor))
                return SidebarCubeCatalogLoad.Unavailable;

            var directory = new PlacementDirectoryService(
                new FilePlacementDirectoryStore(placementDirectory));
            return await LoadAsync(directory, actor, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (InvalidDataException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (JsonException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
    }

    public static async ValueTask<SidebarCubeCatalogLoad> LoadAsync(
        IPlacementDirectory directory,
        DirectoryIdentity actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        try
        {
            var listed = await directory.ListAsync(actor, cancellationToken).ConfigureAwait(false);
            if (!listed.Ok || listed.Value is null)
                return SidebarCubeCatalogLoad.Unavailable;
            return SidebarCubeCatalogLoad.Ready(FromRows(listed.Value));
        }
        catch (IOException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (InvalidDataException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
        catch (JsonException)
        {
            return SidebarCubeCatalogLoad.Unavailable;
        }
    }

    private static (
        string? Suffix,
        bool ConnectEnabled,
        string? ConnectDetail,
        SidebarCubeReachability Reachability) MapPeerPresentation(
        PlacementRow row,
        SidebarCubeReachability baseReachability)
    {
        if (row.Ssh is not null)
        {
            var attention = SshPlacementPresentation.ResolveAttention(row.Reachability, row.Ssh);
            return (
                SshPlacementPresentation.ReachabilitySuffix(attention),
                SshPlacementPresentation.ConnectEnabled(attention),
                SshPlacementPresentation.ConnectDetail(attention, row.Ssh),
                MapSshReachability(attention, baseReachability));
        }

        if (row.Quic is not null)
        {
            var attention = QuicPlacementPresentation.ResolveAttention(row.Reachability, row.Quic);
            return (
                QuicPlacementPresentation.ReachabilitySuffix(attention),
                QuicPlacementPresentation.ConnectEnabled(attention),
                null,
                MapQuicReachability(attention, baseReachability));
        }

        return (null, true, null, baseReachability);
    }

    private static SidebarCubeReachability MapSshReachability(
        SshPlacementAttention attention,
        SidebarCubeReachability baseReachability) =>
        MapProviderReachability(
            attention is SshPlacementAttention.Disabled
                or SshPlacementAttention.ApprovalRequired
                or SshPlacementAttention.PreparationRequired
                or SshPlacementAttention.Unreachable,
            baseReachability);

    private static SidebarCubeReachability MapQuicReachability(
        QuicPlacementAttention attention,
        SidebarCubeReachability baseReachability) =>
        MapProviderReachability(
            attention is QuicPlacementAttention.Disabled or QuicPlacementAttention.Unreachable,
            baseReachability);

    private static SidebarCubeReachability MapProviderReachability(
        bool forceUnreachable,
        SidebarCubeReachability baseReachability)
    {
        if (forceUnreachable)
            return SidebarCubeReachability.Unreachable;
        return baseReachability;
    }

    private static bool TryKind(PlacementDirectoryKind kind, out SidebarCubeKind mapped)
    {
        mapped = kind switch
        {
            PlacementDirectoryKind.Local => SidebarCubeKind.Local,
            PlacementDirectoryKind.Peer => SidebarCubeKind.Peer,
            PlacementDirectoryKind.Cube => SidebarCubeKind.Cube,
            _ => (SidebarCubeKind)(-1),
        };
        return mapped is SidebarCubeKind.Local or SidebarCubeKind.Peer or SidebarCubeKind.Cube;
    }

    private static bool TryReachability(
        PlacementReachability reachability,
        out SidebarCubeReachability mapped)
    {
        mapped = reachability switch
        {
            PlacementReachability.Local => SidebarCubeReachability.Local,
            PlacementReachability.Reachable => SidebarCubeReachability.Reachable,
            PlacementReachability.Unreachable => SidebarCubeReachability.Unreachable,
            PlacementReachability.Asleep => SidebarCubeReachability.Asleep,
            _ => (SidebarCubeReachability)(-1),
        };
        return mapped is SidebarCubeReachability.Local
            or SidebarCubeReachability.Reachable
            or SidebarCubeReachability.Unreachable
            or SidebarCubeReachability.Asleep;
    }
}
