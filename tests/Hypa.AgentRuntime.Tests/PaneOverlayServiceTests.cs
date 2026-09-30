using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PaneOverlayServiceTests
{
    [Fact]
    public void Show_grants_one_owner_and_rejects_second_client_same_pane()
    {
        var (svc, pane) = HiddenPane();
        var first = svc.Show(new PaneShowOverlayRequest
        {
            PaneId = pane,
            AttachClientId = "conn_a",
            AreaCols = 80,
            AreaRows = 24,
        });
        Assert.True(first.IsOk);
        Assert.True(first.Value.Changed);
        Assert.Equal(PaneOverlayWire.Overlay, first.Value.Mode);

        var second = svc.Show(new PaneShowOverlayRequest
        {
            PaneId = pane,
            AttachClientId = "conn_b",
            AreaCols = 100,
            AreaRows = 30,
        });
        Assert.False(second.IsOk);
        Assert.Equal(PaneOverlayError.BusyCode, second.Error.Code);
        Assert.Equal("conn_a", svc.OwnerOf(pane)!.AttachClientId);
    }

    [Fact]
    public void Two_panes_may_have_separate_owners()
    {
        var (svc, a, b) = TwoHiddenPanes();
        Assert.True(svc.Show(new PaneShowOverlayRequest { PaneId = a, AttachClientId = "conn_a" }).IsOk);
        Assert.True(svc.Show(new PaneShowOverlayRequest { PaneId = b, AttachClientId = "conn_b" }).IsOk);
        Assert.Equal("conn_a", svc.OwnerOf(a)!.AttachClientId);
        Assert.Equal("conn_b", svc.OwnerOf(b)!.AttachClientId);
        Assert.Equal(2, svc.ListOwners().Count);
    }

    [Fact]
    public void Fence_check_translates_to_authority_errors()
    {
        var (svc, pane) = HiddenPane();
        var missing = svc.Check("overlay", null);
        Assert.False(missing.IsOk);
        Assert.Equal(PlacementAuthorityError.MissingAttachClient.Code, missing.Error.Code);

        Assert.True(svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).IsOk);
        var busy = svc.Check("overlay", "conn_a");
        Assert.False(busy.IsOk);
        Assert.Equal(PlacementAuthorityError.OverlayBusy.Code, busy.Error.Code);
        Assert.True(svc.Check("tiled", "conn_a").IsOk);
    }

    [Fact]
    public void Hide_and_release_fence_stale_input()
    {
        var (svc, pane) = HiddenPane();
        var shown = svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).Value;
        Assert.True(svc.AdmitInput(new PaneOverlayInputAdmit
        {
            AttachClientId = "conn_a",
            PaneId = pane,
            Generation = shown.Generation,
        }));

        Assert.True(svc.Hide(new PaneHideOverlayRequest
        {
            PaneId = pane,
            AttachClientId = "conn_a",
            Generation = shown.Generation,
        }).IsOk);

        Assert.False(svc.AdmitInput(new PaneOverlayInputAdmit
        {
            AttachClientId = "conn_a",
            PaneId = pane,
            Generation = shown.Generation,
        }));
    }

    [Fact]
    public void Same_client_same_pane_is_unchanged()
    {
        var (svc, pane) = HiddenPane();
        var first = svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).Value;
        var again = svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).Value;
        Assert.False(again.Changed);
        Assert.Equal(first.Generation, again.Generation);
    }

    [Fact]
    public void TryRevertOwner_keeps_later_client_reservation()
    {
        var (svc, a, b) = TwoHiddenPanes();
        var first = svc.Show(new PaneShowOverlayRequest { PaneId = a, AttachClientId = "conn_a" }).Value;
        var prior = svc.OwnerOf(a)!;
        Assert.True(svc.Hide(new PaneHideOverlayRequest
        {
            PaneId = a,
            AttachClientId = "conn_a",
            Generation = first.Generation,
        }).IsOk);

        var later = svc.Show(new PaneShowOverlayRequest { PaneId = b, AttachClientId = "conn_a" }).Value;
        Assert.False(svc.TryRevertOwner("conn_a", prior, a, prior.Generation, operationReleased: true));
        Assert.Equal(b, svc.TryGet("conn_a")!.PaneId);
        Assert.Equal(later.Generation, svc.TryGet("conn_a")!.Generation);
        Assert.Null(svc.OwnerOf(a));
    }

    [Fact]
    public void TryRevertOwner_releases_failed_show_when_still_owned()
    {
        var (svc, pane) = HiddenPane();
        var shown = svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).Value;
        Assert.True(svc.TryRevertOwner(
            "conn_a",
            priorOwner: null,
            pane,
            shown.Generation,
            operationReleased: false));
        Assert.False(svc.HasReservation("conn_a"));
        Assert.Null(svc.OwnerOf(pane));
    }

    [Fact]
    public void TryRevertOwner_restores_prior_when_hide_has_no_later_owner()
    {
        var (svc, pane) = HiddenPane();
        var shown = svc.Show(new PaneShowOverlayRequest { PaneId = pane, AttachClientId = "conn_a" }).Value;
        var prior = svc.OwnerOf(pane)!;
        Assert.True(svc.Hide(new PaneHideOverlayRequest
        {
            PaneId = pane,
            AttachClientId = "conn_a",
            Generation = shown.Generation,
        }).IsOk);
        Assert.True(svc.TryRevertOwner("conn_a", prior, pane, prior.Generation, operationReleased: true));
        Assert.Equal(prior.Generation, svc.OwnerOf(pane)!.Generation);
        Assert.Equal("conn_a", svc.OwnerOf(pane)!.AttachClientId);
    }

    private static (PaneOverlayService Svc, PaneId Pane) HiddenPane()
    {
        var state = new AppState(SessionId.New("ov"));
        var ws = state.CreateWorkspace("/tmp", "ws");
        var tab = state.GetTab(ws.FocusedTabId!.Value)!;
        var pane = new PaneId("p-hidden");
        state.RegisterPane(new PaneState
        {
            Id = pane,
            TabId = tab.Id,
            WorkspaceId = ws.Id,
            Placement = PanePlacement.Hidden,
            Cols = 80,
            Rows = 24,
            IsAlive = true,
        });
        return (new PaneOverlayService(state), pane);
    }

    private static (PaneOverlayService Svc, PaneId A, PaneId B) TwoHiddenPanes()
    {
        var state = new AppState(SessionId.New("ov2"));
        var ws = state.CreateWorkspace("/tmp", "ws");
        var tab = state.GetTab(ws.FocusedTabId!.Value)!;
        var a = new PaneId("p-a");
        var b = new PaneId("p-b");
        state.RegisterPane(new PaneState
        {
            Id = a,
            TabId = tab.Id,
            WorkspaceId = ws.Id,
            Placement = PanePlacement.Hidden,
            IsAlive = true,
        });
        state.RegisterPane(new PaneState
        {
            Id = b,
            TabId = tab.Id,
            WorkspaceId = ws.Id,
            Placement = PanePlacement.Hidden,
            IsAlive = true,
        });
        return (new PaneOverlayService(state), a, b);
    }
}
