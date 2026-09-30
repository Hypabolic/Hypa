using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// Renews input and resize leases on a 10s beat. Default TTL is 30s.
/// </summary>
public sealed class LeaseRenewLoop
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(10);
    public const int DefaultTtlMs = 30_000;
    public const int MaxConsecutiveFailures = 3;

    private readonly TimeProvider _time;
    private readonly TimeSpan _period;
    private readonly int _ttlMs;
    private readonly Func<string, int, CancellationToken, Task> _renew;
    private readonly object _gate = new();
    private readonly List<string> _leaseIds = [];

    public LeaseRenewLoop(
        Func<string, int, CancellationToken, Task> renew,
        TimeProvider? time = null,
        TimeSpan? period = null,
        int ttlMs = DefaultTtlMs)
    {
        _renew = renew ?? throw new ArgumentNullException(nameof(renew));
        _time = time ?? TimeProvider.System;
        _period = period ?? DefaultPeriod;
        _ttlMs = ttlMs > 0 ? ttlMs : DefaultTtlMs;
    }

    public IReadOnlyList<string> LeaseIds
    {
        get
        {
            lock (_gate)
                return _leaseIds.ToArray();
        }
    }

    public int RenewCount { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public void Track(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        lock (_gate)
        {
            if (!_leaseIds.Contains(leaseId, StringComparer.Ordinal))
                _leaseIds.Add(leaseId);
        }
    }

    public void Untrack(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        lock (_gate)
            _leaseIds.Remove(leaseId);
    }

    public bool IsTracked(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return false;
        lock (_gate)
            return _leaseIds.Contains(leaseId, StringComparer.Ordinal);
    }

    internal static bool IsStaleLease(ControlPlaneException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Code is ProtocolErrorCodes.NotFound
            or ProtocolErrorCodes.LeaseExpired
            or ProtocolErrorCodes.LeaseRequired;
    }

    public async Task RenewOnceAsync(CancellationToken ct)
    {
        string[] ids;
        lock (_gate)
            ids = _leaseIds.ToArray();
        foreach (var id in ids)
        {
            if (!IsTracked(id))
                continue;
            try
            {
                await _renew(id, _ttlMs, ct).ConfigureAwait(false);
                RenewCount++;
            }
            catch (ControlPlaneException ex) when (IsStaleLease(ex))
            {
                Untrack(id);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_period, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await RenewOnceAsync(ct).ConfigureAwait(false);
                ConsecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (ControlPlaneClientTimeoutException)
            {
                throw;
            }
            catch (ControlPlaneException)
            {
                throw;
            }
            catch (Exception) when (ConsecutiveFailures < MaxConsecutiveFailures - 1)
            {
                ConsecutiveFailures++;
            }
        }
    }
}
