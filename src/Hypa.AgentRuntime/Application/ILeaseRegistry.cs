using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// In-memory lease authority for F1 (connection-bound). One active grant per (pane, scope).
/// </summary>
public interface ILeaseRegistry
{
    /// <summary>
    /// Claim a lease for <paramref name="holderId"/> on <paramref name="paneId"/>/<paramref name="scope"/>.
    /// Takeover without <paramref name="takeover"/> is denied when another holder is active.
    /// When <paramref name="paneAlive"/> is provided, it is evaluated under the registry lock
    /// before any holder transition so claim/takeover stays atomic with pane liveness.
    /// </summary>
    LeaseClaimResult Claim(
        string paneId,
        string scope,
        string holderId,
        bool takeover,
        string? reason,
        int? ttlMs,
        Func<string, bool>? paneAlive = null);

    /// <summary>Extend TTL for an active lease owned by <paramref name="holderId"/>.</summary>
    LeaseRenewResult Renew(string leaseId, string holderId, int? ttlMs);

    /// <summary>Release an active lease owned by <paramref name="holderId"/>.</summary>
    LeaseReleaseResult Release(string leaseId, string holderId);

    /// <summary>
    /// Authorize an operation. Accepts explicit lease id and/or holder connection id.
    /// </summary>
    LeaseAuthorizeResult TryAuthorize(
        string paneId,
        string scope,
        string? leaseId,
        string? holderId);

    /// <summary>Drop every lease held by a connection (disconnect). Returns released grants for audit.</summary>
    IReadOnlyList<LeaseState> DropConnection(string connectionId);

    /// <summary>
    /// Release every active lease for a pane (<c>pane.close</c> / spawn rollback).
    /// Returns released grants for <c>lease.changed</c> audit. Prunes residuals for the pane.
    /// </summary>
    IReadOnlyList<LeaseState> DropPane(string paneId);

    /// <summary>
    /// Lookup by lease id. Active grants are always present; released/expired grants
    /// may be pruned and return null (historical audit uses lease.changed events).
    /// </summary>
    LeaseState? Get(string leaseId);

    /// <summary>Active grant for (pane, scope), or null.</summary>
    LeaseState? GetActive(string paneId, string scope);
}

/// <summary>Result of <see cref="ILeaseRegistry.Claim"/>.</summary>
public sealed record LeaseClaimResult
{
    public required string Outcome { get; init; }
    public LeaseState? Lease { get; init; }
    /// <summary>Previous active lease when takeover swapped holders (for lease.changed audit).</summary>
    public LeaseState? PreviousLease { get; init; }
    public int? RetryAfterMs { get; init; }
}

/// <summary>Result of <see cref="ILeaseRegistry.Renew"/>.</summary>
public sealed record LeaseRenewResult
{
    public bool Ok { get; init; }
    public LeaseState? Lease { get; init; }
    public bool Expired { get; init; }
    public bool NotFound { get; init; }
    public bool NotHolder { get; init; }
}

/// <summary>Result of <see cref="ILeaseRegistry.Release"/>.</summary>
public sealed record LeaseReleaseResult
{
    public required string LeaseId { get; init; }
    public bool Released { get; init; }
    public LeaseState? Lease { get; init; }
}

/// <summary>Authorization decision for input/resize/admin gates.</summary>
public enum LeaseAuthorizeStatus
{
    Authorized = 0,
    Missing = 1,
    Expired = 2,
    WrongHolder = 3,
}

/// <summary>Result of <see cref="ILeaseRegistry.TryAuthorize"/>.</summary>
public sealed record LeaseAuthorizeResult
{
    public required LeaseAuthorizeStatus Status { get; init; }
    public LeaseState? Lease { get; init; }

    public static LeaseAuthorizeResult Authorized(LeaseState lease) =>
        new() { Status = LeaseAuthorizeStatus.Authorized, Lease = lease };

    public static LeaseAuthorizeResult Missing() =>
        new() { Status = LeaseAuthorizeStatus.Missing };

    public static LeaseAuthorizeResult Expired(LeaseState? lease = null) =>
        new() { Status = LeaseAuthorizeStatus.Expired, Lease = lease };

    public static LeaseAuthorizeResult WrongHolder(LeaseState? lease = null) =>
        new() { Status = LeaseAuthorizeStatus.WrongHolder, Lease = lease };
}
