using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

public sealed class PanePlacementAuthorityService : IPanePlacementAuthorityService
{
    public const string OccupantPrefix = "occ_";
    public const string ParentPrefix = "par_";
    public const int SecretBytes = 32;

    private readonly ILeaseRegistry _leases;
    private readonly object _gate = new();
    private readonly Dictionary<string, OccupantRecord> _occupants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parentCapabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

    public PanePlacementAuthorityService(ILeaseRegistry leases)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    public string IssueOccupant(PaneId paneId, int generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId.Value);
        var token = Mint(OccupantPrefix);
        lock (_gate)
        {
            _occupants[paneId.Value] = new OccupantRecord(token, generation);
            ClearSequences(paneId.Value, PlacementAuthorityRole.Occupant);
        }

        return token;
    }

    public string IssueParentCapability(PaneId childPaneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childPaneId.Value);
        var token = Mint(ParentPrefix);
        lock (_gate)
        {
            _parentCapabilities[childPaneId.Value] = token;
            ClearSequences(childPaneId.Value, PlacementAuthorityRole.Parent);
        }

        return token;
    }

    public Result<PaneId?, PlacementAuthorityError> ValidateCreateParent(PlacementParentProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var claimed = EmptyToNull(proof.ClaimedParentPaneId);
        var token = EmptyToNull(proof.OccupantToken);
        var leaseId = EmptyToNull(proof.LeaseId);
        var status = EmptyToNull(proof.StatusSource);

        if (status is not null && token is null && leaseId is null)
            return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.StatusCredential);

        if (token is not null)
        {
            lock (_gate)
            {
                var parent = ResolveOccupantUnlocked(token);
                if (parent is null)
                    return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.ForeignCredential);
                if (claimed is not null && !string.Equals(claimed, parent.Value.Value, StringComparison.Ordinal))
                    return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.UnprovenParent);
                return Result<PaneId?, PlacementAuthorityError>.Ok(parent);
            }
        }

        if (leaseId is not null)
        {
            if (claimed is null)
                return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.UnprovenParent);
            if (string.IsNullOrWhiteSpace(proof.HolderId)
                || !LeaseOwnsParent(claimed, leaseId, proof.HolderId))
            {
                return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.ForeignCredential);
            }

            return Result<PaneId?, PlacementAuthorityError>.Ok(new PaneId(claimed));
        }

        if (claimed is not null)
            return Result<PaneId?, PlacementAuthorityError>.Fail(PlacementAuthorityError.UnprovenParent);

        return Result<PaneId?, PlacementAuthorityError>.Ok(null);
    }

    public Result<PlacementAuthorityGrant, PlacementAuthorityError> Authorize(
        PlacementAuthorityRequest request,
        IOverlayPlacementFence? fence = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
            return AuthorizeUnlocked(request, fence);
    }

    public Result<PlacementApplyResult<T, E>, PlacementAuthorityError> Apply<T, E>(
        PlacementAuthorityRequest request,
        Func<Result<T, E>> mutate,
        IOverlayPlacementFence? fence = null,
        bool commitSequence = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var auth = AuthorizeUnlocked(request, fence);
            if (!auth.IsOk)
                return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Fail(auth.Error);

            var grant = auth.Value;
            if (grant.SequenceRequired)
            {
                if (request.Sequence is not { } seq)
                    return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Fail(
                        PlacementAuthorityError.MissingSequence);
                if (IsStaleUnlocked(grant, seq))
                {
                    return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Ok(
                        new PlacementApplyResult<T, E> { Changed = false, Stale = true });
                }
            }
            else if (request.Sequence is { } optional
                     && IsStaleUnlocked(grant, optional))
            {
                return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Ok(
                    new PlacementApplyResult<T, E> { Changed = false, Stale = true });
            }

            var mutation = mutate();
            if (!mutation.IsOk)
            {
                return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Ok(
                    new PlacementApplyResult<T, E>
                    {
                        Changed = false,
                        MutationError = mutation.Error,
                    });
            }

            if (commitSequence && request.Sequence is { } accepted)
                AcceptUnlocked(grant, accepted);

            return Result<PlacementApplyResult<T, E>, PlacementAuthorityError>.Ok(
                new PlacementApplyResult<T, E>
                {
                    Changed = true,
                    Value = mutation.Value,
                    Grant = grant,
                    Sequence = request.Sequence,
                });
        }
    }

    public void CommitSequence(PlacementAuthorityGrant grant, long sequence)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
            AcceptUnlocked(grant, sequence);
    }

    public void RevokePane(PaneId paneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId.Value);
        lock (_gate)
        {
            _occupants.Remove(paneId.Value);
            _parentCapabilities.Remove(paneId.Value);
            ClearAllSequences(paneId.Value);
        }
    }

    public void RevokeOccupant(PaneId paneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId.Value);
        lock (_gate)
        {
            _occupants.Remove(paneId.Value);
            ClearSequences(paneId.Value, PlacementAuthorityRole.Occupant);
        }
    }

    public bool HasOccupant(PaneId paneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId.Value);
        lock (_gate)
            return _occupants.ContainsKey(paneId.Value);
    }

    public bool HasParentCapability(PaneId paneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId.Value);
        lock (_gate)
            return _parentCapabilities.ContainsKey(paneId.Value);
    }

    private Result<PlacementAuthorityGrant, PlacementAuthorityError> AuthorizeUnlocked(
        PlacementAuthorityRequest request,
        IOverlayPlacementFence? fence)
    {
        var fenceCheck = (fence ?? NullFence.Instance).Check(
            request.Mode,
            request.AttachClientId,
            request.PaneId.Value);
        if (!fenceCheck.IsOk)
            return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(fenceCheck.Error);

        var occupant = EmptyToNull(request.OccupantToken);
        var parent = EmptyToNull(request.ParentCapability);
        var leaseId = EmptyToNull(request.LeaseId);
        var status = EmptyToNull(request.StatusSource);

        if (status is not null && occupant is null && parent is null && leaseId is null)
            return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(
                PlacementAuthorityError.StatusCredential);

        if (occupant is not null)
        {
            if (!_occupants.TryGetValue(request.PaneId.Value, out var record)
                || !FixedEquals(record.Token, occupant))
            {
                return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(
                    PlacementAuthorityError.ForeignCredential);
            }

            return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Ok(
                new PlacementAuthorityGrant
                {
                    Role = PlacementAuthorityRole.Occupant,
                    PaneId = request.PaneId,
                    CredentialId = request.PaneId.Value,
                    SequenceRequired = true,
                });
        }

        if (parent is not null)
        {
            if (!_parentCapabilities.TryGetValue(request.PaneId.Value, out var stored)
                || !FixedEquals(stored, parent))
            {
                return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(
                    PlacementAuthorityError.ForeignCredential);
            }

            return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Ok(
                new PlacementAuthorityGrant
                {
                    Role = PlacementAuthorityRole.Parent,
                    PaneId = request.PaneId,
                    CredentialId = request.PaneId.Value,
                    SequenceRequired = true,
                });
        }

        if (leaseId is not null)
        {
            if (!LeaseOwnsHuman(request.PaneId.Value, leaseId, request.HolderId))
            {
                return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(
                    PlacementAuthorityError.ForeignCredential);
            }

            return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Ok(
                new PlacementAuthorityGrant
                {
                    Role = PlacementAuthorityRole.HumanLease,
                    PaneId = request.PaneId,
                    CredentialId = leaseId,
                    SequenceRequired = false,
                });
        }

        return Result<PlacementAuthorityGrant, PlacementAuthorityError>.Fail(
            PlacementAuthorityError.ForgedIdentity);
    }

    private bool IsStaleUnlocked(PlacementAuthorityGrant grant, long sequence)
    {
        return _sequences.TryGetValue(SequenceKey(grant), out var previous) && sequence <= previous;
    }

    private void AcceptUnlocked(PlacementAuthorityGrant grant, long sequence) =>
        _sequences[SequenceKey(grant)] = sequence;

    private void ClearSequences(string paneId, PlacementAuthorityRole role)
    {
        var prefix = paneId + "\0" + role + "\0";
        var keys = _sequences.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
            _sequences.Remove(key);
    }

    private void ClearAllSequences(string paneId)
    {
        var prefix = paneId + "\0";
        var keys = _sequences.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
            _sequences.Remove(key);
    }

    private static string SequenceKey(PlacementAuthorityGrant grant) =>
        grant.PaneId.Value + "\0" + grant.Role + "\0" + grant.CredentialId;

    private PaneId? ResolveOccupantUnlocked(string token)
    {
        PaneId? found = null;
        foreach (var (paneId, record) in _occupants)
        {
            if (FixedEquals(record.Token, token))
                found = new PaneId(paneId);
        }

        return found;
    }

    private bool LeaseOwnsParent(string paneId, string leaseId, string? holderId)
    {
        if (string.IsNullOrWhiteSpace(holderId))
            return false;
        var input = _leases.TryAuthorize(paneId, LeaseScopes.Input, leaseId, holderId);
        if (input.Status == LeaseAuthorizeStatus.Authorized)
            return true;
        var admin = _leases.TryAuthorize(paneId, LeaseScopes.Admin, leaseId, holderId);
        return admin.Status == LeaseAuthorizeStatus.Authorized;
    }

    private bool LeaseOwnsHuman(string paneId, string leaseId, string? holderId)
    {
        var input = _leases.TryAuthorize(paneId, LeaseScopes.Input, leaseId, holderId);
        if (input.Status == LeaseAuthorizeStatus.Authorized)
            return true;
        var resize = _leases.TryAuthorize(paneId, LeaseScopes.Resize, leaseId, holderId);
        if (resize.Status == LeaseAuthorizeStatus.Authorized)
            return true;
        var admin = _leases.TryAuthorize(paneId, LeaseScopes.Admin, leaseId, holderId);
        return admin.Status == LeaseAuthorizeStatus.Authorized;
    }

    private static string Mint(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool FixedEquals(string stored, string presented)
    {
        var a = Encoding.UTF8.GetBytes(stored);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record OccupantRecord(string Token, int Generation);

    private sealed class NullFence : IOverlayPlacementFence
    {
        public static NullFence Instance { get; } = new();

        public Result<bool, PlacementAuthorityError> Check(string mode, string? attachClientId)
        {
            if (string.Equals(mode, "overlay", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(attachClientId))
            {
                return Result<bool, PlacementAuthorityError>.Fail(
                    PlacementAuthorityError.MissingAttachClient);
            }

            return Result<bool, PlacementAuthorityError>.Ok(true);
        }
    }
}
