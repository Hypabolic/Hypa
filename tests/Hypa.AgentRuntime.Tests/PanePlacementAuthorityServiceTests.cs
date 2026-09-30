using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PanePlacementAuthorityServiceTests
{
    [Fact]
    public void Create_parent_lease_requires_holder_and_rejects_resize_scope()
    {
        var leases = new InMemoryLeaseRegistry();
        var svc = new PanePlacementAuthorityService(leases);
        var parent = new PaneId("p_parent");
        var input = leases.Claim(parent.Value, LeaseScopes.Input, "conn_owner", false, null, 30_000);
        var resize = leases.Claim(parent.Value, LeaseScopes.Resize, "conn_owner", false, null, 30_000);
        Assert.Equal(LeaseOutcomes.Granted, input.Outcome);
        Assert.Equal(LeaseOutcomes.Granted, resize.Outcome);

        var missingHolder = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = parent.Value,
            LeaseId = input.Lease!.LeaseId,
        });
        Assert.False(missingHolder.IsOk);
        Assert.Equal(PlacementAuthorityError.ForeignCredential.Code, missingHolder.Error.Code);

        var foreign = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = parent.Value,
            LeaseId = input.Lease.LeaseId,
            HolderId = "conn_foreign",
        });
        Assert.False(foreign.IsOk);
        Assert.Equal(PlacementAuthorityError.ForeignCredential.Code, foreign.Error.Code);

        var resizeProof = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = parent.Value,
            LeaseId = resize.Lease!.LeaseId,
            HolderId = "conn_owner",
        });
        Assert.False(resizeProof.IsOk);
        Assert.Equal(PlacementAuthorityError.ForeignCredential.Code, resizeProof.Error.Code);

        var owner = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = parent.Value,
            LeaseId = input.Lease.LeaseId,
            HolderId = "conn_owner",
        });
        Assert.True(owner.IsOk);
        Assert.Equal(parent, owner.Value);
    }

    [Fact]
    public void Occupant_token_proves_parent_without_holder()
    {
        var svc = new PanePlacementAuthorityService(new InMemoryLeaseRegistry());
        var parent = new PaneId("p_occ");
        var token = svc.IssueOccupant(parent, 1);

        var ok = svc.ValidateCreateParent(new PlacementParentProof { OccupantToken = token });
        Assert.True(ok.IsOk);
        Assert.Equal(parent, ok.Value);

        var mismatch = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = "p_other",
            OccupantToken = token,
        });
        Assert.False(mismatch.IsOk);
        Assert.Equal(PlacementAuthorityError.UnprovenParent.Code, mismatch.Error.Code);
    }

    [Fact]
    public void Status_source_does_not_prove_parent()
    {
        var svc = new PanePlacementAuthorityService(new InMemoryLeaseRegistry());
        var result = svc.ValidateCreateParent(new PlacementParentProof
        {
            ClaimedParentPaneId = "p1",
            StatusSource = "pane.report_agent",
        });
        Assert.False(result.IsOk);
        Assert.Equal(PlacementAuthorityError.StatusCredential.Code, result.Error.Code);
    }

    [Fact]
    public void Failed_mutate_does_not_consume_seq_and_human_resize_lease_still_authorizes()
    {
        var leases = new InMemoryLeaseRegistry();
        var svc = new PanePlacementAuthorityService(leases);
        var pane = new PaneId("p_child");
        var token = svc.IssueOccupant(pane, 1);
        var resize = leases.Claim(pane.Value, LeaseScopes.Resize, "conn_h", false, null, 30_000);

        var failed = svc.Apply<int, string>(
            new PlacementAuthorityRequest
            {
                PaneId = pane,
                OccupantToken = token,
                Sequence = 4,
            },
            () => Result<int, string>.Fail("graph"));
        Assert.True(failed.IsOk);
        Assert.False(failed.Value.Changed);
        Assert.Equal("graph", failed.Value.MutationError);

        var retry = svc.Apply(
            new PlacementAuthorityRequest
            {
                PaneId = pane,
                OccupantToken = token,
                Sequence = 4,
            },
            () => Result<int, string>.Ok(9));
        Assert.True(retry.IsOk);
        Assert.True(retry.Value.Changed);
        Assert.Equal(9, retry.Value.Value);

        var human = svc.Authorize(new PlacementAuthorityRequest
        {
            PaneId = pane,
            LeaseId = resize.Lease!.LeaseId,
            HolderId = "conn_h",
        });
        Assert.True(human.IsOk);
        Assert.Equal(PlacementAuthorityRole.HumanLease, human.Value.Role);
        Assert.False(human.Value.SequenceRequired);
    }

    [Fact]
    public void Stale_seq_is_ignored_and_revoke_drops_secrets()
    {
        var svc = new PanePlacementAuthorityService(new InMemoryLeaseRegistry());
        var pane = new PaneId("p_seq");
        var token = svc.IssueOccupant(pane, 1);
        Assert.True(svc.Apply(
            new PlacementAuthorityRequest { PaneId = pane, OccupantToken = token, Sequence = 2 },
            () => Result<int, string>.Ok(1)).Value.Changed);

        var deferred = svc.Apply(
            new PlacementAuthorityRequest { PaneId = pane, OccupantToken = token, Sequence = 3 },
            () => Result<int, string>.Ok(3),
            commitSequence: false);
        Assert.True(deferred.Value.Changed);
        Assert.NotNull(deferred.Value.Grant);

        var retryDeferred = svc.Apply(
            new PlacementAuthorityRequest { PaneId = pane, OccupantToken = token, Sequence = 3 },
            () => Result<int, string>.Ok(4),
            commitSequence: false);
        Assert.True(retryDeferred.Value.Changed);
        Assert.Equal(4, retryDeferred.Value.Value);

        svc.CommitSequence(deferred.Value.Grant!, 3);
        var staleAfterCommit = svc.Apply(
            new PlacementAuthorityRequest { PaneId = pane, OccupantToken = token, Sequence = 3 },
            () => Result<int, string>.Ok(5));
        Assert.True(staleAfterCommit.Value.Stale);
        Assert.False(staleAfterCommit.Value.Changed);

        var stale = svc.Apply(
            new PlacementAuthorityRequest { PaneId = pane, OccupantToken = token, Sequence = 2 },
            () => Result<int, string>.Ok(2));
        Assert.True(stale.Value.Stale);
        Assert.False(stale.Value.Changed);

        svc.RevokeOccupant(pane);
        Assert.False(svc.HasOccupant(pane));
        var after = svc.Authorize(new PlacementAuthorityRequest
        {
            PaneId = pane,
            OccupantToken = token,
            Sequence = 3,
        });
        Assert.False(after.IsOk);
        Assert.Equal(PlacementAuthorityError.ForeignCredential.Code, after.Error.Code);
    }
}
