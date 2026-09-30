using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Overlay;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
// / Host-frame cells for overlay lifecycle.
/// <c>src/client/shell/composition.rs</c> blits after chrome. One
/// host-size frame. One encoder. Reveal accepts a Full ingest
/// after resize. Dirty or incomplete frames stay rejected.
/// </summary>
public sealed class HiddenPaneLifecycleHostFrameTests
{
    [Fact]
    public void Two_geometries_stamp_different_rects_with_one_encoder()
    {
        var small = PopupGeometry.TryResolve(80, 24)!;
        var large = PopupGeometry.TryResolve(100, 30)!;
        Assert.NotEqual(small.InnerCols, large.InnerCols);
        Assert.NotEqual(small.InnerRows, large.InnerRows);

        var hostA = new HostFrame();
        hostA.Resize(80, 24);
        ClientOverlayComposer.Stamp(hostA, Fill("p-a", small.InnerCols, small.InnerRows, "A"), small, ThemePalette.Catppuccin);
        Assert.Equal("A", hostA.CellAt(small.InnerCol, small.InnerRow).Text);
        Assert.NotEqual("A", hostA.CellAt(0, 0).Text);

        var hostB = new HostFrame();
        hostB.Resize(100, 30);
        ClientOverlayComposer.Stamp(hostB, Fill("p-b", large.InnerCols, large.InnerRows, "B"), large, ThemePalette.Catppuccin);
        Assert.Equal("B", hostB.CellAt(large.InnerCol, large.InnerRow).Text);

        var first = ClientOverlayComposer.EncodeOnce(hostA, new HostBlitEncoder());
        var second = ClientOverlayComposer.EncodeOnce(hostB, new HostBlitEncoder());
        Assert.True(first.Full);
        Assert.True(second.Full);
        Assert.NotEqual(Encoding.UTF8.GetString(first.Bytes), Encoding.UTF8.GetString(second.Bytes));
    }

    [Fact]
    public void Stale_queued_snapshot_after_hide_cannot_restamp()
    {
        var geo = PopupGeometry.TryResolve(80, 24)!;
        var overlay = new ClientOverlayState();
        Assert.True(overlay.ApplyServerShow("p-hidden", 4, geo));
        overlay.NoteResized();
        var ready = Fill("p-hidden", geo.InnerCols, geo.InnerRows, "N");
        Assert.True(overlay.TryAcceptReveal(ready));
        Assert.True(overlay.CanStamp(ready));

        Assert.True(overlay.ApplyServerHide("p-hidden", 5));
        Assert.False(overlay.OwnsModal);
        Assert.False(overlay.TryAcceptReveal(ready));
        Assert.False(overlay.CanStamp(ready));

        var host = new HostFrame();
        host.Resize(80, 24);
        host.Stamp(geo.InnerCol, geo.InnerRow, new AssembledCell("Z", 1, false, AssembledStyle.Default));
        Assert.Equal("Z", host.CellAt(geo.InnerCol, geo.InnerRow).Text);
        Assert.False(overlay.CanStamp(ready));
    }

    [Fact]
    public void Escape_hides_without_disposal_and_outside_click_is_swallowed()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        engine.OverlayOpen = true;
        engine.OverlayPaneId = "p-hidden";
        var hide = engine.Feed(KeyChord.Parse("esc"));
        Assert.Equal(KeyEngineEventKind.HideOverlay, hide[0].Kind);
        Assert.Equal("p-hidden", hide[0].TargetId);

        var geo = PopupGeometry.TryResolve(80, 24)!;
        var overlay = new ClientOverlayState();
        Assert.True(overlay.ApplyServerShow("p-hidden", 2, geo));
        var outside = new MouseEvent(MouseButton.Left, MouseAction.Press, 0, 0);
        Assert.True(ClientOverlayInput.SwallowOutsideClick(overlay, outside));
        var inner = new MouseEvent(
            MouseButton.Left,
            MouseAction.Press,
            geo.InnerCol,
            geo.InnerRow);
        Assert.False(ClientOverlayInput.SwallowOutsideClick(overlay, inner));
        Assert.True(overlay.OwnsModal);
    }

    [Fact]
    public void Foreign_client_event_cannot_own_or_stamp()
    {
        var live = Live("conn_b");
        Assert.False(AttachSession.ApplyOverlayPlacement(
            live,
            Placement("overlay", "p-a", "conn_a", 1)));
        Assert.False(live.Overlay.OwnsModal);

        var geo = PopupGeometry.TryResolve(80, 24)!;
        var snapshot = Fill("p-a", geo.InnerCols, geo.InnerRows, "X");
        Assert.False(live.Overlay.TryAcceptReveal(snapshot));
        Assert.False(live.Overlay.CanStamp(snapshot));
    }

    private static AttachLiveState Live(string attachClientId)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new NoopPort(), "w1", "t1", "p1", "lease-r"),
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

    private static AssembledSnapshot Fill(string paneId, int cols, int rows, string glyph)
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
            "ghostty",
            "main",
            cells,
            default,
            AssembledCursor.Default,
            OccupantGeneration: 1,
            Generation: 11,
            IngestFull: true);
    }

    private sealed class NoopPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }
}
