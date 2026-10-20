using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SidebarLiveEventTests
{
    [Fact]
    public async Task Agent_status_event_paints_the_sidebar_row()
    {
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = StatusEvent("idle", "claude");

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement,
            new ScriptPort(),
            new SnapshotAssembler(),
            tty,
            live,
            CancellationToken.None);

        Assert.Contains(live.SidebarRows, row => row.Id == "p1" && row.Kind == SidebarStubKind.Agent);
        Assert.Contains("claude", HostText(live.Host), StringComparison.Ordinal);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PaneAgentStatusChanged
            && record.Outcome == ProcessLogEvents.OutcomePainted);
    }

    [Fact]
    public async Task Agent_exit_removes_the_sidebar_row()
    {
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var started = StatusEvent("idle", "claude");
        using var exited = StatusEvent("unknown", agent: null);

        await AttachSession.HandleRenderEventAsync(
            started.RootElement, new ScriptPort(), new SnapshotAssembler(), tty, live, CancellationToken.None);
        Assert.Contains("claude", HostText(live.Host), StringComparison.Ordinal);

        await AttachSession.HandleRenderEventAsync(
            exited.RootElement, new ScriptPort(), new SnapshotAssembler(), tty, live, CancellationToken.None);

        Assert.DoesNotContain(live.SidebarRows, row => row.Id == "p1" && row.Kind == SidebarStubKind.Agent);
        Assert.DoesNotContain("claude", HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pane_close_event_updates_the_layout()
    {
        var port = new ScriptPort { Handler = SplitThenClosed };
        var live = Live();
        live.ChromeSeed = new ChromeComputeSeed(SplitRoot(), false, null, "p1", 80, 24);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SplitRoot(), false, null, "p1");
        Assert.Contains(live.Chrome.Panes, pane => pane.PaneId == "p2");
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t1",
                ["state"] = "closed",
            });

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);

        Assert.NotNull(live.Chrome);
        Assert.Contains(live.Chrome.Panes, pane => pane.PaneId == "p1");
        Assert.DoesNotContain(live.Chrome.Panes, pane => pane.PaneId == "p2");
        Assert.Equal("t1", port.ExportedTab);
    }

    [Fact]
    public async Task Control_batch_presents_a_tiled_show()
    {
        var port = new ScriptPort { Handler = FocusedSecondTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["tab_id"] = "t2",
                ["pane_id"] = "p2",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["mode"] = "tiled",
            });

        await AttachSession.ApplyControlEventsAsync(
            [ev.RootElement], live, port, tty, CancellationToken.None);

        Assert.Equal("t2", live.TabId);
        Assert.Equal("p2", live.PaneId);
        Assert.Equal("t2", port.ExportedTab);
        Assert.NotNull(live.Chrome);
        Assert.Contains(live.Chrome.Panes, pane => pane.PaneId == "p2");
        Assert.DoesNotContain(live.Chrome.Panes, pane => pane.PaneId == "p1");
    }

    [Fact]
    public async Task Hidden_pane_lifecycle_adds_the_sidebar_row()
    {
        var port = new ScriptPort { Handler = HiddenChildSnapshot };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject
            {
                ["pane_id"] = "h1",
                ["tab_id"] = "t1",
                ["state"] = "running",
            });

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);

        Assert.Contains(live.SidebarRows, row => row.Id == "h1");
        Assert.Contains("claude", HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_log_records_control_and_lifecycle_and_skips_render()
    {
        var sink = new CapturingProcessLogSink();
        var live = Live();
        live.ProcessLog = sink;
        live.SessionName = "h283";
        live.AttachClientId = "cli_test";
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var agent = StatusEvent("idle", "claude");
        using var closed = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject { ["pane_id"] = "p2", ["tab_id"] = "t1", ["state"] = "closed" });
        using var render = RuntimeEvent(
            ProtocolEventTypes.TerminalRender,
            new JsonObject { ["pane_id"] = "p1" });
        var port = new ScriptPort { Handler = SplitThenClosed };

        await AttachSession.HandleRenderEventAsync(
            agent.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
        await AttachSession.HandleRenderEventAsync(
            closed.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
        await AttachSession.HandleRenderEventAsync(
            render.RootElement, port, new SnapshotAssembler(), null, live, CancellationToken.None);

        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventReceived
            && record.EventType == ProtocolEventTypes.PaneAgentStatusChanged
            && record.PaneId == "p1");
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PaneAgentStatusChanged
            && record.Outcome == ProcessLogEvents.OutcomePainted);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventReceived
            && record.EventType == ProtocolEventTypes.PaneLifecycle
            && record.PaneId == "p2");
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PaneLifecycle
            && record.Outcome == ProcessLogEvents.OutcomePainted);
        Assert.DoesNotContain(sink.Records, record =>
            record.EventType == ProtocolEventTypes.TerminalRender);
    }

    [Fact]
    public async Task Queued_structural_events_refresh_and_paint_once()
    {
        var port = new ScriptPort { Handler = SplitThenClosed };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p1",
                ["tab_id"] = "t1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["mode"] = "tiled",
            });
        using var closed = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["state"] = "closed",
                ["occupant_generation"] = 1,
            });
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["tab_id"] = "t1",
                ["focused_pane_id"] = "p1",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement, closed.RootElement, layout.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.Equal(1, port.SnapshotCalls);
        Assert.Equal(1, port.ExportCalls);
        Assert.Equal("t1", port.ExportedTab);
        Assert.Equal(24, live.Host.Rows);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Equal(3, sink.Records.Count(record =>
            record.Event == ProcessLogEvents.AttachEventReceived));
        Assert.Equal(2, sink.Records.Count(record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.Outcome == ProcessLogEvents.OutcomeCoalesced));
        Assert.Equal(1, sink.Records.Count(record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.LayoutUpdated
            && record.Outcome == ProcessLogEvents.OutcomePainted));
    }

    [Fact]
    public async Task Tiled_show_presents_the_mux_tab_when_the_client_tab_differs()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.SetPaneFrame(GlyphFrame("p2", "N"));
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t2",
                ["workspace_id"] = "w1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["placement"] = "tiled",
                ["mode"] = "tiled",
            });
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["workspace_id"] = "w1",
                ["tab_id"] = "t2",
                ["focused_pane_id"] = "p2",
                ["root"] = new JsonObject { ["type"] = "pane", ["pane_id"] = "p2" },
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement, layout.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.Equal("t2", live.TabId);
        Assert.Equal("p2", live.PaneId);
        Assert.Equal("t2", port.ExportedTab);
        Assert.NotNull(live.Chrome);
        Assert.Contains(live.Chrome.Panes, pane => pane.PaneId == "p2");
        Assert.DoesNotContain(live.Chrome.Panes, pane => pane.PaneId == "p1");
        var host = HostText(live.Host);
        Assert.Contains("shown", host, StringComparison.Ordinal);
        Assert.Contains("N", host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Focus_change_from_another_client_keeps_this_client_tab()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["workspace_id"] = "w1",
                ["tab_id"] = "t2",
                ["focused_pane_id"] = "p2",
            });
        using var tab = RuntimeEvent(
            ProtocolEventTypes.TabLifecycle,
            new JsonObject { ["workspace_id"] = "w1", ["tab_id"] = "t2", ["action"] = "focused" });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [layout.RootElement, tab.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.Equal("t1", live.TabId);
        Assert.Equal("p1", live.PaneId);
        Assert.Equal("t1", port.ExportedTab);
        Assert.Empty(port.FocusedTabs);
    }

    [Fact]
    public async Task Tiled_show_with_the_mux_payload_presents_the_tab()
    {
        // The mux writes overlay_generation as JSON null for a tiled show.
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var wire = Hypa.AgentRuntime.Application.RuntimeEventPayloadJson.WritePanePlacementChanged(
            "p2", "t2", "w1", from: "hidden", to: "tiled", mode: "tiled");
        Assert.Contains("\"overlay_generation\":null", wire, StringComparison.Ordinal);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            JsonNode.Parse(wire)!.AsObject());

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.Equal("t2", live.TabId);
        Assert.Equal("t2", port.ExportedTab);
        // The client focuses the shown tab on its own connection, so the mux
        // sends it the shown pane's render frames.
        Assert.Contains("t2", port.FocusedTabs);
    }

    [Fact]
    public async Task Hidden_size_grid_stays_off_the_host_until_it_matches_the_pane()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t2",
                ["workspace_id"] = "w1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["placement"] = "tiled",
                ["mode"] = "tiled",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");
        Assert.True(pane.Content.Cols > 0 && pane.Content.Rows > 0);
        Assert.NotEqual(120, pane.Content.Cols);

        var question = "question: continue? (y/n)";
        var hidden = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            Full = true,
            GridCols = 120,
            GridRows = 40,
            Generation = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = question }],
        };
        Assert.False(AttachSession.TryWriteCells(tty, live, hidden, reanchorBeforePaint: true));
        Assert.DoesNotContain(question, HostText(live.Host), StringComparison.Ordinal);

        var matched = hidden with
        {
            GridCols = pane.Content.Cols,
            GridRows = pane.Content.Rows,
            Generation = 2,
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, matched, reanchorBeforePaint: true));
        Assert.Contains(question, HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declined_resize_is_sent_once_until_the_box_or_owner_changes()
    {
        var resizes = 0;
        var port = new ScriptPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.PaneResize)
                {
                    resizes++;
                    return Parse(
                        """
                        {"ok":true,"pane_id":"p2","cols":120,"rows":40,"geometry_owner":"conn_b"}
                        """);
                }

                return ShownTabOverClientTab(method, parameters);
            },
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t2",
                ["workspace_id"] = "w1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["placement"] = "tiled",
                ["mode"] = "tiled",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        var afterShow = resizes;
        Assert.True(afterShow >= 1);
        await AttachSession.ResizeVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Equal(afterShow, resizes);

        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");
        var wider = pane.Content with { Cols = pane.Content.Cols + 1 };
        live.Chrome = live.Chrome with
        {
            Panes = live.Chrome.Panes
                .Select(item => item.PaneId == "p2" ? item with { Content = wider } : item)
                .ToList(),
        };
        await AttachSession.ResizeVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Equal(afterShow + 1, resizes);
        await AttachSession.ResizeVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Equal(afterShow + 1, resizes);

        using var owners = JsonDocument.Parse(
            """
            {"panes":[{"pane_id":"p2","geometry_owner":"conn_c"}]}
            """);
        AttachSession.NoteSnapshotGeometryOwners(live, owners.RootElement);
        await AttachSession.ResizeVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Equal(afterShow + 2, resizes);
        await AttachSession.ResizeVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Equal(afterShow + 2, resizes);
    }

    [Fact]
    public async Task Repeated_frame_drops_log_once_until_the_frame_is_admitted()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t2",
                ["workspace_id"] = "w1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["placement"] = "tiled",
                ["mode"] = "tiled",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        var matched = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            Full = true,
            GridCols = pane.Content.Cols,
            GridRows = pane.Content.Rows,
            Generation = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = "question: continue? (y/n)" }],
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, matched, reanchorBeforePaint: true));

        // Deltas against a base this client never applied: one drop kind.
        var stale = matched with { Full = false, BaseGeneration = 40, Generation = 41 };
        Assert.False(AttachSession.TryWriteCells(tty, live, stale, reanchorBeforePaint: true));
        Assert.False(AttachSession.TryWriteCells(tty, live, stale with { Generation = 42 }, reanchorBeforePaint: true));
        Assert.False(AttachSession.TryWriteCells(tty, live, stale with { Generation = 43 }, reanchorBeforePaint: true));
        Assert.Equal(1, CountObserve(sink, "p2", "generation"));

        Assert.True(AttachSession.TryWriteCells(tty, live, matched with { Generation = 5 }, reanchorBeforePaint: true));
        var admitted = "admitted " + pane.Content.Cols + "x" + pane.Content.Rows;
        Assert.Equal(2, CountObserve(sink, "p2", admitted));
        Assert.Equal(1, CountObserve(sink, "p2", "suppressed 2 generation"));

        Assert.False(AttachSession.TryWriteCells(tty, live, stale with { Generation = 46 }, reanchorBeforePaint: true));
        Assert.False(AttachSession.TryWriteCells(tty, live, stale with { Generation = 47 }, reanchorBeforePaint: true));
        Assert.Equal(2, CountObserve(sink, "p2", "generation"));

        var again = matched with { Generation = 8 };
        Assert.True(AttachSession.TryWriteCells(tty, live, again, reanchorBeforePaint: true));
        Assert.Equal(1, CountObserve(sink, "p2", "suppressed 1 generation"));
        var admitsAfterDrop = CountObserve(sink, "p2", admitted);

        // The same admit again, with no drop between, is not logged again.
        Assert.True(AttachSession.TryWriteCells(tty, live, again with { Generation = 9 }, reanchorBeforePaint: true));
        Assert.Equal(admitsAfterDrop, CountObserve(sink, "p2", admitted));
    }

    [Fact]
    public async Task Frame_sized_by_another_attach_is_clipped_into_the_local_pane()
    {
        // Issue #138: another attach took over the pane geometry after this
        // client's own resize landed. This view must keep painting its frames.
        var clock = new ManualTime();
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);

        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");
        var remote = RemoteSizedFrame(pane.Content);

        // Within the grace after our own resize, a frame at another size is stale.
        Assert.False(AttachSession.TryWriteCells(tty, live, remote, reanchorBeforePaint: true));
        Assert.True(live.BlitReanchorPending);
        live.BlitReanchorPending = false; // The render loop consumes the reanchor request.

        clock.Advance(AttachSession.OwnPaneResizeGrace);
        AssertRemoteFrameFollows(tty, live, pane.Content, remote with { Generation = 2 });
    }

    [Fact]
    public async Task Frame_at_the_owner_size_paints_at_once_after_a_declined_resize()
    {
        var port = new ScriptPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.PaneResize)
                {
                    return Parse(
                        """
                        {"ok":true,"pane_id":"p2","cols":120,"rows":40,"geometry_owner":"conn_b"}
                        """);
                }

                return ShownTabOverClientTab(method, parameters);
            },
        };
        var live = Live();
        live.Time = new ManualTime();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);

        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");
        Assert.True(live.DeclinedPaneResizes.ContainsKey("p2"));
        var remote = RemoteSizedFrame(pane.Content) with { GridCols = 120, GridRows = 40 };
        AssertRemoteFrameFollows(tty, live, pane.Content, remote);
    }

    [Fact]
    public async Task Zero_row_delta_moves_the_host_caret()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        var output = new MemoryStream();
        using var tty = new UnixRawTerminal(output, 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        live.PaneId = "p2";
        var box = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2").Content;
        var full = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            Full = true,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ ab" }],
            Cursor = new TerminalRenderCursorPayload { Col = 4, Row = 0, Visible = true, Shape = 2 },
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        // A cursor-only change: no rows, the caret moves left one cell.
        var mark = output.Length;
        var delta = full with
        {
            Full = false,
            BaseGeneration = 1,
            Generation = 2,
            Rows = [],
            Cursor = new TerminalRenderCursorPayload { Col = 3, Row = 0, Visible = true, Shape = 2 },
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, delta, reanchorBeforePaint: true));

        Assert.Equal(box.Col + 3, live.Host.Cursor.Col);
        Assert.Equal(box.Row, live.Host.Cursor.Row);
        Assert.True(live.Host.Cursor.Visible);
        var written = Encoding.UTF8.GetString(output.ToArray(), (int)mark, (int)(output.Length - mark));
        var open = written.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        var close = written.LastIndexOf("\u001b[?2026l", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, written);
        var block = written[open..close];
        Assert.Contains($"\u001b[{box.Row + 1};{box.Col + 4}H", block, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[?25h", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_compose_requests_reanchor_and_pane_converges()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 2, "$ two"), reanchorBeforePaint: true));

        // The host refuses the write of generation 3.
        live.PresentationFrozen = true;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ three"), reanchorBeforePaint: true));
        live.PresentationFrozen = false;

        // Generations 4 to 24 chain on 3, which the client never applied.
        for (var generation = 4; generation <= 24; generation++)
            Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, generation, "$ lost"), reanchorBeforePaint: true));
        Assert.True(live.ChainReanchors.HasQueued);
        // The request does not make the render loop take the control gate.
        Assert.False(AttachSession.HasPendingObserveWork(live));

        var observedBefore = ObservedPanes(live);
        var mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        var request = Assert.Single(ObserveCalls(port, mark));
        Assert.Equal("p2", request["pane_id"]?.GetValue<string>());
        Assert.Equal("sub_render", request["subscription_id"]?.GetValue<string>());
        Assert.False(request["replace"]?.GetValue<bool>());
        Assert.Equal(observedBefore, ObservedPanes(live));

        // More drops before the full frame comes ask nothing more.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 25, "$ lost"), reanchorBeforePaint: true));
        mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Empty(ObserveCalls(port, mark));
        Assert.Equal(1, live.ChainReanchors.RequestsSent);

        // The reply is a full frame. The next delta chains on it and paints.
        var reply = ChainFull(box, generation: 26, "$ reply");
        Assert.True(AttachSession.TryWriteCells(tty, live, reply, reanchorBeforePaint: true));
        Assert.False(live.ChainReanchors.IsOpen("p2"));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(reply, 27, "$ typed after"), reanchorBeforePaint: true));
        Assert.Contains("$ typed after", HostText(live.Host), StringComparison.Ordinal);
        Assert.Equal(27, live.LastAppliedBlit("p2").Generation);
    }

    [Fact]
    public async Task Global_reset_with_an_unchanged_visible_set_requests_one_reanchor_and_the_pane_paints()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 2, "$ two"), reanchorBeforePaint: true));
        // Every visible pane is observed and has a frame.
        await AttachSession.ObserveVisiblePanesAsync(port, live, CancellationToken.None);

        // The read loop resets every chain, and the visible set is the same.
        live.ResetAppliedBlit();
        var mark = port.Calls.Count;
        await AttachSession.ObserveVisiblePanesAsync(port, live, CancellationToken.None);
        Assert.Empty(ObserveCalls(port, mark));

        // The next delta does not chain. The client asks for one full frame.
        for (var generation = 3; generation <= 20; generation++)
            Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, generation, "$ lost"), reanchorBeforePaint: true));
        var observedBefore = ObservedPanes(live);
        mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        var request = Assert.Single(ObserveCalls(port, mark));
        Assert.Equal("p2", request["pane_id"]?.GetValue<string>());
        Assert.False(request["replace"]?.GetValue<bool>());
        Assert.Equal(observedBefore, ObservedPanes(live));

        var reply = ChainFull(box, generation: 21, "$ reply");
        Assert.True(AttachSession.TryWriteCells(tty, live, reply, reanchorBeforePaint: true));
        Assert.Contains("$ reply", HostText(live.Host), StringComparison.Ordinal);
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(reply, 22, "$ next"), reanchorBeforePaint: true));
        Assert.Contains("$ next", HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_compose_leaves_the_stored_frame_at_the_last_applied_generation()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 2, "$ two"), reanchorBeforePaint: true));

        live.PresentationFrozen = true;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ three"), reanchorBeforePaint: true));

        Assert.True(live.TryGetPaneFrame("p2", out var stored));
        Assert.NotNull(stored);
        Assert.Equal(2, live.LastAppliedBlit("p2").Generation);
        Assert.Equal(live.LastAppliedBlit("p2").Generation, stored.Generation);
    }

    [Fact]
    public async Task Refused_compose_of_a_first_frame_leaves_no_stored_frame()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);

        live.PresentationFrozen = true;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 1, "$ one"), reanchorBeforePaint: true));

        Assert.False(live.TryGetPaneFrame("p2", out var stored) && stored is not null);
        Assert.Equal(0, live.LastAppliedBlit("p2").Generation);
    }

    [Fact]
    public async Task Each_generation_drop_of_a_pane_is_counted_in_the_process_log()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        for (var i = 0; i < 18; i++)
        {
            var stale = full with { Full = false, BaseGeneration = 40 + i, Generation = 41 + i };
            Assert.False(AttachSession.TryWriteCells(tty, live, stale, reanchorBeforePaint: true));
        }

        // While the pane is still dropped, the reanchor request shows the count.
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        var request = Assert.Single(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachObserve
            && record.PaneId == "p2"
            && record.Reason == "reanchor");
        Assert.Equal(18, request.DropCount);

        // The full frame ends the row, and the summary has the whole count.
        Assert.True(AttachSession.TryWriteCells(tty, live, full with { Generation = 60 }, reanchorBeforePaint: true));
        var first = Assert.Single(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachObserve
            && record.PaneId == "p2"
            && record.Reason == "generation");
        Assert.Equal(1, first.DropCount);
        var summary = Assert.Single(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachObserve
            && record.PaneId == "p2"
            && record.Reason == "suppressed 17 generation");
        Assert.Equal(18, summary.DropCount);
    }

    [Fact]
    public async Task Failed_reanchor_request_asks_again_only_after_the_resend_wait()
    {
        var clock = new ManualTime();
        var fail = true;
        var port = new ScriptPort
        {
            Handler = (method, parameters) =>
            {
                if (fail && method == ProtocolMethods.TerminalObserve)
                    throw new IOException("link lost");
                return ShownTabOverClientTab(method, parameters);
            },
        };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ lost"), reanchorBeforePaint: true));
        var mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Single(ObserveCalls(port, mark));

        // The link still fails. More drops in the wait send nothing.
        for (var generation = 4; generation <= 10; generation++)
            Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, generation, "$ lost"), reanchorBeforePaint: true));
        mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Empty(ObserveCalls(port, mark));

        // After the wait, the next drop asks again.
        fail = false;
        clock.Advance(PaneChainReanchorRequests.ResendAfter);
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 11, "$ lost"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Equal("p2", Assert.Single(ObserveCalls(port, mark))["pane_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reanchor_request_for_a_pane_the_mux_does_not_find_is_dropped()
    {
        var port = new ScriptPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.TerminalObserve)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, "Pane not found: p2");
                return ShownTabOverClientTab(method, parameters);
            },
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ lost"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.False(live.ChainReanchors.IsOpen("p2"));
    }

    [Fact]
    public async Task Size_drop_does_not_request_a_reanchor()
    {
        var clock = new ManualTime();
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        live.RenderSub = "sub_render";
        var pane = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2");

        // Within the grace after our own resize, a frame at another size is
        // stale. A full frame at that size would be dropped again.
        Assert.False(AttachSession.TryWriteCells(tty, live, RemoteSizedFrame(pane.Content), reanchorBeforePaint: true));
        Assert.False(live.ChainReanchors.IsOpen("p2"));
        Assert.False(AttachSession.HasPendingObserveWork(live));
    }

    [Fact]
    public async Task Pending_reanchor_reply_does_not_delay_the_focused_pane()
    {
        var clock = new ManualTime();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new ScriptPort
        {
            AsyncHandler = (method, _) => method == ProtocolMethods.TerminalObserve ? reply.Task : null,
        };
        var live = Live();
        live.Time = clock;
        live.ChromeSeed = new ChromeComputeSeed(SplitRoot(), false, null, "p1", 80, 24);
        live.Chrome = AttachSession.ComputeLiveChrome(live, 80, 24, SplitRoot(), false, null, "p1");
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var p2Box = ChainPane(live);
        var p1Box = Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p1").Content;
        var p1Full = ChainFull(p1Box, generation: 1, "$ one") with { PaneId = "p1" };
        Assert.True(AttachSession.TryWriteCells(tty, live, p1Full, reanchorBeforePaint: true));
        var p2Full = ChainFull(p2Box, generation: 1, "$ side");
        Assert.True(AttachSession.TryWriteCells(tty, live, p2Full, reanchorBeforePaint: true));

        // The chain of the side pane breaks. Its request goes, and the mux
        // does not reply.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(p2Full, 3, "$ lost"), reanchorBeforePaint: true));
        var observe = AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.True(observe.IsCompleted);
        Assert.Equal(1, live.ChainReanchors.InFlight);
        Assert.Single(ObserveCalls(port, 0));

        // The echo of the focused pane applies and paints at once.
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(p1Full, 2, "$ typed"), reanchorBeforePaint: true));
        Assert.Contains("$ typed", HostText(live.Host), StringComparison.Ordinal);

        // While the request is in flight, a later drop sends no second one,
        // also after the resend wait.
        clock.Advance(PaneChainReanchorRequests.ResendAfter);
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(p2Full, 4, "$ lost"), reanchorBeforePaint: true));
        Assert.True(AttachSession.ObservePendingAsync(port, live, CancellationToken.None).IsCompleted);
        Assert.Single(ObserveCalls(port, 0));

        // The full frame comes on the render stream before the reply, and
        // the render path applies it as any other body.
        var side = ChainFull(p2Box, generation: 5, "$ side again");
        Assert.True(AttachSession.TryWriteCells(tty, live, side, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(side, 6, "$ side next"), reanchorBeforePaint: true));
        Assert.Contains("$ side next", HostText(live.Host), StringComparison.Ordinal);

        reply.SetResult(Parse("{}"));
        await Task.Yield();
        await Poll(() => live.ChainReanchors.InFlight == 0);
    }

    [Fact]
    public async Task Full_frames_refused_in_a_row_ask_again_after_the_wait_not_once_per_reply()
    {
        var clock = new ManualTimeProvider();
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);

        // The host shows a frame of a newer generation than the full frames
        // that come next, as the frame of a mux before its restart.
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 90, "$ earlier mux"), reanchorBeforePaint: true));

        // The first refused full frame asks again at once.
        var mark = port.Calls.Count;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 2, "$ two"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.Single(ObserveCalls(port, mark));

        // The full frame of that request is refused too, and the next one.
        // No request goes for each of them.
        for (var generation = 3; generation <= 12; generation++)
        {
            Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation, "$ again"), reanchorBeforePaint: true));
            await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        }

        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.Single(ObserveCalls(port, mark));

        // After the wait the pane asks one time more, also with no later frame.
        await clock.WaitForArmedTimerAsync();
        clock.Advance(PaneChainReanchorRequests.ResendAfter);
        await Poll(() => ObserveCalls(port, mark).Count == 2);
        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.Equal(2, ObserveCalls(port, mark).Count);
    }

    [Fact]
    public async Task A_retry_timer_from_before_a_shown_full_frame_does_not_send_a_later_retry_early()
    {
        var clock = new ManualTimeProvider();
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 90, "$ earlier mux"), reanchorBeforePaint: true));
        var mark = port.Calls.Count;

        // Two refused full frames in a row: one request at once, then a
        // retry after the wait, with its timer.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 2, "$ two"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 3, "$ three"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        await clock.WaitForArmedTimerAsync();
        Assert.Single(ObserveCalls(port, mark));

        // The host shows a full frame, so that retry ends. 600 ms later the
        // chain breaks again in the same way and gets its own retry.
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 91, "$ shown"), reanchorBeforePaint: true));
        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 4, "$ four"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.Equal(2, ObserveCalls(port, mark).Count);
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 5, "$ five"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        await clock.WaitForArmedTimerAsync();

        // The first timer ends. It does not send the later retry 600 ms early.
        // Wait until its ticket is checked. A retry that the check queued is
        // queued, in flight, or sent, so read the three in that order.
        var checkedBefore = live.ChainReanchors.RetriesChecked;
        clock.Advance(PaneChainReanchorRequests.ResendAfter - TimeSpan.FromMilliseconds(600));
        await Poll(() => live.ChainReanchors.RetriesChecked > checkedBefore);
        Assert.False(live.ChainReanchors.HasQueued);
        Assert.Equal(0, live.ChainReanchors.InFlight);
        Assert.Equal(2, ObserveCalls(port, mark).Count);

        // The later timer sends it.
        clock.Advance(TimeSpan.FromMilliseconds(600));
        await Poll(() => ObserveCalls(port, mark).Count == 3);
        await Poll(() => live.ChainReanchors.InFlight == 0);
        Assert.Equal(3, ObserveCalls(port, mark).Count);
    }

    [Fact]
    public async Task Refused_full_reply_asks_again_at_once()
    {
        var clock = new ManualTime();
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        live.Time = clock;
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ lost"), reanchorBeforePaint: true));
        var mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Single(ObserveCalls(port, mark));

        // The full reply comes, but the host refuses its write. No later
        // delta comes. The pane asks again at once, not after the wait.
        live.PresentationFrozen = true;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 4, "$ reply"), reanchorBeforePaint: true));
        Assert.True(live.ChainReanchors.HasQueued);
        // A frozen presentation keeps the request queued.
        mark = port.Calls.Count;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Empty(ObserveCalls(port, mark));

        live.PresentationFrozen = false;
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Equal("p2", Assert.Single(ObserveCalls(port, mark))["pane_id"]?.GetValue<string>());

        var again = ChainFull(box, generation: 5, "$ reply again");
        Assert.True(AttachSession.TryWriteCells(tty, live, again, reanchorBeforePaint: true));
        Assert.False(live.ChainReanchors.IsOpen("p2"));
        Assert.Contains("$ reply again", HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_compose_restores_the_cells_of_the_stored_frame()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 2, "$ two"), reanchorBeforePaint: true));

        live.PresentationFrozen = true;
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ three"), reanchorBeforePaint: true));
        Assert.Equal("$ two", StoredRowText(live, "p2", 0));

        // A refused full frame of the same size blank-fills the shared
        // storage. The stored frame gets its cells back too.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 4, "$ four"), reanchorBeforePaint: true));
        Assert.True(live.TryGetPaneFrame("p2", out var stored));
        Assert.Equal(2, stored!.Generation);
        Assert.Equal("$ two", StoredRowText(live, "p2", 0));
    }

    [Fact]
    public async Task Refused_compose_keeps_a_newer_frame_stored_during_the_write()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        // Another thread stores a newer frame of the pane while the batch
        // composes. The host write sees it and refuses the older batch.
        var newer = VtCellUnpacker.Apply(ChainFull(box, generation: 99, "$ newer"), previous: null);
        tty.DuringCompose = () =>
        {
            tty.DuringCompose = null;
            live.SetPaneFrame(newer);
        };
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 2, "$ two"), reanchorBeforePaint: true));

        Assert.True(live.TryGetPaneFrame("p2", out var stored));
        Assert.Same(newer, stored);
        Assert.Equal("$ newer", StoredRowText(live, "p2", 0));
    }

    [Fact]
    public async Task Break_after_the_full_reply_while_the_request_is_in_flight_asks_again_when_it_ends()
    {
        var replies = new List<TaskCompletionSource<JsonElement>>();
        var port = new ScriptPort
        {
            AsyncHandler = (method, _) =>
            {
                if (method != ProtocolMethods.TerminalObserve)
                    return null;
                var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (replies)
                    replies.Add(reply);
                return reply.Task;
            },
            Handler = ShownTabOverClientTab,
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        var mark = port.Calls.Count;

        // The chain breaks. More drops before the full frame are the same break.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ lost"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 4, "$ lost"), reanchorBeforePaint: true));
        Assert.Single(ObserveCalls(port, mark));

        // The full reply comes on the stream before the reply of the call.
        var reply = ChainFull(box, generation: 5, "$ reply");
        Assert.True(AttachSession.TryWriteCells(tty, live, reply, reanchorBeforePaint: true));

        // Then the chain breaks again, and no later delta comes.
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(reply, 9, "$ lost"), reanchorBeforePaint: true));
        Assert.Single(ObserveCalls(port, mark));

        // The call ends. The pane asks again at once.
        replies[0].SetResult(Parse("{}"));
        await Poll(() => ObserveCalls(port, mark).Count == 2);
        Assert.Equal("p2", ObserveCalls(port, mark)[1]["pane_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Request_in_flight_that_ends_with_no_new_break_asks_nothing_more()
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new ScriptPort
        {
            AsyncHandler = (method, _) => method == ProtocolMethods.TerminalObserve ? reply.Task : null,
            Handler = ShownTabOverClientTab,
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        var mark = port.Calls.Count;

        // Drops before the full frame are one break, and one request.
        for (var generation = 3; generation <= 8; generation++)
            Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, generation, "$ lost"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.True(AttachSession.TryWriteCells(tty, live, ChainFull(box, generation: 9, "$ reply"), reanchorBeforePaint: true));

        reply.SetResult(Parse("{}"));
        await Poll(() => live.ChainReanchors.InFlight == 0);
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        Assert.Single(ObserveCalls(port, mark));
        Assert.False(live.ChainReanchors.IsOpen("p2"));
    }

    [Fact]
    public async Task Failed_request_after_a_new_break_asks_again_after_the_wait_with_no_later_delta()
    {
        var replies = new List<TaskCompletionSource<JsonElement>>();
        var port = new ScriptPort
        {
            AsyncHandler = (method, _) =>
            {
                if (method != ProtocolMethods.TerminalObserve)
                    return null;
                var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (replies)
                    replies.Add(reply);
                return reply.Task;
            },
            Handler = ShownTabOverClientTab,
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 101, 37);
        await ShowHiddenPaneAsync(port, tty, live);
        var box = ChainPane(live);
        var full = ChainFull(box, generation: 1, "$ one");
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));
        var mark = port.Calls.Count;

        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(full, 3, "$ lost"), reanchorBeforePaint: true));
        await AttachSession.ObservePendingAsync(port, live, CancellationToken.None);
        var reply = ChainFull(box, generation: 5, "$ reply");
        Assert.True(AttachSession.TryWriteCells(tty, live, reply, reanchorBeforePaint: true));
        Assert.False(AttachSession.TryWriteCells(tty, live, ChainDelta(reply, 9, "$ lost"), reanchorBeforePaint: true));

        // The call fails. The retry waits, then goes with no later delta.
        var failedAt = DateTime.UtcNow;
        replies[0].SetException(new IOException("link lost"));
        await Poll(() => ObserveCalls(port, mark).Count == 2);
        Assert.True(DateTime.UtcNow - failedAt >= PaneChainReanchorRequests.ResendAfter - TimeSpan.FromMilliseconds(50));
    }

    private static string StoredRowText(AttachLiveState live, string paneId, int row)
    {
        Assert.True(live.TryGetPaneFrame(paneId, out var frame));
        var text = new StringBuilder();
        foreach (var cell in frame!.Cells[row])
            text.Append(cell.Text);
        return text.ToString().TrimEnd();
    }

    private static Task Poll(Func<bool> ready) =>
        UnitTests.Poll.UntilAsync(ready, "the condition");

    private static CellRect ChainPane(AttachLiveState live)
    {
        live.RenderSub = "sub_render";
        lock (live.ChromeStateGate)
            live.ObservedPaneIds.Add("p2");
        return Assert.Single(live.Chrome!.Panes, item => item.PaneId == "p2").Content;
    }

    private static TerminalRenderCellsPayload ChainFull(CellRect box, long generation, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        Full = true,
        GridCols = box.Cols,
        GridRows = box.Rows,
        Generation = generation,
        Rows = [new TerminalRenderCellRow { I = 0, T = text }],
    };

    private static TerminalRenderCellsPayload ChainDelta(TerminalRenderCellsPayload full, long generation, string text) =>
        full with
        {
            Full = false,
            BaseGeneration = generation - 1,
            Generation = generation,
            Rows = [new TerminalRenderCellRow { I = 0, T = text }],
        };

    private static string[] ObservedPanes(AttachLiveState live)
    {
        lock (live.ChromeStateGate)
            return [.. live.ObservedPaneIds.Order(StringComparer.Ordinal)];
    }

    private static List<JsonObject> ObserveCalls(ScriptPort port, int mark)
    {
        lock (port.Calls)
        {
            return [.. port.Calls
                .Skip(mark)
                .Where(call => call.Method == ProtocolMethods.TerminalObserve)
                .Select(call => call.Parameters ?? new JsonObject())];
        }
    }

    private static async Task ShowHiddenPaneAsync(ScriptPort port, UnixRawTerminal tty, AttachLiveState live)
    {
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p2",
                ["tab_id"] = "t2",
                ["workspace_id"] = "w1",
                ["from"] = "hidden",
                ["to"] = "tiled",
                ["placement"] = "tiled",
                ["mode"] = "tiled",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);
    }

    private static TerminalRenderCellsPayload RemoteSizedFrame(CellRect box) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        Full = true,
        GridCols = box.Cols + 20,
        GridRows = box.Rows - 5,
        Generation = 1,
        Rows = [new TerminalRenderCellRow { I = 0, T = "$ ls" }],
    };

    private static void AssertRemoteFrameFollows(
        UnixRawTerminal tty,
        AttachLiveState live,
        CellRect box,
        TerminalRenderCellsPayload remote)
    {
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.True(AttachSession.TryWriteCells(tty, live, remote, reanchorBeforePaint: true));
        Assert.False(live.BlitReanchorPending);
        Assert.Equal(
            1,
            CountObserve(
                sink,
                "p2",
                "admitted " + remote.GridCols + "x" + remote.GridRows + " in " + box.Cols + "x" + box.Rows));

        // A delta on that frame applies too: the view follows the remote typing.
        var delta = remote with
        {
            Full = false,
            BaseGeneration = remote.Generation,
            Generation = remote.Generation + 1,
            Rows = [new TerminalRenderCellRow { I = 1, T = new string('x', remote.GridCols) }],
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, delta, reanchorBeforePaint: true));

        Assert.Contains("$ ls", HostText(live.Host), StringComparison.Ordinal);
        for (var col = 0; col < box.Cols; col++)
            Assert.Equal("x", live.Host.CellAt(box.Col + col, box.Row + 1).Text);
        Assert.NotEqual("x", live.Host.CellAt(box.Col + box.Cols, box.Row + 1).Text);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Layout_event_keeps_the_client_tab_when_it_does_not_name_mux_focus()
    {
        var port = new ScriptPort { Handler = ShownTabOverClientTab };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["workspace_id"] = "w1",
                ["tab_id"] = "t1",
                ["focused_pane_id"] = "p1",
            });

        await AttachSession.HandleRenderEventAsync(
            layout.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);

        Assert.Equal("t1", live.TabId);
        Assert.Equal("p1", live.PaneId);
        Assert.Equal("t1", port.ExportedTab);
    }

    [Fact]
    public async Task Hide_removes_pane_content_from_the_host()
    {
        var hidden = false;
        var port = new ScriptPort
        {
            Handler = (method, _) => hidden ? HiddenLayout(method) : VisibleLayout(method),
        };
        var live = Live();
        live.SetPaneFrame(GlyphFrame("p1", "Q"));
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var shown = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject { ["tab_id"] = "t1", ["focused_pane_id"] = "p1" });

        await AttachSession.HandleRenderEventAsync(
            shown.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
        Assert.Contains("Q", HostText(live.Host), StringComparison.Ordinal);

        hidden = true;
        using var placement = RuntimeEvent(
            ProtocolEventTypes.PanePlacementChanged,
            new JsonObject
            {
                ["pane_id"] = "p1",
                ["tab_id"] = "t1",
                ["workspace_id"] = "w1",
                ["from"] = "tiled",
                ["to"] = "hidden",
                ["placement"] = "hidden",
                ["mode"] = "tiled",
            });
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["workspace_id"] = "w1",
                ["tab_id"] = "t1",
                ["focused_pane_id"] = "p1",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [placement.RootElement, layout.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.DoesNotContain("Q", HostText(live.Host), StringComparison.Ordinal);
        Assert.NotNull(live.Chrome);
        Assert.DoesNotContain(live.Chrome.Panes, pane => pane.PaneId == "p1");
    }

    [Fact]
    public async Task Hidden_child_close_removes_the_sidebar_row()
    {
        var closed = false;
        var port = new ScriptPort
        {
            Handler = (method, _) => closed
                ? SplitThenClosed(method, null)
                : HiddenChildSnapshot(method, null),
        };
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var started = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject
            {
                ["pane_id"] = "h1",
                ["state"] = "running",
                ["occupant_generation"] = 1,
            });

        await AttachSession.HandleRenderEventAsync(
            started.RootElement, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
        Assert.Contains(live.SidebarRows, row => row.Id == "h1");
        Assert.Contains("claude", HostText(live.Host), StringComparison.Ordinal);

        closed = true;
        using var lifecycle = RuntimeEvent(
            ProtocolEventTypes.PaneLifecycle,
            new JsonObject
            {
                ["pane_id"] = "h1",
                ["state"] = "closed",
                ["occupant_generation"] = 1,
            });
        using var layout = RuntimeEvent(
            ProtocolEventTypes.LayoutUpdated,
            new JsonObject
            {
                ["workspace_id"] = "w1",
                ["tab_id"] = "t1",
                ["focused_pane_id"] = "p1",
            });

        await AttachSession.ApplyQueuedStructuralEventsAsync(
            [lifecycle.RootElement, layout.RootElement],
            port,
            tty,
            live,
            CancellationToken.None);

        Assert.DoesNotContain(live.SidebarRows, row => row.Id == "h1");
        Assert.DoesNotContain("claude", HostText(live.Host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_popup_open_is_dropped()
    {
        var live = Live();
        live.PopupClosedSeq = 5;
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = PopupEvent("opened", 3);

        await AttachSession.HandleRenderEventAsync(
            ev.RootElement, new ScriptPort(), new SnapshotAssembler(), tty, live, CancellationToken.None);

        Assert.False(live.PopupOpen);
        AssertPopupDropped(live, "stale");
    }

    [Fact]
    public async Task Stale_popup_open_is_dropped_from_the_control_batch()
    {
        var live = Live();
        live.PopupClosedSeq = 5;
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var ev = PopupEvent("opened", 3);

        await AttachSession.ApplyControlEventsAsync(
            [ev.RootElement], live, new ScriptPort(), tty, CancellationToken.None);

        Assert.False(live.PopupOpen);
        AssertPopupDropped(live, "stale");
    }

    [Fact]
    public async Task Control_batch_of_agent_status_events_writes_one_frame()
    {
        async Task<int> WritesFor(int count)
        {
            var live = Live();
            var stream = new WriteCountingStream();
            using var tty = new UnixRawTerminal(stream, 80, 24);
            var docs = Enumerable.Range(0, count)
                .Select(i => StatusEvent(i % 2 == 0 ? "working" : "idle", "claude"))
                .ToList();
            try
            {
                stream.Writes = 0;
                await AttachSession.ApplyControlEventsAsync(
                    docs.Select(d => d.RootElement).ToList(), live, new ScriptPort(), tty, CancellationToken.None);
                Assert.Contains("claude", HostText(live.Host), StringComparison.Ordinal);
                return stream.Writes;
            }
            finally
            {
                foreach (var doc in docs)
                    doc.Dispose();
            }
        }

        var one = await WritesFor(1);
        var three = await WritesFor(3);

        Assert.True(one > 0);
        Assert.Equal(one, three);
    }

    [Fact]
    public async Task Control_batch_logs_painted_for_the_last_agent_status_event()
    {
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var first = StatusEvent("working", "claude");
        using var last = StatusEvent("idle", "claude");

        await AttachSession.ApplyControlEventsAsync(
            [first.RootElement, last.RootElement], live, new ScriptPort(), tty, CancellationToken.None);

        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        var outcomes = sink.Records
            .Where(record => record.Event == ProcessLogEvents.AttachEventOutcome
                && record.EventType == ProtocolEventTypes.PaneAgentStatusChanged)
            .Select(record => record.Outcome)
            .ToList();
        Assert.Equal([ProcessLogEvents.OutcomeCoalesced, ProcessLogEvents.OutcomePainted], outcomes);
    }

    [Fact]
    public async Task Control_batch_logs_the_window_title_outcome()
    {
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var title = RuntimeEvent(
            ProtocolEventTypes.ClientWindowTitleChanged,
            new JsonObject { ["title"] = "build", ["overridden"] = true });

        await AttachSession.ApplyControlEventsAsync(
            [title.RootElement], live, new ScriptPort(), tty, CancellationToken.None);

        Assert.Equal("build", live.WindowTitleOverride);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.ClientWindowTitleChanged
            && record.Outcome == ProcessLogEvents.OutcomeApplied);
    }

    [Fact]
    public void Faulted_render_loop_writes_a_fault_line_with_the_exception()
    {
        var live = Live();
        var faulted = Task.FromException(new InvalidOperationException("null overlay generation"));
        var idle = Task.Delay(Timeout.Infinite);

        AttachSession.TraceLoopFault(live, faulted, faulted, idle, idle, idle, idle);
        AttachSession.TraceLoopFault(live, idle, faulted, idle, idle, idle, idle);

        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        var fault = Assert.Single(sink.Records, record => record.Event == ProcessLogEvents.AttachLoopFault);
        Assert.Equal("render", fault.Action);
        Assert.Equal("InvalidOperationException: null overlay generation", fault.Reason);
    }

    private sealed class WriteCountingStream : MemoryStream
    {
        public int Writes { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0)
                Writes++;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > 0)
                Writes++;
            base.Write(buffer);
        }
    }

    [Fact]
    public async Task Rejected_popup_lifecycle_is_dropped()
    {
        var live = Live();
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var direct = PopupEvent("nope", 1);
        using var batched = PopupEvent("nope", 2);

        await AttachSession.HandleRenderEventAsync(
            direct.RootElement, new ScriptPort(), new SnapshotAssembler(), tty, live, CancellationToken.None);
        AssertPopupDropped(live, "rejected");

        await AttachSession.ApplyControlEventsAsync(
            [batched.RootElement], live, new ScriptPort(), tty, CancellationToken.None);
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Equal(2, sink.Records.Count(record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PopupLifecycle
            && record.Outcome == ProcessLogEvents.OutcomeDropped
            && record.Reason == "rejected"));
    }

    private static void AssertPopupDropped(AttachLiveState live, string reason)
    {
        var sink = Assert.IsType<CapturingProcessLogSink>(live.ProcessLog);
        Assert.Contains(sink.Records, record =>
            record.Event == ProcessLogEvents.AttachEventOutcome
            && record.EventType == ProtocolEventTypes.PopupLifecycle
            && record.Outcome == ProcessLogEvents.OutcomeDropped
            && record.Reason == reason);
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var port = new ScriptPort();
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            ChromeEnabled = true,
            SidebarOpen = true,
            SidebarCollapsed = false,
            SidebarRequestedWidth = 26,
            SidebarWidth = 26,
            SessionName = "live",
            AttachClientId = "cli_live",
            ProcessLog = new CapturingProcessLogSink(),
            CollapsedSectionIds = new HashSet<string>(StringComparer.Ordinal)
            {
                SidebarTokenGrammar.CubesId,
                SidebarTokenGrammar.SpacesId,
            },
            ChromeSeed = new ChromeComputeSeed(
                new LayoutNodeDto { Type = "pane", PaneId = "p1" },
                false,
                null,
                "p1",
                80,
                24),
            SidebarInput = new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                FocusedPaneId = "p1",
                FocusedTabId = "t1",
                FocusedWorkspaceId = "w1",
                Expanded = true,
                RequestedWidth = 26,
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = "p1",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "shell",
                        State = "idle",
                    },
                ],
            },
        };
    }

    private static JsonElement SplitThenClosed(string method, JsonObject? parameters)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t1",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[{"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"main"}],
                  "panes":[{"pane_id":"p1","tab_id":"t1","workspace_id":"w1","agent":"shell","state":"idle"}]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
        {
            return Parse(
                """
                {"root":{"type":"pane","pane_id":"p1"},"focused_pane_id":"p1"}
                """);
        }

        if (method == ProtocolMethods.TabList)
        {
            return Parse(
                """
                [{"tab_id":"t1","label":"main"}]
                """);
        }

        return Parse("{}");
    }

    private static JsonElement FocusedSecondTab(string method, JsonObject? _)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t2",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[
                    {"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"old"},
                    {"tab_id":"t2","workspace_id":"w1","focused_pane_id":"p2","label":"new"}
                  ],
                  "panes":[
                    {"pane_id":"p1","tab_id":"t1","workspace_id":"w1"},
                    {"pane_id":"p2","tab_id":"t2","workspace_id":"w1","agent":"claude","state":"idle"}
                  ]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
        {
            return Parse(
                """
                {"root":{"type":"pane","pane_id":"p2"},"focused_pane_id":"p2"}
                """);
        }

        if (method == ProtocolMethods.TabList)
        {
            return Parse(
                """
                [{"tab_id":"t1","label":"old"},{"tab_id":"t2","label":"new"}]
                """);
        }

        return Parse("{}");
    }

    private static JsonElement HiddenChildSnapshot(string method, JsonObject? _)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t1",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[{"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"main"}],
                  "panes":[
                    {"pane_id":"p1","tab_id":"t1","workspace_id":"w1","agent":"shell","state":"idle"},
                    {"pane_id":"h1","tab_id":"t1","workspace_id":"w1","agent":"claude","state":"idle","hidden":true,"parent_pane_id":"p1"}
                  ]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
        {
            return Parse(
                """
                {"root":{"type":"pane","pane_id":"p1"},"focused_pane_id":"p1"}
                """);
        }

        if (method == ProtocolMethods.TabList)
            return Parse("""[{"tab_id":"t1","label":"main"}]""");

        return Parse("{}");
    }

    private static JsonElement ShownTabOverClientTab(string method, JsonObject? _)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t1",
                  "mux_focused_workspace_id":"w1",
                  "mux_focused_tab_id":"t2",
                  "mux_focused_pane_id":"p2",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[
                    {"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"old"},
                    {"tab_id":"t2","workspace_id":"w1","focused_pane_id":"p1","label":"shown"}
                  ],
                  "panes":[
                    {"pane_id":"p1","tab_id":"t1","workspace_id":"w1"},
                    {"pane_id":"p2","tab_id":"t2","workspace_id":"w1"}
                  ]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
        {
            return Parse(
                """
                {"root":{"type":"pane","pane_id":"p2"},"focused_pane_id":"p2","tab_id":"t2"}
                """);
        }

        if (method == ProtocolMethods.TabList)
        {
            return Parse(
                """
                [{"tab_id":"t1","label":"old"},{"tab_id":"t2","label":"shown"}]
                """);
        }

        return Parse("{}");
    }

    private static JsonElement VisibleLayout(string method)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t1",
                  "mux_focused_workspace_id":"w1",
                  "mux_focused_tab_id":"t1",
                  "mux_focused_pane_id":"p1",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[{"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"main"}],
                  "panes":[{"pane_id":"p1","tab_id":"t1","workspace_id":"w1","placement":"tiled"}]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
        {
            return Parse(
                """
                {"root":{"type":"pane","pane_id":"p1"},"focused_pane_id":"p1","tab_id":"t1"}
                """);
        }

        if (method == ProtocolMethods.TabList)
            return Parse("""[{"tab_id":"t1","label":"main"}]""");

        return Parse("{}");
    }

    private static JsonElement HiddenLayout(string method)
    {
        if (method == ProtocolMethods.SessionSnapshot)
        {
            return Parse(
                """
                {
                  "focused_workspace_id":"w1",
                  "focused_tab_id":"t1",
                  "mux_focused_workspace_id":"w1",
                  "mux_focused_tab_id":"t1",
                  "mux_focused_pane_id":"p1",
                  "workspaces":[{"workspace_id":"w1","label":"one"}],
                  "tabs":[{"tab_id":"t1","workspace_id":"w1","focused_pane_id":"p1","label":"main"}],
                  "panes":[{"pane_id":"p1","tab_id":"t1","workspace_id":"w1","hidden":true,"placement":"hidden"}]
                }
                """);
        }

        if (method == ProtocolMethods.LayoutExport)
            return Parse("""{"tab_id":"t1"}""");

        if (method == ProtocolMethods.TabList)
            return Parse("""[{"tab_id":"t1","label":"main"}]""");

        return Parse("{}");
    }

    private static AssembledSnapshot GlyphFrame(string paneId, string glyph)
    {
        var cells = new AssembledCell[1][];
        cells[0] = [new AssembledCell(glyph, 1, false, AssembledStyle.Default)];
        return new AssembledSnapshot(
            paneId,
            1,
            1,
            "ghostty",
            "main",
            cells,
            default,
            AssembledCursor.Default,
            OccupantGeneration: 1,
            Generation: 1,
            IngestFull: true);
    }

    private static LayoutNodeDto SplitRoot() =>
        new()
        {
            Type = "split",
            Direction = "right",
            First = new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            Second = new LayoutNodeDto { Type = "pane", PaneId = "p2" },
        };

    private static JsonDocument StatusEvent(string status, string? agent)
    {
        var payload = new JsonObject
        {
            ["pane_id"] = "p1",
            ["tab_id"] = "t1",
            ["occupant_generation"] = 1,
            ["agent_status"] = status,
            ["seen"] = true,
        };
        payload["agent"] = agent is null ? JsonNode.Parse("null") : JsonValue.Create(agent);
        return RuntimeEvent(ProtocolEventTypes.PaneAgentStatusChanged, payload);
    }

    private static JsonDocument RuntimeEvent(string type, JsonObject payload, long? seq = null)
    {
        var parms = new JsonObject
        {
            ["type"] = type,
            ["payload"] = payload,
        };
        if (seq is { } value)
            parms["seq"] = value;
        return JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = parms,
        }.ToJsonString());
    }

    private static JsonDocument PopupEvent(string state, long seq) =>
        RuntimeEvent(
            ProtocolEventTypes.PopupLifecycle,
            new JsonObject { ["state"] = state },
            seq);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static int CountObserve(CapturingProcessLogSink sink, string paneId, string reason) =>
        sink.Records.Count(record =>
            record.Event == ProcessLogEvents.AttachObserve
            && record.PaneId == paneId
            && record.Reason == reason);

    private static string HostText(HostFrame host)
    {
        var text = new StringBuilder();
        for (var row = 0; row < host.Rows; row++)
        {
            for (var col = 0; col < host.Cols; col++)
                text.Append(host.CellAt(col, row).Text);
            text.Append('\n');
        }

        return text.ToString();
    }

    private sealed class ScriptPort : IAttachCommandPort
    {
        public string? ExportedTab { get; private set; }

        public int SnapshotCalls { get; private set; }

        public int ExportCalls { get; private set; }

        public Func<string, JsonObject?, JsonElement>? Handler { get; init; }

        /// <summary>When it returns a task, the call returns that task, which can stay pending.</summary>
        public Func<string, JsonObject?, Task<JsonElement>?>? AsyncHandler { get; init; }

        public List<string> FocusedTabs { get; } = [];

        public List<(string Method, JsonObject? Parameters)> Calls { get; } = [];

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((method, parameters?.DeepClone().AsObject()));
            if (string.Equals(method, ProtocolMethods.TabFocus, StringComparison.Ordinal))
                FocusedTabs.Add(parameters?["tab_id"]?.GetValue<string>() ?? "");
            if (string.Equals(method, ProtocolMethods.SessionSnapshot, StringComparison.Ordinal))
                SnapshotCalls++;
            if (string.Equals(method, ProtocolMethods.LayoutExport, StringComparison.Ordinal))
            {
                ExportCalls++;
                ExportedTab = parameters?["tab_id"]?.GetValue<string>();
            }
            if (AsyncHandler?.Invoke(method, parameters) is { } pending)
                return pending;
            if (Handler is not null)
                return Task.FromResult(Handler(method, parameters));
            return Task.FromResult(Parse("{}"));
        }
    }
}
