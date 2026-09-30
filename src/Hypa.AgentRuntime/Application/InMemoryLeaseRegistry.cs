using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Thread-safe in-memory lease registry. F1 authority is connection-bound;
/// no SQLite durability.
/// </summary>
public sealed class InMemoryLeaseRegistry : ILeaseRegistry
{
    public const int DefaultTtlMs = 30_000;
    public const int DefaultGraceMs = 5_000;
    public const int MinTtlMs = 1_000;
    public const int MaxTtlMs = 24 * 60 * 60 * 1000;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, LeaseState> _byId = new(StringComparer.Ordinal);
    // key = pane_id + "\0" + scope
    private readonly Dictionary<string, string> _activeByPaneScope = new(StringComparer.Ordinal);
    private long _idCounter;

    public InMemoryLeaseRegistry(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public LeaseClaimResult Claim(
        string paneId,
        string scope,
        string holderId,
        bool takeover,
        string? reason,
        int? ttlMs,
        Func<string, bool>? paneAlive = null)
    {
        if (string.IsNullOrWhiteSpace(paneId) ||
            string.IsNullOrWhiteSpace(holderId) ||
            !LeaseScopes.IsKnown(scope))
        {
            return new LeaseClaimResult { Outcome = LeaseOutcomes.Invalid };
        }

        var ttl = ClampTtl(ttlMs);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            ExpireStaleUnderLock(now);

            // Atomic with liveness: reject before any holder transition.
            if (paneAlive is not null && !paneAlive(paneId))
                return new LeaseClaimResult { Outcome = LeaseOutcomes.Invalid };

            var key = PaneScopeKey(paneId, scope);

            if (_activeByPaneScope.TryGetValue(key, out var existingId) &&
                _byId.TryGetValue(existingId, out var existing) &&
                IsUsable(existing, now))
            {
                if (string.Equals(existing.HolderId, holderId, StringComparison.Ordinal))
                {
                    // Same holder re-claim: refresh TTL and report already_held.
                    var refreshed = existing with
                    {
                        ExpiresAt = now.AddMilliseconds(ttl),
                        GraceEndsAt = now.AddMilliseconds(ttl + DefaultGraceMs),
                        TtlMs = ttl,
                        Reason = reason ?? existing.Reason,
                        State = LeaseStates.Granted,
                    };
                    _byId[existing.LeaseId] = refreshed;
                    return new LeaseClaimResult
                    {
                        Outcome = LeaseOutcomes.AlreadyHeld,
                        Lease = refreshed,
                    };
                }

                if (!takeover)
                {
                    return new LeaseClaimResult
                    {
                        Outcome = LeaseOutcomes.Denied,
                        Lease = existing,
                        RetryAfterMs = Math.Max(
                            0,
                            (int)(existing.GraceEndsAt - now).TotalMilliseconds),
                    };
                }

                // Takeover: release old, grant new. Drop previous from _byId (audit uses
                // PreviousLease snapshot returned to the caller for lease.changed emit).
                var released = existing with { State = LeaseStates.Released };
                _byId.Remove(existing.LeaseId);
                _activeByPaneScope.Remove(key);

                var granted = CreateGrantUnderLock(paneId, scope, holderId, reason, ttl, now);
                return new LeaseClaimResult
                {
                    Outcome = LeaseOutcomes.Granted,
                    Lease = granted,
                    PreviousLease = released,
                };
            }

            var lease = CreateGrantUnderLock(paneId, scope, holderId, reason, ttl, now);
            return new LeaseClaimResult
            {
                Outcome = LeaseOutcomes.Granted,
                Lease = lease,
            };
        }
    }

    public LeaseRenewResult Renew(string leaseId, string holderId, int? ttlMs)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(holderId))
            return new LeaseRenewResult { NotFound = true };

        var ttl = ClampTtl(ttlMs);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            ExpireStaleUnderLock(now);
            if (!_byId.TryGetValue(leaseId, out var lease))
                return new LeaseRenewResult { NotFound = true };

            if (!string.Equals(lease.HolderId, holderId, StringComparison.Ordinal))
                return new LeaseRenewResult { NotHolder = true, Lease = lease };

            if (lease.State != LeaseStates.Granted)
                return new LeaseRenewResult { Expired = true, Lease = lease };

            // Renew allowed through grace window.
            if (now > lease.GraceEndsAt)
            {
                var expired = lease with { State = LeaseStates.Expired };
                DropActiveIfMatchUnderLock(expired);
                _byId.Remove(leaseId);
                return new LeaseRenewResult { Expired = true, Lease = expired };
            }

            var renewed = lease with
            {
                ExpiresAt = now.AddMilliseconds(ttl),
                GraceEndsAt = now.AddMilliseconds(ttl + DefaultGraceMs),
                TtlMs = ttl,
                State = LeaseStates.Granted,
            };
            _byId[leaseId] = renewed;
            _activeByPaneScope[PaneScopeKey(renewed.PaneId, renewed.Scope)] = leaseId;
            return new LeaseRenewResult { Ok = true, Lease = renewed };
        }
    }

    public LeaseReleaseResult Release(string leaseId, string holderId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return new LeaseReleaseResult { LeaseId = leaseId ?? "", Released = false };

        lock (_gate)
        {
            if (!_byId.TryGetValue(leaseId, out var lease))
                return new LeaseReleaseResult { LeaseId = leaseId, Released = false };

            if (!string.Equals(lease.HolderId, holderId, StringComparison.Ordinal))
                return new LeaseReleaseResult { LeaseId = leaseId, Released = false, Lease = lease };

            if (lease.State != LeaseStates.Granted)
            {
                // Non-active residual — drop so history does not accumulate.
                _byId.Remove(leaseId);
                return new LeaseReleaseResult { LeaseId = leaseId, Released = false, Lease = lease };
            }

            var released = lease with { State = LeaseStates.Released };
            DropActiveIfMatchUnderLock(released);
            _byId.Remove(leaseId);
            return new LeaseReleaseResult { LeaseId = leaseId, Released = true, Lease = released };
        }
    }

    public LeaseAuthorizeResult TryAuthorize(
        string paneId,
        string scope,
        string? leaseId,
        string? holderId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || !LeaseScopes.IsKnown(scope))
            return LeaseAuthorizeResult.Missing();

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // Explicit lease id: look up before ExpireStale so a past-grace grant still
            // returns Expired (then prune). Holder-only path sweeps first.
            LeaseState? lease = null;
            if (!string.IsNullOrWhiteSpace(leaseId))
            {
                _byId.TryGetValue(leaseId, out lease);
            }
            else
            {
                ExpireStaleUnderLock(now);
                if (!string.IsNullOrWhiteSpace(holderId))
                {
                    var key = PaneScopeKey(paneId, scope);
                    if (_activeByPaneScope.TryGetValue(key, out var activeId))
                        _byId.TryGetValue(activeId, out lease);
                }
            }

            if (lease is null)
                return LeaseAuthorizeResult.Missing();

            if (!string.Equals(lease.PaneId, paneId, StringComparison.Ordinal) ||
                !string.Equals(lease.Scope, scope, StringComparison.Ordinal))
            {
                return LeaseAuthorizeResult.Missing();
            }

            if (lease.State != LeaseStates.Granted)
            {
                // Residual non-active entry: report and prune.
                var status = lease.State == LeaseStates.Expired
                    ? LeaseAuthorizeResult.Expired(lease)
                    : LeaseAuthorizeResult.Missing();
                _byId.Remove(lease.LeaseId);
                return status;
            }

            // Usable through grace; past grace is expired then pruned.
            if (now > lease.GraceEndsAt)
            {
                var expired = lease with { State = LeaseStates.Expired };
                DropActiveIfMatchUnderLock(expired);
                _byId.Remove(lease.LeaseId);
                return LeaseAuthorizeResult.Expired(expired);
            }

            if (!string.IsNullOrWhiteSpace(holderId) &&
                !string.Equals(lease.HolderId, holderId, StringComparison.Ordinal))
            {
                return LeaseAuthorizeResult.WrongHolder(lease);
            }

            if (!string.IsNullOrWhiteSpace(leaseId) &&
                !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return LeaseAuthorizeResult.Missing();
            }

            return LeaseAuthorizeResult.Authorized(lease);
        }
    }

    public IReadOnlyList<LeaseState> DropConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return [];

        lock (_gate)
        {
            var released = new List<LeaseState>();
            foreach (var lease in _byId.Values.ToList())
            {
                if (lease.State != LeaseStates.Granted)
                    continue;
                if (!string.Equals(lease.HolderId, connectionId, StringComparison.Ordinal))
                    continue;

                var rel = lease with { State = LeaseStates.Released };
                DropActiveIfMatchUnderLock(rel);
                _byId.Remove(lease.LeaseId);
                released.Add(rel);
            }

            return released;
        }
    }

    public IReadOnlyList<LeaseState> DropPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return [];

        lock (_gate)
        {
            var released = new List<LeaseState>();
            foreach (var lease in _byId.Values.ToList())
            {
                if (!string.Equals(lease.PaneId, paneId, StringComparison.Ordinal))
                    continue;

                if (lease.State == LeaseStates.Granted)
                {
                    var rel = lease with { State = LeaseStates.Released };
                    DropActiveIfMatchUnderLock(rel);
                    _byId.Remove(lease.LeaseId);
                    released.Add(rel);
                }
                else
                {
                    // Residual non-active entry for this pane — prune.
                    DropActiveIfMatchUnderLock(lease);
                    _byId.Remove(lease.LeaseId);
                }
            }

            return released;
        }
    }

    public LeaseState? Get(string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return null;
        lock (_gate)
        {
            ExpireStaleUnderLock(_time.GetUtcNow());
            return _byId.TryGetValue(leaseId, out var l) ? l : null;
        }
    }

    public LeaseState? GetActive(string paneId, string scope)
    {
        if (string.IsNullOrWhiteSpace(paneId) || !LeaseScopes.IsKnown(scope))
            return null;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            ExpireStaleUnderLock(now);
            var key = PaneScopeKey(paneId, scope);
            if (!_activeByPaneScope.TryGetValue(key, out var id))
                return null;
            return _byId.TryGetValue(id, out var l) && IsUsable(l, now) ? l : null;
        }
    }

    private LeaseState CreateGrantUnderLock(
        string paneId,
        string scope,
        string holderId,
        string? reason,
        int ttl,
        DateTimeOffset now)
    {
        var id = "lease_" + Interlocked.Increment(ref _idCounter);
        var lease = new LeaseState
        {
            LeaseId = id,
            PaneId = paneId,
            Scope = scope,
            HolderId = holderId,
            State = LeaseStates.Granted,
            ExpiresAt = now.AddMilliseconds(ttl),
            GraceEndsAt = now.AddMilliseconds(ttl + DefaultGraceMs),
            TtlMs = ttl,
            Reason = reason,
            CreatedAt = now,
        };
        _byId[id] = lease;
        _activeByPaneScope[PaneScopeKey(paneId, scope)] = id;
        return lease;
    }

    private void ExpireStaleUnderLock(DateTimeOffset now)
    {
        foreach (var lease in _byId.Values.ToList())
        {
            if (lease.State != LeaseStates.Granted)
            {
                // Sweep residual released/expired entries if any remain.
                if (lease.State is LeaseStates.Released or LeaseStates.Expired)
                    _byId.Remove(lease.LeaseId);
                continue;
            }

            if (now <= lease.GraceEndsAt)
                continue;

            DropActiveIfMatchUnderLock(lease);
            _byId.Remove(lease.LeaseId);
        }
    }

    private void DropActiveIfMatchUnderLock(LeaseState lease)
    {
        var key = PaneScopeKey(lease.PaneId, lease.Scope);
        if (_activeByPaneScope.TryGetValue(key, out var id) &&
            string.Equals(id, lease.LeaseId, StringComparison.Ordinal))
        {
            _activeByPaneScope.Remove(key);
        }
    }

    private static bool IsUsable(LeaseState lease, DateTimeOffset now) =>
        lease.State == LeaseStates.Granted && now <= lease.GraceEndsAt;

    private static string PaneScopeKey(string paneId, string scope) => paneId + "\0" + scope;

    private static int ClampTtl(int? ttlMs)
    {
        var ttl = ttlMs ?? DefaultTtlMs;
        if (ttl < MinTtlMs)
            ttl = MinTtlMs;
        if (ttl > MaxTtlMs)
            ttl = MaxTtlMs;
        return ttl;
    }
}
