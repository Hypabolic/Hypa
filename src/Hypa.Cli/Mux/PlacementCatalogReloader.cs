using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
// / Poll Placement directory changes.
/// Reload never selects another client's endpoint.
/// </summary>
public sealed class PlacementCatalogReloader : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ISidebarCubeCatalogSource _catalog;
    private readonly DirectoryIdentity _actor;
    private readonly CancellationTokenSource _cts = new();
    private readonly SshPlacementEndpointRegistry _endpoints;
    private Task? _loop;
    private IReadOnlyList<SidebarCubeItem> _previous = [];
    private bool _firstPoll = true;

    public PlacementCatalogReloader(
        ISidebarCubeCatalogSource catalog,
        DirectoryIdentity actor,
        SshPlacementEndpointRegistry endpoints)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _actor = actor;
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    }

    public event Action<IReadOnlyList<SidebarCubeItem>, SidebarCubeCatalogState>? Changed;

    public void Start()
    {
        if (_loop is not null)
            return;
        _loop = Task.Run(WatchAsync);
    }

    private async Task WatchAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var loaded = await _catalog.LoadAsync(_cts.Token).ConfigureAwait(false);
                if (_firstPoll || !CatalogKeysEqual(_previous, loaded.Items))
                {
                    _firstPoll = false;
                    _previous = loaded.Items;
                    ReconcileEndpoints(loaded.Items);
                    Changed?.Invoke(loaded.Items, loaded.State);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidDataException)
            {
            }

            try
            {
                await Task.Delay(PollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ReconcileEndpoints(IReadOnlyList<SidebarCubeItem> items)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ConnectEnabled)
                active.Add(item.Id);
        }

        _endpoints.RetireExcept(active);
    }

    private static bool CatalogKeysEqual(
        IReadOnlyList<SidebarCubeItem> previous,
        IReadOnlyList<SidebarCubeItem> current)
    {
        if (previous.Count != current.Count)
            return false;
        for (var i = 0; i < previous.Count; i++)
        {
            if (!string.Equals(previous[i].Id, current[i].Id, StringComparison.Ordinal)
                || !string.Equals(previous[i].Name, current[i].Name, StringComparison.Ordinal)
                || previous[i].Kind != current[i].Kind
                || previous[i].Reachability != current[i].Reachability
                || previous[i].ConnectEnabled != current[i].ConnectEnabled
                || !string.Equals(previous[i].ProviderSuffix, current[i].ProviderSuffix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Retire local SSH endpoint generations when catalog rows disappear or disable.
/// </summary>
public sealed class SshPlacementEndpointRegistry
{
    private readonly Dictionary<string, SshAttachEndpoint> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryRegister(string placementId, SshAttachEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            if (_active.ContainsKey(placementId))
                return false;
            _active[placementId] = endpoint;
            return true;
        }
    }

    public ulong RegisterConnect(string placementId, SshAttachEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        ArgumentNullException.ThrowIfNull(endpoint);
        SshAttachEndpoint? prior;
        var generation = endpoint.Generation;
        lock (_gate)
        {
            _active.Remove(placementId, out prior);
            _active[placementId] = endpoint;
        }

        if (prior is not null && !ReferenceEquals(prior, endpoint))
            _ = RetireQuietlyAsync(prior);
        return generation;
    }

    public bool IsConnectActive(string placementId, ulong generation, SshAttachEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            return IsConnectActiveLocked(placementId, generation, endpoint);
        }
    }

    private bool IsConnectActiveLocked(string placementId, ulong generation, SshAttachEndpoint endpoint) =>
        _active.TryGetValue(placementId, out var active)
        && ReferenceEquals(active, endpoint)
        && active.Generation == generation;

    public bool RetireIfActive(string placementId, ulong generation, SshAttachEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        ArgumentNullException.ThrowIfNull(endpoint);
        SshAttachEndpoint? retired;
        lock (_gate)
        {
            if (!IsConnectActiveLocked(placementId, generation, endpoint))
                return false;
            _active.Remove(placementId, out retired);
        }

        if (retired is not null)
            _ = RetireQuietlyAsync(retired);
        return true;
    }

    public void Retire(string placementId)
    {
        SshAttachEndpoint? endpoint;
        lock (_gate)
        {
            if (!_active.Remove(placementId, out endpoint))
                return;
        }

        _ = RetireQuietlyAsync(endpoint);
    }

    public void RetireExcept(IReadOnlySet<string> allowedPlacementIds)
    {
        List<SshAttachEndpoint> retired;
        lock (_gate)
        {
            retired = [];
            foreach (var (placementId, endpoint) in _active.ToList())
            {
                if (allowedPlacementIds.Contains(placementId))
                    continue;
                _active.Remove(placementId);
                retired.Add(endpoint);
            }
        }

        foreach (var endpoint in retired)
            _ = RetireQuietlyAsync(endpoint);
    }

    private static async Task RetireQuietlyAsync(SshAttachEndpoint endpoint)
    {
        try
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}
