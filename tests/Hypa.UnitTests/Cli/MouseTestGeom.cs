using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.UnitTests.Cli;

internal static class MouseTestGeom
{
    public static LayoutChromeGeometry Split(
        bool sidebarOpen = false,
        int sidebarWidth = 18,
        IReadOnlyList<SidebarStubRow>? rows = null,
        IReadOnlyList<ToastHit>? toasts = null,
        AttachUiConfig? ui = null) =>
        LayoutChromeGeometry.Compute(
            80,
            24,
            RightSplit(),
            false,
            null,
            "p1",
            ui ?? LayoutChromeGeometry.BareInsets,
            2,
            AttachClientMode.Terminal,
            [("t1", "one", true), ("t2", "two", false)],
            sidebarOpen: sidebarOpen,
            sidebarWidth: sidebarWidth,
            sidebarRows: rows,
            toasts: toasts);

    public static LayoutChromeGeometry OverflowTabs(int overflowOffset = 2)
    {
        var tabs = Enumerable.Range(1, 8)
            .Select(i => ("t" + i, "long-label-" + i, i == overflowOffset + 1))
            .ToList();
        return LayoutChromeGeometry.Compute(
            40,
            24,
            RightSplit(),
            false,
            null,
            "p1",
            LayoutChromeGeometry.BareInsets,
            tabs.Count,
            AttachClientMode.Terminal,
            tabs,
            overflowOffset);
    }

    public static LayoutNodeDto RightSplit() =>
        new()
        {
            Type = "split",
            Direction = "right",
            Ratio = 0.5,
            First = new LayoutNodeDto { Type = "pane", PaneId = "p1", Label = "left" },
            Second = new LayoutNodeDto { Type = "pane", PaneId = "p2", Label = "right" },
        };

    public static AssembledSnapshot Frame(string text, string paneId = "p1", int cols = 20, string? mouse = null)
    {
        var line = text.PadRight(cols);
        var cells = new List<AssembledCell>(cols);
        foreach (var ch in line)
            cells.Add(new AssembledCell(ch.ToString(), 1, false, AssembledStyle.Default));
        JsonElement snap = default;
        if (mouse is not null)
        {
            using var doc = JsonDocument.Parse("{\"modes\":{\"mouse\":\"" + mouse + "\"}}");
            snap = doc.RootElement.Clone();
        }

        return new AssembledSnapshot(paneId, cols, 1, "basic", "main", [cells], snap, AssembledCursor.Default);
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static MouseRecordingPort ApplyPort(
        string leftoverPane = "p1",
        string focusedWorkspace = "w1")
    {
        var port = new MouseRecordingPort();
        port.Handler = (method, _) => method switch
        {
            ProtocolMethods.RuntimeLeaseClaim => Parse(
                """{"lease_id":"lease-next","outcome":"granted"}"""),
            ProtocolMethods.LayoutExport => Parse(
                """{"workspace_id":"w1","tab_id":"t1","zoomed":false,"focused_pane_id":"p1","root":{"type":"split","direction":"right","ratio":0.5,"first":{"type":"pane","pane_id":"p1"},"second":{"type":"pane","pane_id":"p2"}}}"""),
            ProtocolMethods.TabList => Parse(
                """[{"tab_id":"t1","label":"one"},{"tab_id":"t2","label":"two"}]"""),
            ProtocolMethods.SessionSnapshot => Parse(
                $$"""{"focused_workspace_id":"{{focusedWorkspace}}","focused_tab_id":"t1","tabs":[{"tab_id":"t1","focused_pane_id":"{{leftoverPane}}"}],"panes":[{"pane_id":"p1"},{"pane_id":"p2"},{"pane_id":"p9"}]}"""),
            _ => Parse("{}"),
        };
        return port;
    }

    public static AttachLiveState ApplyLive(
        MouseRecordingPort port,
        LayoutChromeGeometry geo,
        string paneId = "p1")
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", paneId, "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = paneId,
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            ChromeEnabled = true,
            Chrome = geo,
            Ui = LayoutChromeGeometry.BareInsets,
            SidebarOpen = geo.Sidebar is not null,
            SidebarWidth = geo.SidebarWidth,
            SidebarRows = geo.SidebarRows
                .Select(r => new SidebarStubRow(r.Kind, r.Id, r.Label, r.Rect.Row - (geo.Sidebar?.Row ?? 0)))
                .ToArray(),
            Toasts = geo.ToastHits,
        };
        live.Renew.Track("lease-in");
        live.Renew.Track("lease-r");
        return live;
    }

    public static async Task ApplyFeedAsync(
        AttachLiveState live,
        MouseRecordingPort port,
        params MouseEvent[] events)
    {
        using var linked = new CancellationTokenSource();
        var ctx = AttachSession.MouseFeedContextFor(live);
        foreach (var ev in events)
        {
            foreach (var result in live.Mouse.Feed(ev, ctx))
            {
                _ = await AttachSession.ApplyMouseResultAsync(
                    result, live, port, tty: null, linked, CancellationToken.None);
            }
        }
    }
}

internal sealed class MouseRecordingPort : IAttachCommandPort
{
    public List<(string Method, JsonObject? Params)> Calls { get; } = [];

    public Func<string, JsonObject?, JsonElement>? Handler { get; set; }

    public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        Calls.Add((method, parameters));
        return Task.FromResult(Handler?.Invoke(method, parameters) ?? MouseTestGeom.Parse("{}"));
    }
}

internal sealed class FrozenTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta) => Now += delta;
}
