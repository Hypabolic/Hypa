using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReleaseAttachSurfaceTests
{
    [Fact]
    public void MuxRelease_PaneAndWorkspaceMenusOmitTransfer()
    {
        var paneGeo = MouseTestGeom.Split();
        var paneCell = paneGeo.Panes[0].Content;
        var paneEngine = new MouseEngine();
        var paneOpen = paneEngine.Feed(
            new MouseEvent(MouseButton.Right, MouseAction.Press, paneCell.Col, paneCell.Row),
            new MouseFeedContext(paneGeo, RightClickPolicy: "hypa"));
        Assert.Equal(MouseCommandKind.OpenMenu, paneOpen[0].Kind);
        Assert.DoesNotContain(paneOpen[0].Menu!.Items, i => i.Id == ContextMenuModel.Transfer);

        var rows = new SidebarStubRow[] { new(SidebarStubKind.Workspace, "w2", "space", 0) };
        var wsGeo = MouseTestGeom.Split(sidebarOpen: true, rows: rows);
        var ws = wsGeo.SidebarRows.Single(h => h.Kind is SidebarStubKind.Workspace);
        var wsEngine = new MouseEngine();
        var wsOpen = wsEngine.Feed(
            new MouseEvent(MouseButton.Right, MouseAction.Press, ws.Rect.Col, ws.Rect.Row),
            new MouseFeedContext(wsGeo));
        Assert.Equal(["rename", "close"], wsOpen[0].Menu!.Items.Select(i => i.Id));

        var paneMenu = ContextMenuModel.ForPane("p1", 2, 2, 80, 24);
        Assert.DoesNotContain(paneMenu.Items, i => i.Id == ContextMenuModel.Transfer);
        var wsMenu = ContextMenuModel.ForWorkspace("w2", 2, 2, 80, 24);
        Assert.Equal(["rename", "close"], wsMenu.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task MuxRelease_TransferDispatchDoesNotEnterPicker()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);
        var item = new ContextMenuItem(ContextMenuModel.Transfer, ContextMenuModel.TransferLabel);
        await AttachSession.ApplyContextMenuItemAsync(
            item,
            new MouseEngineResult(MouseCommandKind.ApplyMenu, MenuItem: item, PaneId: "p1"),
            live,
            port,
            tty: null,
            CancellationToken.None);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
        Assert.Null(live.PendingTransfer);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task MuxRelease_CubesConnectDispatchIsInert()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        var connect = new ContextMenuItem(ContextMenuModel.Connect, "Connect");
        await AttachSession.ApplyContextMenuItemAsync(
            connect,
            new MouseEngineResult(
                MouseCommandKind.ApplyMenu,
                MenuItem: connect,
                PlacementId: "plc_x"),
            live,
            port,
            tty: null,
            CancellationToken.None);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);

        var move = new ContextMenuItem(ContextMenuModel.MoveWorkPrefix + "plc_x", "Move Work");
        await AttachSession.ApplyContextMenuItemAsync(
            move,
            new MouseEngineResult(
                MouseCommandKind.ApplyMenu,
                MenuItem: move,
                PlacementId: "plc_x"),
            live,
            port,
            tty: null,
            CancellationToken.None);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
        Assert.Null(live.PendingMoveWork);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public async Task MuxRelease_AttachDoesNotOpenPlacementCatalog()
    {
        var state = Path.Combine(Path.GetTempPath(), "hypa-mux-rel-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", state);
        try
        {
            var catalog = new CountingCatalog();
            var port = MouseTestGeom.ApplyPort();
            var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
            live.CubeCatalog = catalog;
            Assert.False(live.Release.ContinuityEnabled);
            await AttachSession.RefreshChromeAsync(port, live, tty: null, CancellationToken.None);
            Assert.Equal(1, catalog.Calls);
            Assert.Empty(live.Cubes);
            Assert.Equal(SidebarCubeCatalogState.Ready, live.CubesState);
            Assert.False(Directory.Exists(Path.Combine(state, "hypa", "placements")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", previous);
            try { Directory.Delete(state, recursive: true); }
            catch { /* cleanup */ }
        }
    }

    [Fact]
    public void MuxRelease_AttachComposesWithProductCapability()
    {
        var services = AttachHostEntry.CreateServices();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<MuxAttachService>());
        Assert.Null(provider.GetService<IMoveWorkMenu>());
        Assert.NotNull(provider.GetService<ICubesConnectRetargeter>());
        Assert.NotNull(provider.GetService<ICubesConnectEndpointResolver>());
        Assert.NotNull(provider.GetService<ISidebarCubeCatalogSource>());
        var reach = provider.GetRequiredService<IHostReachCatalog>();
        var session = Assert.IsType<AttachSession>(provider.GetRequiredService<IMuxAttachDriver>());
        Assert.Same(reach, session.HostReach);
        Assert.Null(provider.GetService<IMoveWorkDestExecutorFactory>());
        Assert.False(provider.GetRequiredService<MuxReleaseCapability>().ContinuityEnabled);
    }

    [Fact]
    public async Task MuxRelease_LocalReconnectRemainsOperational()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);

        var pair = ChannelFramedSession.Pair("plc_rel001");
        await using var client = pair.Client;
        await using var mux = pair.Mux;
        var service = new AttachReconnectService();
        var first = IssueJoin("rel001aa", "plc_rel001");
        var attached = service.Attach(first);
        Assert.True(attached.Ok, attached.Detail);
        Assert.True(service.NoteObserved(
            StreamFrame.Binary(StreamDirection.MuxToClient, 1, 4, "grid"u8.ToArray())).Ok);
        Assert.True(service.Stall(AttachReconnectLimits.StallWindow).Ok);
        var request = new AttachReconnectRequest
        {
            PreviousAttemptId = attached.Value,
            AttemptId = service.MintAttemptId(),
            Capability = IssueJoin("rel001bb", "plc_rel001"),
            LastReceived = service.LastReceived,
        };
        Assert.Contains("\"type\":\"attach.reconnect\"", AttachReconnectCodec.Write(request), StringComparison.Ordinal);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(
            () => FramedAttachReconnect.ServeAsync(mux, service, cts.Token).AsTask(),
            cts.Token);
        var offered = await FramedAttachReconnect.RequestAsync(client, request, cts.Token);
        var served = await serve;
        Assert.True(served.Ok, served.Detail);
        Assert.True(offered.Ok, offered.Detail);
        Assert.NotNull(client);
    }

    private static JoinCapability IssueJoin(string nonce, string placement)
    {
        Assert.True(JoinNonce.TryParse(nonce, out var parsedNonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var device));
        Assert.True(PlacementId.TryParse(placement, out var parsedPlacement));
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            parsedPlacement,
            device,
            JoinRole.Client,
            parsedNonce,
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow);
        Assert.True(issued.Ok, issued.Detail);
        return issued.Value!;
    }

    private sealed class CountingCatalog : ISidebarCubeCatalogSource
    {
        public int Calls { get; private set; }

        public ValueTask<SidebarCubeCatalogLoad> LoadAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(SidebarCubeCatalogLoad.Ready([]));
        }
    }
}
