using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach marks <c>pane.send_keys</c> bodies that are not keyboard input
/// (mouse reports, focus reports, bracketed paste) with
/// <c>keyboard: false</c>. Only keyboard input opens the echo window on the
/// mux. Keyboard bodies have no field, which means true.
/// </summary>
public sealed class AttachKeyboardFlagTests
{
    [Fact]
    public async Task Focus_report_body_is_not_keyboard_input()
    {
        var live = Live(focusReporting: true);
        var port = new MouseRecordingPort();
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();

        await AttachSession.ForwardOuterFocusIfRequestedAsync(
            [AttachSession.OuterFocusEvent.FocusIn],
            port,
            gate,
            live,
            tty: null,
            linked,
            CancellationToken.None);

        var call = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
        Assert.False(call.Params!["keyboard"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Forwarded_mouse_report_body_for_the_live_pane_is_not_keyboard_input()
    {
        var live = Live(focusReporting: false);
        var port = new MouseRecordingPort();

        await AttachSession.ApplyMouseResultAsync(
            new MouseEngineResult(MouseCommandKind.ForwardSgr, PaneId: "p1", ForwardKeys: "\u001b[<0;1;1M"u8.ToArray()),
            live,
            port,
            tty: null,
            linked: null,
            CancellationToken.None);

        var call = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
        Assert.False(call.Params!["keyboard"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Forwarded_mouse_report_body_for_an_overlay_pane_is_not_keyboard_input()
    {
        var live = Live(focusReporting: false);
        Assert.True(live.Overlay.ApplyServerShow("ov1", 1, Geometry()));
        live.OverlayLeases = new Hypa.Cli.Attach.Overlay.TargetPaneLeasePair
        {
            PaneId = "ov1",
            InputLease = "lease-ov",
            ResizeLease = "lease-ov-r",
        };
        var port = new MouseRecordingPort();

        await AttachSession.ApplyMouseResultAsync(
            new MouseEngineResult(MouseCommandKind.ForwardSgr, PaneId: "ov1", ForwardKeys: "\u001b[<0;1;1M"u8.ToArray()),
            live,
            port,
            tty: null,
            linked: null,
            CancellationToken.None);

        var call = Assert.Single(port.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
        Assert.Equal("ov1", call.Params!["pane_id"]!.GetValue<string>());
        Assert.False(call.Params["keyboard"]!.GetValue<bool>());
    }

    [Fact]
    public void Key_bodies_carry_no_keyboard_field()
    {
        var live = Live(focusReporting: false);
        Assert.False(AttachSession.BuildPaneSendKeys("p1", [0x61], "lease").ContainsKey("keyboard"));
        Assert.False(AttachSession.OverlaySendKeysBody(live, [0x61]).ContainsKey("keyboard"));
    }

    [Fact]
    public void Paste_bytes_are_marked_and_key_bytes_are_not()
    {
        var engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));
        var events = engine.Feed("a\u001b[200~xy\u001b[201~b"u8);

        var sends = events.Where(e => e.Kind == KeyEngineEventKind.SendPaneBytes).ToList();
        Assert.Equal([false, true, false], sends.Select(e => e.Paste).ToArray());
    }

    [Fact]
    public void Key_bytes_never_go_out_marked_as_paste()
    {
        var merged = AttachSession.CoalescePaneBytes(
        [
            new KeyEngineEvent(KeyEngineEventKind.SendPaneBytes, Bytes: [0x61]),
            new KeyEngineEvent(KeyEngineEventKind.SendPaneBytes, Bytes: [0x62], Paste: true),
            new KeyEngineEvent(KeyEngineEventKind.Dispatch, Action: KeyActionId.Detach),
            new KeyEngineEvent(KeyEngineEventKind.SendPaneBytes, Bytes: [0x63], Paste: true),
            new KeyEngineEvent(KeyEngineEventKind.SendPaneBytes, Bytes: [0x64], Paste: true),
        ]);

        // Typed bytes and pasted bytes stay in separate runs (the predictor
        // treats a paste as one unpredicted payload). Adjacent paste bytes
        // merge.
        Assert.Equal(4, merged.Count);
        Assert.False(merged[0].Paste);
        Assert.Equal([0x61], merged[0].Bytes);
        Assert.True(merged[1].Paste);
        Assert.True(merged[3].Paste);
        Assert.Equal([0x63, 0x64], merged[3].Bytes);
    }

    private static PopupGeometryResult Geometry() => new()
    {
        OuterCol = 0,
        OuterRow = 0,
        OuterCols = 20,
        OuterRows = 10,
        InnerCol = 1,
        InnerRow = 1,
        InnerCols = 18,
        InnerRows = 8,
    };

    private static AttachLiveState Live(bool focusReporting)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = null!,
        };
        live.PaneId = "p1";
        live.InputLease = "lease-in";
        using var doc = JsonDocument.Parse(
            "{\"modes\":{\"focus_reporting\":" + (focusReporting ? "true" : "false") + "}}");
        live.SetPaneFrame(new AssembledSnapshot(
            "p1",
            1,
            1,
            "ghostty",
            "main",
            [],
            doc.RootElement.Clone(),
            AssembledCursor.Default));
        return live;
    }
}
