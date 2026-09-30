using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Overlay;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.UnitTests.Cli;

public class ClientOverlayTests
{
    [Fact]
    public void Stamp_uses_one_host_frame_and_one_encoder()
    {
        var geometry = PopupGeometry.TryResolve(80, 24)!;
        var host = new HostFrame();
        host.Resize(80, 24);
        host.Stamp(0, 0, new AssembledCell("T", 1, false, AssembledStyle.Default));

        var cells = new AssembledCell[geometry.InnerRows][];
        for (var r = 0; r < geometry.InnerRows; r++)
        {
            cells[r] = new AssembledCell[geometry.InnerCols];
            for (var c = 0; c < geometry.InnerCols; c++)
                cells[r][c] = new AssembledCell("X", 1, false, AssembledStyle.Default);
        }

        var snapshot = new AssembledSnapshot(
            "p-hidden",
            geometry.InnerCols,
            geometry.InnerRows,
            "basic",
            "main",
            cells,
            default,
            AssembledCursor.Default);
        ClientOverlayComposer.Stamp(host, snapshot, geometry, ThemePalette.Catppuccin);

        Assert.Equal("T", host.CellAt(0, 0).Text);
        Assert.Contains(ClientOverlayComposer.OverlayTitle.Trim(), TitleRow(host, geometry));
        Assert.Equal("X", host.CellAt(geometry.InnerCol, geometry.InnerRow).Text);

        var first = ClientOverlayComposer.EncodeOnce(host, new HostBlitEncoder());
        var second = ClientOverlayComposer.EncodeOnce(host, new HostBlitEncoder());
        Assert.True(first.Full);
        Assert.True(second.Full);
        Assert.Equal(Encoding.UTF8.GetString(first.Bytes), Encoding.UTF8.GetString(second.Bytes));
    }

    [Fact]
    public void Stamp_title_uses_the_pane_label()
    {
        var geometry = PopupGeometry.TryResolve(80, 24)!;
        var host = new HostFrame();
        host.Resize(80, 24);
        var cells = new AssembledCell[geometry.InnerRows][];
        for (var r = 0; r < geometry.InnerRows; r++)
        {
            cells[r] = new AssembledCell[geometry.InnerCols];
            for (var c = 0; c < geometry.InnerCols; c++)
                cells[r][c] = new AssembledCell(" ", 1, false, AssembledStyle.Default);
        }

        var snapshot = new AssembledSnapshot(
            "p-ticker",
            geometry.InnerCols,
            geometry.InnerRows,
            "basic",
            "main",
            cells,
            default,
            AssembledCursor.Default);
        ClientOverlayComposer.Stamp(host, snapshot, geometry, ThemePalette.Catppuccin, "ticker");

        var title = TitleRow(host, geometry);
        Assert.Contains("ticker", title);
        Assert.DoesNotContain("Agent", title);
    }

    [Fact]
    public void Content_origin_is_translated_to_host_and_maps_mouse()
    {
        var content = new CellRect(18, 2, 62, 21);
        var geometry = ClientOverlayInput.ResolveForContent(content)!;
        Assert.True(geometry.OuterCol >= content.Col);
        Assert.True(geometry.OuterRow >= content.Row);
        Assert.True(geometry.OuterCol + geometry.OuterCols <= content.EndCol);
        Assert.True(geometry.OuterRow + geometry.OuterRows <= content.EndRow);

        var overlay = new ClientOverlayState();
        Assert.True(overlay.ApplyServerShow("p-hidden", 3, geometry));
        var outside = new MouseEvent(MouseButton.Left, MouseAction.Press, 0, 0);
        Assert.True(ClientOverlayInput.SwallowOutsideClick(overlay, outside));
        var inner = new MouseEvent(
            MouseButton.Left,
            MouseAction.Press,
            geometry.InnerCol,
            geometry.InnerRow);
        Assert.False(ClientOverlayInput.SwallowOutsideClick(overlay, inner));
        var forward = ClientOverlayInput.ForwardInner(overlay, inner);
        Assert.Equal("p-hidden", forward!.PaneId);
        Assert.NotNull(forward.ForwardKeys);
        Assert.NotEmpty(forward.ForwardKeys);
    }

    [Fact]
    public void Server_hide_is_monotonic_and_ignores_stale_show()
    {
        var live = Live("conn_owner");
        Assert.True(AttachSession.ApplyOverlayPlacement(live, Placement("overlay", "p-hidden", "conn_owner", 3)));
        Assert.True(live.Overlay.OwnsModal);
        Assert.True(AttachSession.ApplyOverlayPlacement(live, Placement("hidden", "p-hidden", "conn_owner", 4)));
        Assert.False(live.Overlay.OwnsModal);
        Assert.False(live.Engine.OverlayOpen);
        Assert.False(AttachSession.ApplyOverlayPlacement(live, Placement("overlay", "p-hidden", "conn_owner", 3)));
        Assert.False(live.Overlay.OwnsModal);
        Assert.False(AttachSession.ApplyOverlayPlacement(live, Placement("hidden", "p-other", "conn_owner", 5)));
    }

    [Fact]
    public void Hide_without_ownership_retains_generation_and_keeps_newer_fence()
    {
        var overlay = new ClientOverlayState();
        var geo = PopupGeometry.TryResolve(80, 24)!;
        Assert.False(overlay.ApplyServerHide("p-hidden", 4));
        Assert.False(overlay.OwnsModal);
        Assert.Equal(4, overlay.AppliedGeneration);
        Assert.False(overlay.ApplyServerShow("p-hidden", 4, geo));
        Assert.False(overlay.ApplyServerHide("p-hidden", 3));
        Assert.Equal(4, overlay.AppliedGeneration);
        Assert.True(overlay.ApplyServerShow("p-hidden", 5, geo));
        Assert.True(overlay.OwnsModal);
        Assert.Equal(5, overlay.AppliedGeneration);
        Assert.False(overlay.ApplyServerHide("p-other", 6));
        Assert.True(overlay.OwnsModal);
        Assert.Equal(5, overlay.AppliedGeneration);
    }

    [Fact]
    public void Hide_release_codes_exclude_persist_failure()
    {
        Assert.True(AttachSession.IsOverlayHideReleased(
            new ControlPlaneException(ProtocolErrorCodes.Fenced, "stale overlay generation")));
        Assert.True(AttachSession.IsOverlayHideReleased(
            new ControlPlaneException(ProtocolErrorCodes.NotFound, "Pane not found: p-hidden")));
        Assert.True(AttachSession.IsOverlayHideReleased(
            new ControlPlaneException(ProtocolErrorCodes.InvalidState, "ui_busy")));
        Assert.False(AttachSession.IsOverlayHideReleased(
            new ControlPlaneException(ProtocolErrorCodes.PersistenceUnavailable, "injected persist fail")));
        Assert.False(AttachSession.IsOverlayHideReleased(
            new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "overlay_generation is required")));
    }

    [Fact]
    public void Foreign_event_and_old_cached_frame_do_not_reveal()
    {
        var live = Live("conn_b");
        Assert.False(AttachSession.ApplyOverlayPlacement(
            live,
            Placement("overlay", "p-a", "conn_a", 1)));
        Assert.False(live.Overlay.OwnsModal);

        var geo = PopupGeometry.TryResolve(80, 24)!;
        Assert.True(live.Overlay.ApplyServerShow("p-hidden", 2, geo));
        live.SetPaneFrame(FillSnapshot("p-hidden", 8, 4, "O"));
        Assert.False(live.Overlay.TryAcceptReveal(live.TryGetPaneFrame("p-hidden", out var old) ? old! : FillSnapshot("p-hidden", 8, 4, "O")));
        live.Overlay.NoteResized();
        var wrong = FillSnapshot("p-hidden", 8, 4, "O");
        Assert.False(live.Overlay.TryAcceptReveal(wrong));
        var ready = FillSnapshot("p-hidden", geo.InnerCols, geo.InnerRows, "N");
        Assert.True(live.Overlay.TryAcceptReveal(ready));
        Assert.True(live.Overlay.CanStamp(ready));
    }

    [Fact]
    public void Escape_hides_other_keys_go_to_pane()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        engine.OverlayOpen = true;
        engine.OverlayPaneId = "p-hidden";
        var hide = engine.Feed(KeyChord.Parse("esc"));
        Assert.Equal(KeyEngineEventKind.HideOverlay, hide[0].Kind);
        Assert.Equal("p-hidden", hide[0].TargetId);

        engine.OverlayOpen = true;
        engine.OverlayPaneId = "p-hidden";
        var keys = engine.Feed(KeyChord.Parse("a"));
        Assert.Equal(KeyEngineEventKind.SendPaneBytes, keys[0].Kind);
        Assert.Equal("p-hidden", keys[0].TargetId);
        Assert.Equal("overlay", engine.ClientModeToken);
    }

    [Fact]
    public void Hide_reshow_rejects_cached_frame_until_new_full()
    {
        var geo = PopupGeometry.TryResolve(80, 24)!;
        var overlay = new ClientOverlayState();
        Assert.True(overlay.ApplyServerShow("p-hidden", 3, geo));
        overlay.NoteResized();
        var ready = FillSnapshot("p-hidden", geo.InnerCols, geo.InnerRows, "N");
        Assert.True(overlay.TryAcceptReveal(ready));
        Assert.True(overlay.ApplyServerHide("p-hidden", 4));
        Assert.False(overlay.ApplyServerShow("p-hidden", 3, geo));
        Assert.True(overlay.ApplyServerShow("p-hidden", 5, geo));
        Assert.False(overlay.TryAcceptReveal(ready));
        overlay.NoteResized(ready.Generation);
        Assert.False(overlay.TryAcceptReveal(ready));
        var next = FillSnapshot("p-hidden", geo.InnerCols, geo.InnerRows, "N") with { Generation = ready.Generation + 1 };
        Assert.True(overlay.TryAcceptReveal(next));
    }

    [Fact]
    public void Dirty_patch_does_not_reveal_after_resize()
    {
        var geo = PopupGeometry.TryResolve(80, 24)!;
        var overlay = new ClientOverlayState();
        Assert.True(overlay.ApplyServerShow("p-hidden", 2, geo));
        overlay.NoteResized();
        var dirty = FillSnapshot("p-hidden", geo.InnerCols, geo.InnerRows, "D") with { IngestFull = false };
        Assert.False(overlay.TryAcceptReveal(dirty));
        var ready = FillSnapshot("p-hidden", geo.InnerCols, geo.InnerRows, "F");
        Assert.True(overlay.TryAcceptReveal(ready));
    }

    [Fact]
    public void New_connection_resets_server_generation_fence()
    {
        var live = Live("conn_old");
        Assert.True(AttachSession.ApplyOverlayPlacement(live, Placement("overlay", "p-hidden", "conn_old", 10)));
        live.Overlay.NoteRecoverableCancel("p-hidden", 10);
        AttachSession.ClearLocalOverlay(live, resetEventFence: true);
        Assert.False(live.Overlay.HasRecoverableCancel);
        live.AttachClientId = "conn_new";
        Assert.True(AttachSession.ApplyOverlayPlacement(live, Placement("overlay", "p-hidden", "conn_new", 1)));
        Assert.True(live.Overlay.OwnsModal);
        Assert.False(AttachSession.ApplyOverlayPlacement(live, Placement("overlay", "p-hidden", "conn_old", 11)));
        Assert.True(live.Overlay.OwnsModal);
        Assert.False(AttachSession.ApplyOverlayPlacement(live, Placement("hidden", "p-hidden", "conn_old", 11)));
        Assert.True(live.Overlay.OwnsModal);
        Assert.Equal(1, live.Overlay.AppliedGeneration);
    }

    [Fact]
    public async Task Mouse_menu_publishes_client_mode()
    {
        var port = new MouseRecordingPort();
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            AttachClientId = "conn_owner",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
        };
        using var linked = new CancellationTokenSource();
        await AttachSession.ApplyMouseResultAsync(
            new MouseEngineResult(
                MouseCommandKind.OpenMenu,
                Menu: ContextMenuModel.ForGlobal(0, 0, 80, 24)),
            live,
            port,
            tty: null,
            linked,
            CancellationToken.None);
        Assert.Contains(port.Calls, c => c.Method == ProtocolMethods.UiClientMode);
        Assert.Equal("globalmenu", live.Engine.ClientModeToken);
    }

    [Fact]
    public async Task Show_modal_from_menu_publishes_terminal_before_overlay()
    {
        var port = new MouseRecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (string.Equals(method, ProtocolMethods.RuntimeLeaseClaim, StringComparison.Ordinal))
                {
                    var scope = parameters?["scope"]?.GetValue<string>() ?? "resize";
                    return JsonDocument.Parse(new JsonObject
                    {
                        ["lease_id"] = "lease-" + scope,
                        ["outcome"] = "granted",
                    }.ToJsonString()).RootElement.Clone();
                }

                return JsonDocument.Parse("""{"ok":true}""").RootElement.Clone();
            },
        };
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            AttachClientId = "conn_owner",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            LastSnapshot = JsonDocument.Parse(
                """{"panes":[{"pane_id":"p-hidden","tab_id":"t1","workspace_id":"w1","hidden":true,"placement":"hidden"}]}""")
                .RootElement.Clone(),
        };
        live.Engine.EnterContextMenu();
        live.MouseMenu = HiddenPaneMenuModel.ForPane("p-hidden", 0, 1, 80, 24, hidden: true, overlayVisible: false);
        using var linked = new CancellationTokenSource();
        await AttachSession.ApplyMouseResultAsync(
            new MouseEngineResult(
                MouseCommandKind.ApplyMenu,
                PaneId: "p-hidden",
                Menu: live.MouseMenu,
                MenuItem: new ContextMenuItem(HiddenPaneActions.ShowModal, "Show modal")),
            live,
            port,
            tty: null,
            linked,
            CancellationToken.None);

        var modeBeforeShow = port.Calls
            .TakeWhile(c => c.Method != ProtocolMethods.PaneShow)
            .LastOrDefault(c => c.Method == ProtocolMethods.UiClientMode);
        Assert.Equal(ProtocolMethods.UiClientMode, modeBeforeShow.Method);
        Assert.Equal("terminal", modeBeforeShow.Params?["client_mode"]?.GetValue<string>());
        var show = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.PaneShow);
        Assert.Equal("overlay", show.Params?["mode"]?.GetValue<string>());
        Assert.Equal("p-hidden", show.Params?["pane_id"]?.GetValue<string>());
        Assert.True(
            port.Calls.FindIndex(c => c.Method == ProtocolMethods.UiClientMode)
            < port.Calls.FindIndex(c => c.Method == ProtocolMethods.PaneShow));
        Assert.DoesNotContain(
            port.Calls.Skip(port.Calls.FindIndex(c => c.Method == ProtocolMethods.PaneShow) + 1),
            c => c.Method == ProtocolMethods.RuntimeLeaseRelease);
    }

    [Fact]
    public void Overlay_send_keys_body_uses_overlay_lease()
    {
        var live = Live("conn_owner");
        live.InputLease = "tiled-in";
        live.OverlayLeases = new TargetPaneLeasePair
        {
            PaneId = "p-hidden",
            InputLease = "overlay-in",
            ResizeLease = "overlay-r",
        };
        Assert.True(live.Overlay.ApplyServerShow("p-hidden", 9, PopupGeometry.TryResolve(80, 24)!));
        var body = AttachSession.OverlaySendKeysBody(live, "x"u8.ToArray());
        Assert.Equal("p-hidden", body["pane_id"]!.GetValue<string>());
        Assert.Equal("overlay-in", body["lease_id"]!.GetValue<string>());
        Assert.Equal(9, body["overlay_generation"]!.GetValue<long>());
    }

    private static AttachLiveState Live(string attachClientId)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new MouseRecordingPort(), "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            AttachClientId = attachClientId,
            InputLease = "lease-in",
            ResizeLease = "lease-r",
        };
    }

    private static JsonElement Placement(string mode, string paneId, string client, long generation) =>
        JsonDocument.Parse(new JsonObject
        {
            ["mode"] = mode,
            ["pane_id"] = paneId,
            ["attach_client_id"] = client,
            ["overlay_generation"] = generation,
        }.ToJsonString()).RootElement.Clone();

    private static AssembledSnapshot FillSnapshot(string paneId, int cols, int rows, string glyph)
    {
        var cells = new AssembledCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new AssembledCell[cols];
            for (var c = 0; c < cols; c++)
                cells[r][c] = new AssembledCell(glyph, 1, false, AssembledStyle.Default);
        }

        return new AssembledSnapshot(
            paneId,
            cols,
            rows,
            "basic",
            "main",
            cells,
            default,
            AssembledCursor.Default,
            OccupantGeneration: 1,
            Generation: 11,
            IngestFull: true);
    }

    private static string TitleRow(HostFrame host, PopupGeometryResult geometry)
    {
        var chars = new char[geometry.OuterCols];
        for (var i = 0; i < geometry.OuterCols; i++)
        {
            var text = host.CellAt(geometry.OuterCol + i, geometry.OuterRow).Text;
            chars[i] = text.Length > 0 ? text[0] : ' ';
        }

        return new string(chars);
    }
}
