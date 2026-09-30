using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachSemanticActionTests
{
    [Fact]
    public async Task Tab_click_and_external_focus_share_one_function()
    {
        var port = new MouseRecordingPort();
        var sink = new ListProcessLog();
        var live = Live(port, sink);
        port.Handler = (_, _) => TabReply();

        await AttachSession.ApplyChromeHitAsync(
            new ChromeHit(ChromeHitKind.Tab, TabId: "tab_two"),
            live,
            new LoggingAttachCommandPort(port, live),
            tty: null,
            CancellationToken.None);

        Assert.Equal("tab_two", live.TabId);
        Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.TabFocus);
        Assert.Contains(sink.Records, record => record.Source == "mouse" && record.Action == "focus");
        var line = Encoding.UTF8.GetString(ProcessLogJsonWriter.WriteLine(sink.Records[0]));
        Assert.Contains("\"source\":\"mouse\"", line, StringComparison.Ordinal);

        port.Calls.Clear();
        sink.Records.Clear();
        port.Handler = (_, _) => TabReply("tab_three");
        var reply = await AttachSession.ApplySemanticActionAsync(
            new LoggingAttachCommandPort(port, live),
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.TabFocus,
                TabId = "tab_three",
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);

        Assert.Equal(AttachSemanticOutcomes.Applied, reply.Outcome);
        Assert.Equal("tab_three", live.TabId);
        Assert.Equal("cli", reply.Source);
        Assert.Contains(sink.Records, record => record.Source == "cli" && record.Action == "focus");
        Assert.Null(reply.Detail);
    }

    [Fact]
    public async Task External_action_reaches_the_running_attach()
    {
        var dir = ShortDir();
        var port = new MouseRecordingPort();
        var sink = new ListProcessLog();
        var live = Live(port, sink);
        port.Handler = (_, _) => TabReply();
        using var gate = new SemaphoreSlim(1, 1);
        AttachSemanticActionHost? host = null;
        try
        {
            host = AttachSemanticActionHost.Start(
                dir,
                live,
                gate,
                (request, token) => AttachSession.ApplySemanticActionAsync(
                    new LoggingAttachCommandPort(port, live),
                    live,
                    tty: null,
                    request,
                    token),
                CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = new StringWriter();
            var exit = await AttachSemanticClient.ExecuteAsync(
                dir,
                attachClientId: null,
                new AttachSemanticRequest
                {
                    Action = AttachSemanticActions.TabFocus,
                    TabId = "tab_two",
                    Verbose = true,
                },
                stdout,
                cts.Token);

            Assert.Equal(AttachSemanticClient.ExitOk, exit);
            Assert.Equal("tab_two", live.TabId);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("applied", doc.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("tab_two", doc.RootElement.GetProperty("tab_id").GetString());
            Assert.Equal("cli", doc.RootElement.GetProperty("source").GetString());
            Assert.Equal(live.AttachClientId, doc.RootElement.GetProperty("attach_client_id").GetString());
            Assert.Contains("tab_two", doc.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains(port.Calls, call => call.Method == ProtocolMethods.TabFocus);
        }
        finally
        {
            if (host is not null)
                await host.DisposeAsync();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Several_attach_clients_require_a_client_id()
    {
        var dir = ShortDir();
        using var sleep = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            ArgumentList = { "30" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(sleep);
        try
        {
            AttachClientDirectory.Register(dir, Record("cli_one", sleep!.Id, Path.Combine(dir, "missing-one.sock")));
            AttachClientDirectory.Register(dir, Record("cli_two", Environment.ProcessId, Path.Combine(dir, "missing-two.sock")));

            var stdout = new StringWriter();
            var exit = await AttachSemanticClient.ExecuteAsync(
                dir,
                attachClientId: null,
                new AttachSemanticRequest { Action = AttachSemanticActions.TabFocus, TabId = "tab_two" },
                stdout,
                CancellationToken.None);

            Assert.Equal(AttachSemanticClient.ExitUsage, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("rejected", doc.RootElement.GetProperty("outcome").GetString());
            Assert.Contains("pass --client", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

            var chosen = new StringWriter();
            var chosenExit = await AttachSemanticClient.ExecuteAsync(
                dir,
                "cli_two",
                new AttachSemanticRequest { Action = AttachSemanticActions.TabFocus, TabId = "tab_two" },
                chosen,
                CancellationToken.None);
            Assert.Equal(AttachSemanticClient.ExitFailed, chosenExit);
            using var chosenDoc = JsonDocument.Parse(chosen.ToString());
            Assert.Equal("absent", chosenDoc.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            if (sleep is { HasExited: false })
                sleep.Kill(entireProcessTree: true);
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Missing_attach_reports_absent()
    {
        var dir = ShortDir();
        try
        {
            var stdout = new StringWriter();
            var exit = await AttachSemanticClient.ExecuteAsync(
                dir,
                attachClientId: null,
                new AttachSemanticRequest { Action = AttachSemanticActions.TabFocus, TabId = "tab_two" },
                stdout,
                CancellationToken.None);

            Assert.Equal(AttachSemanticClient.ExitFailed, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("absent", doc.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Stale_generation_is_rejected()
    {
        var port = new MouseRecordingPort();
        var live = Live(port, new ListProcessLog());
        live.TransportEnvelope.StampServerGeneration(4);

        var reply = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.TabFocus,
                TabId = "tab_two",
                AttachClientId = live.AttachClientId,
                ConnectionGeneration = 3,
            },
            CancellationToken.None);

        Assert.Equal(AttachSemanticOutcomes.Rejected, reply.Outcome);
        Assert.Equal("connection generation does not match", reply.Error);
        Assert.Empty(port.Calls);
        Assert.Equal(4UL, reply.ConnectionGeneration);
    }

    [Fact]
    public async Task Settings_open_and_close_drive_the_settings_overlay()
    {
        var port = new MouseRecordingPort();
        var live = Live(port, new ListProcessLog());

        var unknown = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.SettingsOpen,
                PageId = "no_such_page",
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Rejected, unknown.Outcome);
        Assert.Equal("settings page is unknown", unknown.Error);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);

        var opened = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.SettingsOpen,
                PageId = "integrations",
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Applied, opened.Outcome);
        Assert.Equal(AttachClientMode.Settings, live.Engine.Mode);
        Assert.Equal("integrations", live.Engine.Settings.ActivePageId);

        var closed = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.SettingsClose,
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Applied, closed.Outcome);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);

        var again = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.SettingsClose,
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Rejected, again.Outcome);
    }

    [Fact]
    public async Task Unknown_placement_is_rejected()
    {
        var port = new MouseRecordingPort();
        var live = Live(port, new ListProcessLog());
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_peer",
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];

        var missing = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.PlacementConnect,
                PlacementId = "plc_missing",
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Rejected, missing.Outcome);
        Assert.Equal("placement is not in the sidebar", missing.Error);

        var refused = await AttachSession.ApplySemanticActionAsync(
            port,
            live,
            tty: null,
            new AttachSemanticRequest
            {
                Action = AttachSemanticActions.PlacementConnect,
                PlacementId = "plc_peer",
                AttachClientId = live.AttachClientId,
            },
            CancellationToken.None);
        Assert.Equal(AttachSemanticOutcomes.Rejected, refused.Outcome);
        Assert.Equal("placement connect was not accepted", refused.Error);
        Assert.Null(live.ConnectedPlacementId);
    }

    private static AttachLiveState Live(MouseRecordingPort port, ListProcessLog sink)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "ws", "tab_one", "p1", "lease-r"),
            ProcessLog = sink,
            ChromeEnabled = false,
            PaneId = "p1",
            TabId = "tab_one",
            WorkspaceId = "ws",
            AttachClientId = "cli_test",
            SessionName = "default",
        };
    }

    private static JsonElement TabReply(string tabId = "tab_two") =>
        MouseTestGeom.Parse(
            $$"""{"tab_id":"{{tabId}}","workspace_id":"ws","focused_pane_id":"p1"}""");

    private static AttachClientRecord Record(string clientId, int pid, string socket) =>
        new()
        {
            AttachClientId = clientId,
            Socket = socket,
            Pid = pid,
        };

    private static string ShortDir()
    {
        var dir = Path.Combine("/tmp", "hsa" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ListProcessLog : IProcessLogSink
    {
        public List<ProcessLogRecord> Records { get; } = [];

        public bool IsEnabled(ProcessLogLevel level) => true;

        public bool Disabled => false;

        public string? Path => null;

        public void Write(ProcessLogRecord record) => Records.Add(record);
    }
}
